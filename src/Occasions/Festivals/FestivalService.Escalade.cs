using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals.Escalade;

namespace UnitSport.Occasions.Festivals;

// The Escalade on the festival service (#1351, protocol 77, docs/notes/occasions/festival-escalade.md): one request, "use the Marmite, the soup pot or the
// start line I stand at". The Marmite is smashed by the party's youngest and oldest account among those standing near it (the first to take it holds it
// for six seconds, the other joins; a lone player is both); everyone of the party near it is paid, once a day, and gets chocolate. The pot feeds every
// party member near it who has not eaten today. The start line begins the Course: the server follows the runner's position from the movement packets,
// ticks the six gates in order and the finish at the line, and pays by the time against the par. A bot takes ServeEscalade too (it is counted, never paid).
public partial class FestivalService
{
    public const int EscaladeMarmite = 0, EscaladePot = 1, EscaladeStart = 2;

    /// <summary>What an Escalade request or tick came to.</summary>
    public enum EscaladeEvent { Sync = 0, Held = 1, Smashed = 2, Ladled = 3, CourseStart = 4, Gate = 5, CourseDone = 6, CourseLost = 7 }

    private sealed class CourseRun
    {
        public double Start;
        public int Next;
    }

    private readonly Dictionary<(string Instance, long Peer), CourseRun> _courses = new();
    private readonly Dictionary<string, (long Peer, double At)> _holds = new();
    private readonly Dictionary<(string Instance, string Key), int> _fed = new();
    private double _nextEscaladeTick;

    /// <summary>The fastest believed run, m/s; <c>--escaladefast</c> (the checks, which teleport from gate to gate) lifts it.</summary>
    public static double EscaladeSpeedLimit { get; set; } = CmdArgs.Has("--escaladefast") ? 1e9 : EscaladeRules.SprintSpeed;

    /// <summary>Authority: smashes, bowls, finished runs and bot calls since start (checks).</summary>
    public int EscaladeSmashes { get; private set; }

    public int EscaladeBowls { get; private set; }

    public int EscaladeFinished { get; private set; }

    public int EscaladeBotActs { get; private set; }

    /// <summary>Client: the Escalade told this player something: what, the gate or bowl count, elapsed milliseconds, a line to show.</summary>
    public event Action<EscaladeEvent, int, int, string>? EscaladeTold;

