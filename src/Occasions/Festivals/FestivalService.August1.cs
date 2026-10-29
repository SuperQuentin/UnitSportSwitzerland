using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals.August1;

namespace UnitSport.Occasions.Festivals;

// The 1 August on the festival service (#1355, protocol bump at ready, docs/notes/occasions/festival-august1.md): one request, "use the lampion stand, the
// fireworks stand, the walk's post I stand at, or the box of fireworks in my hand". The stand gives one lampion a day; the fireworks stand sells a box for
// Batzen (three a day); the post starts the Lampionumzug: the party members near it that carry today's lampion, and the village children when it is short
// of three, walk the ring in order at night; the server follows the leader's position and counts a waypoint only when everyone is within 30 m of the leader.
// A lit box is a volley over the lighter, told to everyone in range. The lake fireworks ride the running list (ShowOf, below); watching one pays once.
// A bot takes ServeAugust1 too (it is counted, never paid).
public partial class FestivalService
{
    public const int August1LampionStand = 0, August1FireworkStand = 1, August1Post = 2, August1Light = 3;

    /// <summary>What a 1 August request or tick came to.</summary>
    public enum August1Event { Sync = 0, Lampion = 1, Bought = 2, WalkStart = 3, Waypoint = 4, Waiting = 5, WalkDone = 6, WalkLost = 7, Lit = 8, Watched = 9 }

    private sealed class WalkRun
    {
        public long Leader;
        public List<long> Members = new();
        public int Children;
        public double Start, NextWait;
        public int Next;
    }

    private readonly Dictionary<(string Instance, long Leader), WalkRun> _walks = new();
    private readonly Dictionary<(string Instance, string Key), int> _lamps = new();
    private readonly Dictionary<(string Instance, string Key), (int Day, int Count)> _boxes = new();
    private readonly Dictionary<long, double> _ownAt = new();
    private int _ownCount;
    private double _nextWalkTick;

    /// <summary>Seconds between the server deciding a player's own volley and its first shell: time for the news to reach the others.</summary>
    public const double OwnLead = 3;

    /// <summary>The fastest believed loop, m/s; <c>--august1fast</c> (the checks, which teleport) lifts it.</summary>
    public static double August1SpeedLimit { get; set; } = CmdArgs.Has("--august1fast") ? 1e9 : August1Rules.RunSpeed;

    /// <summary>Authority: lampions given, boxes bought, finished walks, volleys lit, shows watched and bot calls since start (checks).</summary>
    public int August1Lampions { get; private set; }

    public int August1Boxes { get; private set; }

    public int August1Walks { get; private set; }

    public int August1Volleys { get; private set; }

    public int August1BotActs { get; private set; }

    /// <summary>Client: the 1 August told this player something: what, a count (waypoint, walkers), elapsed milliseconds (or children), a line to show.</summary>
    public event Action<August1Event, int, int, string>? August1Told;

    /// <summary>Client: a volley is to be played: the server's start, the seed, the lighter's LV95 position.</summary>
    public event Action<double, int, double, double>? OwnShowTold;

    /// <summary>The 1 August, if it is on and held in this region.</summary>
    public (RunningFestival Run, ResolvedSite Site)? August1Now() =>
        RunningOf(August1Rules.FestivalId) is { Site: { } site } run ? (run, site) : null;

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> uses the lampion stand, the fireworks stand, the walk's post (<see cref="August1LampionStand"/> and the
    /// others) it stands at, or lights the box of fireworks it holds (<see cref="August1Light"/>, anywhere). Returns null when it counted, else why not (the peer is told too).
    /// </summary>
    public string? ServeAugust1(long peer, int target)
    {
        if (!Authority) return "Not here.";
        if (August1Now() is not { } m || FestivalCatalog.Get(m.Run.Id) is not { } def) return Tell(peer, "The 1 August is not on.");
        if (target is < August1LampionStand or > August1Light) return Tell(peer, "There is nothing like that here.");
        if (peer < 0) { August1BotActs++; return null; }
        var (run, site) = m;
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return Tell(peer, "Go to the village.");
        return target switch
        {
            August1LampionStand => ServeLampion(peer, p, run, site, at.E, at.N),
            August1FireworkStand => ServeBox(peer, p, run, def, site, at.E, at.N),
            August1Post => ServeWalkStart(peer, p, run, site, at.E, at.N),
            _ => ServeLight(peer, run, at.E, at.N),
        };
    }

    // ---- the stands ------------------------------------------------------------------------------------------

    private string? ServeLampion(long peer, PlayerProgress p, RunningFestival run, ResolvedSite site, double e, double n)
    {
        if (!August1Rules.NearLampionStand(site, e, n)) return Tell(peer, "Go to the lampion stand first.");
        var key = (run.Instance, p.Key);
        if (_lamps.GetValueOrDefault(key) == run.Day) return Tell(peer, "You have your lampion for today.");
        _lamps[key] = run.Day;
        _adventure.Grant(peer, new Item(ItemId.Lampion, 1), "august1:lampion");
        August1Lampions++;
        NoteAugust1(peer, August1Event.Lampion, 1, 0, "A lampion: hold it in your hand, and take it round the village loop with two friends at night.");
        return null;
    }