    /// <summary>The Escalade, if it is on and held in this region.</summary>
    public (RunningFestival Run, ResolvedSite Site)? EscaladeNow() =>
        RunningOf(EscaladeRules.FestivalId) is { Site: { } site } run ? (run, site) : null;

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> uses the Marmite, the pot or the start line (<see cref="EscaladeMarmite"/>, <see cref="EscaladePot"/>,
    /// <see cref="EscaladeStart"/>) it stands at. Returns null when it counted, else why not (the peer is told too).
    /// </summary>
    public string? ServeEscalade(long peer, int target)
    {
        if (!Authority) return "Not here.";
        if (EscaladeNow() is not { } m || FestivalCatalog.Get(m.Run.Id) is not { } def) return Tell(peer, "The Escalade is not on.");
        if (target is < EscaladeMarmite or > EscaladeStart) return Tell(peer, "There is nothing like that here.");
        if (peer < 0) { EscaladeBotActs++; return null; }
        var (run, site) = m;
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return Tell(peer, "Go to the square.");
        return target switch
        {
            EscaladeMarmite => ServeSmash(peer, p, run, def, site, at.E, at.N),
            EscaladePot => ServeSoupe(peer, p, run, def, site, at.E, at.N),
            _ => ServeCourseStart(peer, run, site, at.E, at.N),
        };
    }

    // ---- the Marmite ---------------------------------------------------------------------------------------

    /// <summary>The humans of a peer's party standing within reach of a point (the peer itself always, if it is there).</summary>
    private List<(long Peer, PlayerProgress P)> CrewNear(long peer, PlayerProgress own, (double E, double N) point)
    {
        var list = new List<(long, PlayerProgress)> { (peer, own) };
        if (CrewKeys(peer) is { } keys)
            foreach (long m in CrewPeers(keys))
                if (m != peer && _adventure.Active(m) && _adventure.Progress(m) is { } mp
                    && _adventure.PositionOf?.Invoke(m) is { } at && EscaladeRules.Dist(at.E, at.N, point) <= EscaladeRules.CrewReach)
                    list.Add((m, mp));
        return list;
    }

    private string NameOf(long peer, PlayerProgress p) => NameOfPeer(peer) is { Length: > 0 } n ? n : Short(p.Key);

    private int PlayedToday(PlayerProgress p, RunningFestival run, FestivalDef def, string game) =>
        p.Get(FestivalBook.Section).Of(run.Instance, def.Id).Played.GetValueOrDefault(FestivalRules.PlayKey(game, run.Day));

    private string? ServeSmash(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, double e, double n)
    {
        if (!EscaladeRules.NearMarmite(site, e, n)) return Tell(peer, "Go to the Marmite first.");
        var crew = CrewNear(peer, p, EscaladeRules.MarmiteAt(site));
        var members = crew.Select(c => new EscaladeRules.Member(c.P.Key, c.P.Created, NameOf(c.Peer, c.P))).ToList();
        var (young, old) = EscaladeRules.Pair(members)!.Value;
        if (p.Key != young.Key && p.Key != old.Key)
            return Tell(peer, $"Only {young.Name} (the youngest) and {old.Name} (the oldest) can smash it.");
        if (PlayedToday(p, run, def, EscaladeRules.MarmiteGame) > 0) return Tell(peer, "You have smashed the Marmite today.");

        double now = GameClock.Now;
        if (young.Key != old.Key)
        {
            // two people: the first holds it, the other joins within the hold window
            string hold = $"{run.Instance}:{young.Key}:{old.Key}";
            if (!_holds.TryGetValue(hold, out var h) || h.Peer == peer || now - h.At > EscaladeRules.HoldSeconds)
            {
                _holds[hold] = (peer, now);
                string other = p.Key == young.Key ? old.Name : young.Name;
                NoteEscalade(peer, EscaladeEvent.Held, 0, 0, $"You hold the Marmite: {other} has {EscaladeRules.HoldSeconds:F0} s to take the other side.");
                foreach (var (cp, cpp) in crew)
                    if (cp != peer && (cpp.Key == young.Key || cpp.Key == old.Key))
                        NoteEscalade(cp, EscaladeEvent.Held, 0, 0, $"{NameOf(peer, p)} holds the Marmite: take the other side now!");
                return null;
            }
            _holds.Remove(hold);
        }

        // smashed: everyone of the party near it who has not had it today is paid, the pair a little more
        EscaladeSmashes++;
        foreach (var (cp, cpp) in crew)
        {
            if (PlayedToday(cpp, run, def, EscaladeRules.MarmiteGame) > 0) continue;
            bool pair = cpp.Key == young.Key || cpp.Key == old.Key;
            int granted = Award(cp, run.Id, EscaladeRules.SmashBatzen + (pair ? EscaladeRules.SmashPairBatzen : 0), "the Marmite smashed", EscaladeRules.MarmiteGame, EscaladeRules.SmashPoints);
            _adventure.Grant(cp, new Item(ItemId.MarmiteChocolate, EscaladeRules.ChocolatePieces), "escalade:marmite");
            NoteEscalade(cp, EscaladeEvent.Smashed, 0, 0, $"\"{EscaladeRules.Saying}\"  +{granted} Batzen and {EscaladeRules.ChocolatePieces} pieces of chocolate");
        }
        return null;
    }

    // ---- the soup pot --------------------------------------------------------------------------------------

    private string? ServeSoupe(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, double e, double n)
    {
        if (!EscaladeRules.NearPot(site, e, n)) return Tell(peer, "Go to the soup pot first.");
        var crew = CrewNear(peer, p, EscaladeRules.PotAt(site));
        int fed = 0;
        foreach (var (cp, cpp) in crew)
        {
            if (_fed.GetValueOrDefault((run.Instance, cpp.Key)) == run.Day) continue;
            _fed[(run.Instance, cpp.Key)] = run.Day;
            int granted = Award(cp, run.Id, EscaladeRules.SoupeBatzen, "a bowl of soupe", EscaladeRules.SoupeGame, EscaladeRules.SoupePoints);
            _adventure.Grant(cp, new Item(ItemId.SoupeEscalade, 1), "escalade:soupe");
            EscaladeBowls++;
            fed++;
            NoteEscalade(cp, EscaladeEvent.Ladled, fed, 0, cp == peer ? $"A bowl of soupe: +{granted} Batzen" : $"{NameOf(peer, p)} ladles you a bowl of soupe: +{granted} Batzen");
        }
        if (fed == 0) return Tell(peer, "Everyone here has eaten today.");
        if (crew.Count > 1) NoteEscalade(peer, EscaladeEvent.Ladled, fed, 0, $"The pot feeds {fed} of the party");
        return null;
    }

    // ---- the Course ----------------------------------------------------------------------------------------

    private string? ServeCourseStart(long peer, RunningFestival run, ResolvedSite site, double e, double n)
    {
        if (!EscaladeRules.NearStart(site, e, n)) return Tell(peer, "Go to the start line first.");
        _courses[(run.Instance, peer)] = new CourseRun { Start = GameClock.Now, Next = 0 };
        NoteEscalade(peer, EscaladeEvent.CourseStart, 0, 0, "Go! Six gates through the old town, then back to the line.");
        return null;
    }

    /// <summary>The runner's gates (every quarter second): the next point of the path within reach is passed; the line at the end finishes; too long is lost.</summary>
    private void EscaladeTick(double now)
    {
        if (_courses.Count == 0 || now < _nextEscaladeTick) return;
        _nextEscaladeTick = now + 0.25;
        var done = new List<(string, long)>();
        foreach (var (key, run) in _courses)
        {
            var (instance, peer) = key;
            if (EscaladeNow() is not { } m || m.Run.Instance != instance || !_adventure.Active(peer) || _adventure.Progress(peer) is not { } p)
            {
                done.Add(key);
                continue;
            }
            var site = m.Site;
            double elapsed = now - run.Start;
            if (elapsed > EscaladeRules.MaxSeconds(site))
            {
                done.Add(key);
                NoteEscalade(peer, EscaladeEvent.CourseLost, run.Next, (int)(elapsed * 1000), "Too slow: the Course is closed. Start again at the line.");
                continue;
            }
            if (_adventure.PositionOf?.Invoke(peer) is not { } at) continue;
            var path = EscaladeRules.Path(site);
            if (EscaladeRules.Dist(at.E, at.N, path[run.Next]) > EscaladeRules.GateReach) continue;
            run.Next++;
            if (run.Next <= EscaladeRules.Gates)
            {
                NoteEscalade(peer, EscaladeEvent.Gate, run.Next, (int)(elapsed * 1000),
                    run.Next == EscaladeRules.Gates ? "Last gate: back to the line!" : $"Gate {run.Next}/{EscaladeRules.Gates}");
                continue;
            }
            done.Add(key);
            FinishCourse(peer, p, m.Run, site, elapsed);
        }
        foreach (var key in done) _courses.Remove(key);
    }

    private void FinishCourse(long peer, PlayerProgress p, RunningFestival run, ResolvedSite site, double seconds)
    {
        double par = EscaladeRules.ParSeconds(site);
        if (!EscaladeRules.Plausible(site, seconds, EscaladeSpeedLimit))
        {
            NoteEscalade(peer, EscaladeEvent.CourseLost, EscaladeRules.Gates, (int)(seconds * 1000), "That was not a run: nobody sprints that fast.");
            return;
        }
        int batzen = EscaladeRules.CoursePay(seconds, par);
        int granted = Award(peer, run.Id, batzen, $"the Course in {seconds:F0} s (par {par:F0})", EscaladeRules.CourseGame, batzen * EscaladeRules.PointsPerBatzen);
        EscaladeFinished++;
        NoteEscalade(peer, EscaladeEvent.CourseDone, EscaladeRules.Gates, (int)(seconds * 1000), $"Course de l'Escalade in {seconds:F0} s (par {par:F0} s): +{granted} Batzen");
    }

    private void NoteEscalade(long peer, EscaladeEvent what, int a, int ms, string text)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellEscalade, (int)what, a, ms, text);
        else if (!_networked && peer > 0) TellEscalade((int)what, a, ms, text);
    }

    // ---- the owner's request ---------------------------------------------------------------------------

    /// <summary>Client: use the Marmite, the pot or the start line I stand at (<see cref="EscaladeMarmite"/> and the others). Offline the local player is the authority.</summary>
    public void AskEscalade(int target)
    {
        if (Authority) ServeEscalade(AdventureService.LocalPeer, target);
        else RpcId(1, MethodName.RequestEscalade, target);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestEscalade(int target)
    {
        if (_server) ServeEscalade(Multiplayer.GetRemoteSenderId(), target);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellEscalade(int what, int a, int ms, string text) => EscaladeTold?.Invoke((EscaladeEvent)what, a, ms, text);
}