    private string? ServeBox(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, double e, double n)
    {
        if (!August1Rules.NearFireworkStand(site, e, n)) return Tell(peer, "Go to the fireworks stand first.");
        var key = (run.Instance, p.Key);
        var (day, count) = _boxes.GetValueOrDefault(key);
        if (day != run.Day) count = 0;
        if (count >= August1Rules.BoxesPerDay) return Tell(peer, $"The stand sells {August1Rules.BoxesPerDay} boxes a day to one person.");
        var inst = p.Get(FestivalBook.Section).Of(run.Instance, def.Id);
        if (!FestivalRules.Spend(inst, August1Rules.BoxPrice)) return Tell(peer, $"A box of fireworks costs {August1Rules.BoxPrice} Batzen; you hold {inst.Batzen}.");
        _boxes[key] = (run.Day, count + 1);
        _adventure.Touch(peer, FestivalBook.Section);
        _adventure.Grant(peer, new Item(ItemId.FireworkBox, 1), "august1:box");
        August1Boxes++;
        GD.Print($"[festival] {Short(p.Key)} bought a box of fireworks for {August1Rules.BoxPrice} Batzen ({inst.Batzen} left)");
        NoteAugust1(peer, August1Event.Bought, count + 1, 0, $"A box of fireworks: -{August1Rules.BoxPrice} Batzen. Light it at night with the use key.");
        return null;
    }

    // ---- the box ------------------------------------------------------------------------------------------------

    private string? ServeLight(long peer, RunningFestival run, double e, double n)
    {
        if (!August1Rules.IsNight(Hour())) return Tell(peer, "Fireworks are for the night: after 20:30.");
        double now = GameClock.Now;
        if (_ownAt.TryGetValue(peer, out double last) && now - last < August1Rules.OwnCooldown)
            return Tell(peer, $"Wait {August1Rules.OwnCooldown - (now - last):F0} s before the next box.");
        _ownAt[peer] = now;
        double start = Net.ClockSync.SimNow + OwnLead;
        int seed = August1Rules.OwnSeed(run.Instance, ++_ownCount);
        August1Volleys++;
        GD.Print($"[festival] a volley #{_ownCount} is lit at {e:F0},{n:F0}");
        foreach (long other in _adventure.Peers.ToArray())
        {
            if (other <= 0) continue;
            if (_adventure.PositionOf?.Invoke(other) is { } at && August1Rules.Dist(at.E, at.N, (e, n)) > August1Rules.OwnRange) continue;
            if (Connected(other)) RpcId(other, MethodName.ShowOwn, start, seed, e, n);
            else if (!_networked) ShowOwn(start, seed, e, n);
        }
        NoteAugust1(peer, August1Event.Lit, 0, 0, "The fireworks are lit: stand back.");
        return null;
    }

    // ---- the lampion walk -------------------------------------------------------------------------------------------

    private string? ServeWalkStart(long peer, PlayerProgress p, RunningFestival run, ResolvedSite site, double e, double n)
    {
        if (!August1Rules.NearPost(site, e, n)) return Tell(peer, "Go to the lampion post first.");
        if (!August1Rules.IsNight(Hour())) return Tell(peer, "The lampion walk starts after dark: from 20:30.");
        if (_lamps.GetValueOrDefault((run.Instance, p.Key)) != run.Day) return Tell(peer, "Take a lampion at the stand first.");
        var members = new List<long> { peer };
        foreach (var (cp, cpp) in CrewNear(peer, p, August1Rules.PostAt(site)))
            if (cp != peer && _lamps.GetValueOrDefault((run.Instance, cpp.Key)) == run.Day) members.Add(cp);
        int children = Math.Max(0, August1Rules.Needed - members.Count);
        _walks[(run.Instance, peer)] = new WalkRun { Leader = peer, Members = members, Children = children, Start = GameClock.Now, Next = 0 };
        foreach (long member in members)
            NoteAugust1(member, August1Event.WalkStart, members.Count, children,
                children > 0 ? $"The lampion walk begins: {members.Count} of you and {children} village {(children == 1 ? "child" : "children")}. Ten stops round the village, then back to the post; stay together."
                             : $"The lampion walk begins: {members.Count} of you. Ten stops round the village, then back to the post; stay together.");
        return null;
    }

    /// <summary>Every half second: the leader's position passes the next waypoint if everyone is within reach of the leader; the post at the end finishes.</summary>
    private void August1Tick(double now)
    {
        if (_walks.Count == 0 || now < _nextWalkTick) return;
        _nextWalkTick = now + 0.5;
        var done = new List<(string, long)>();
        foreach (var (key, walk) in _walks)
        {
            var (instance, leader) = key;
            if (August1Now() is not { } m || m.Run.Instance != instance || !_adventure.Active(leader) || _adventure.Progress(leader) is not { } lp)
            {
                done.Add(key);
                continue;
            }
            var site = m.Site;
            double elapsed = now - walk.Start;
            if (elapsed > August1Rules.MaxSeconds(site) || !August1Rules.IsNight(Hour()))
            {
                done.Add(key);
                foreach (long member in walk.Members)
                    NoteAugust1(member, August1Event.WalkLost, walk.Next, (int)(elapsed * 1000),
                        elapsed > August1Rules.MaxSeconds(site) ? "Too slow: the lanterns burnt down. Start again at the post." : "Dawn: the lanterns go out.");
                continue;
            }
            if (_adventure.PositionOf?.Invoke(leader) is not { } at) continue;
            var path = August1Rules.Path(site);
            if (August1Rules.Dist(at.E, at.N, path[walk.Next]) > August1Rules.WaypointReach) continue;

            // everyone who walks must be with the leader
            var behind = new List<long>();
            foreach (long member in walk.Members)
                if (member != leader && (!_adventure.Active(member) || _adventure.PositionOf?.Invoke(member) is not { } mp
                    || August1Rules.Dist(mp.E, mp.N, (at.E, at.N)) > August1Rules.Together)) behind.Add(member);
            if (behind.Count > 0)
            {
                if (now >= walk.NextWait)
                {
                    walk.NextWait = now + 4;
                    foreach (long member in walk.Members)
                        NoteAugust1(member, August1Event.Waiting, behind.Count, 0, "Wait for the others: a walk is made together.");
                }
                continue;
            }
            walk.Next++;
            foreach (long member in walk.Members)
                NoteAugust1(member, August1Event.Waypoint, walk.Next, (int)(elapsed * 1000),
                    walk.Next == August1Rules.Waypoints ? "Back to the post!" : $"Stop {walk.Next}/{August1Rules.Waypoints}");
            if (walk.Next <= August1Rules.Waypoints) continue;
            done.Add(key);
            FinishWalk(walk, m.Run, site, elapsed);
        }
        foreach (var key in done) _walks.Remove(key);
    }

    private void FinishWalk(WalkRun walk, RunningFestival run, ResolvedSite site, double seconds)
    {
        if (!August1Rules.Plausible(site, seconds, August1SpeedLimit))
        {
            foreach (long member in walk.Members)
                NoteAugust1(member, August1Event.WalkLost, August1Rules.Waypoints, (int)(seconds * 1000), "That was not a walk: nobody covers the loop that fast.");
            return;
        }
        var humans = walk.Members.Where(mb => _adventure.Active(mb)).ToList();
        int pay = August1Rules.WalkPay(humans.Count);
        August1Walks++;
        foreach (long member in humans)
        {
            if (_adventure.Progress(member) is not { } mp) continue;
            if (PlayedToday(mp, run, FestivalCatalog.Get(run.Id)!, August1Rules.LampionGame) > 0)
            {
                NoteAugust1(member, August1Event.WalkDone, humans.Count, (int)(seconds * 1000), "Another lovely walk. You have been paid for today's.");
                continue;
            }
            int granted = Award(member, run.Id, pay, $"the lampion walk with {humans.Count}", August1Rules.LampionGame, pay * August1Rules.PointsPerBatzen);
            NoteAugust1(member, August1Event.WalkDone, humans.Count, (int)(seconds * 1000),
                $"The lampion walk is done in {seconds / 60:F0} min with {humans.Count} of you: +{granted} Batzen");
        }
    }

    // ---- watching a lake show -----------------------------------------------------------------------------------------

    /// <summary>The watch rule (20 s within 1.5 km of the site while a show plays) fired for a player of the 1 August: it pays once.</summary>
    private void WatchedAugust1(long peer, PlayerProgress p, RunningFestival run)
    {
        int granted = Award(peer, run.Id, August1Rules.ShowBatzen, "watched the lake fireworks", August1Rules.ShowGame, August1Rules.ShowPoints);
        NoteAugust1(peer, August1Event.Watched, 0, 0, $"The fireworks over the lake: +{granted} Batzen");
    }

    // ---- telling ------------------------------------------------------------------------------------------------------

    private void NoteAugust1(long peer, August1Event what, int a, int ms, string text)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellAugust1, (int)what, a, ms, text);
        else if (!_networked && peer > 0) TellAugust1((int)what, a, ms, text);
    }

    // ---- the owner's request ---------------------------------------------------------------------------------------------

    /// <summary>Client: use the lampion stand, the fireworks stand or the post I stand at, or light the box in my hand (<see cref="August1LampionStand"/> and the others). Offline the local player is the authority.</summary>
    public void AskAugust1(int target)
    {
        if (Authority) ServeAugust1(AdventureService.LocalPeer, target);
        else RpcId(1, MethodName.RequestAugust1, target);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAugust1(int target)
    {
        if (_server) ServeAugust1(Multiplayer.GetRemoteSenderId(), target);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellAugust1(int what, int a, int ms, string text) => August1Told?.Invoke((August1Event)what, a, ms, text);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShowOwn(double start, int seed, double e, double n) => OwnShowTold?.Invoke(start, seed, e, n);
}
