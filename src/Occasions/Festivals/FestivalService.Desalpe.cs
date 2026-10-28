using System.Text.Json;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Occasions.Festivals.Desalpe;

namespace UnitSport.Occasions.Festivals;

/// <summary>A herd that came down, as the fair's ring shows it.</summary>
public sealed record DesalpeEntryView(int Id, string Label, int Score, int Arrived, int Dressed, int Votes, byte[] Dress);

/// <summary>What every client knows of the Désalpe: the phase, the dress of each cow, the route's size, the herds in the ring.</summary>
public sealed record DesalpeView(string Festival, string Instance, int Phase, int Run, double Meters, double Par, byte[] Dress, double Elapsed, int Hands, DesalpeEntryView[] Ring);

// The Désalpe on the festival service (#1269 part 7, protocol 73): the server holds one herd per festival instance (a DesalpeRun: dressed on the
// alp, driven down by the people near it, set down at the fair), steps it at 10 Hz with the drivers' bodies, snapshots it at 8 Hz to the peers
// near it and pays the people who drove it through Award. Clients draw the snapshots (DesalpeHerd) and ask for three things: dress a cow, start
// the drive, vote for a herd. Bots take the same server methods; "farmhands" (server-run helpers that hold a flank) join a drive that has too
// few people and earn nothing.
public partial class FestivalService
{
    /// <summary>A server-run helper of the drive: a point that walks to its flank slot, a body to the herd's steering, nothing to the payout.</summary>
    private sealed class DesalpeHand
    {
        public long Peer;
        public double X, Y;
        public int Slot;
        public double Since;
    }

    private sealed class DesalpeSrv
    {
        public required string Instance { get; init; }
        public required string Festival { get; init; }
        public required ResolvedSite Site { get; init; }
        public required double Meters { get; init; }
        public required DesalpeRun Run { get; set; }
        public int Count = 1;
        public DesalpeVote Vote = new();
        public HashSet<long> Drivers = new();
        public HashSet<(long Peer, int Cow, int Run)> Paid = new();
        public List<DesalpeHand> Hands = new();
        public Dictionary<long, (double X, double Y)> Last = new();
        public double Acc, SnapAcc;
        public bool Settled;
    }

    private DesalpeSrv? _ds;
    private double _desalpeFair = DesalpeRules.FairSeconds;

    /// <summary>The Désalpe as this peer knows it (the server's own, or what it last told this client); null when none runs.</summary>
    public DesalpeView? DesalpeNow { get; private set; }

    /// <summary>The herd's last snapshot (client), or null.</summary>
    public byte[]? DesalpeSnapshot { get; private set; }

    /// <summary>Raised when <see cref="DesalpeNow"/> changes.</summary>
    public event Action? DesalpeChanged;

    /// <summary>Raised for each snapshot that arrives.</summary>
    public event Action<byte[]>? DesalpeSnapped;

    /// <summary>Client: the drive this player took part in was settled: score, Batzen granted, cows arrived, cows, seconds, cows dressed.</summary>
    public event Action<int, int, int, int, int, int>? DesalpeScored;

    /// <summary>Client: the vote counted (the entry), or was refused (0).</summary>
    public event Action<int>? DesalpeVoted;

    /// <summary>Authority (checks): the live herd of the festival, or null.</summary>
    internal DesalpeRun? DesalpeRunNow => _ds?.Run;

    /// <summary>Authority (checks): the last drive settled: its result and how many humans were paid.</summary>
    public (DesalpeResult Result, int Paid)? LastDesalpe { get; private set; }

    /// <summary>The route of the running festival's drive (the same on every peer), or null.</summary>
    public DesalpeRoute? DesalpeRouteNow()
    {
        if (RunningOf(DesalpeRules.FestivalId) is not { Site: { } site } || DesalpeNow is not { } v) return null;
        return DesalpeGround.RouteOf(site, v.Meters);
    }

    private static double RouteMeters() => FestivalCatalog.TestRequested ? DesalpeRules.TestRouteMeters : DesalpeRules.RouteMeters;

    // ---- the server's herd ------------------------------------------------------------------------

    private DesalpeSrv? DesalpeEnsure()
    {
        if (RunningOf(DesalpeRules.FestivalId) is not { Site: { } site } run)
        {
            if (_ds != null)
            {
                _ds = null;
                DesalpeNow = null;
                DesalpeChanged?.Invoke();
            }
            return null;
        }
        if (_ds != null && _ds.Instance == run.Instance && _ds.Site == site) return _ds;
        double meters = RouteMeters();
        _ds = new DesalpeSrv
        {
            Instance = run.Instance, Festival = run.Id, Site = site, Meters = meters,
            Run = new DesalpeRun(DesalpeGround.SeedOf(site), 1, meters),
        };
        _desalpeFair = FestivalCatalog.TestRequested ? 6 : DesalpeRules.FairSeconds;
        GD.Print($"[desalpe] {run.Instance} at {site.Name}: a herd of {DesalpeRules.Cows} on the alp, a {_ds.Run.Route.Length:F0} m drive to the fair");
        DesalpeBroadcast(_ds);
        return _ds;
    }

    private void DesalpeTick(double delta)
    {
        if (Running.Count == 0 && _ds == null) return;
        if (DesalpeEnsure() is not { } s) return;
        s.Acc += delta;
        s.SnapAcc += delta;
        const float Step = 0.1f;
        int guard = 0;
        while (s.Acc >= Step && guard++ < 20)
        {
            s.Acc -= Step;
            DesalpeStep(s, Step);
        }
        if (s.Acc > Step) s.Acc = 0;
        double every = s.Run.Phase == DesalpePhase.Driving || (s.Run.Phase == DesalpePhase.Arrived && s.Run.AtFair < 20) ? 0.125 : 1.0;
        if (s.SnapAcc >= every)
        {
            s.SnapAcc = 0;
            DesalpeSnap(s);
        }
    }

    private void DesalpeStep(DesalpeSrv s, float dt)
    {
        var run = s.Run;
        if (run.Phase == DesalpePhase.Grazing) return;
        // the drivers: every adventuring body, a mounted one with the wide flight zone, and the farmhands
        var drivers = new List<Driver>(8);
        var flock = run.Flock;
        var (cx, cy) = flock.Centroid();
        foreach (long peer in _adventure.Peers.ToArray())
        {
            if (peer <= 0 || !_adventure.Active(peer) || _adventure.PositionOf?.Invoke(peer) is not { } at) continue;
            double x = at.E - s.Site.E, y = at.N - s.Site.N;
            float zone = DesalpeRules.ZoneFoot;
            if (s.Last.TryGetValue(peer, out var was) && Math.Sqrt((x - was.X) * (x - was.X) + (y - was.Y) * (y - was.Y)) / dt > 3.5) zone = DesalpeRules.ZoneMounted;
            s.Last[peer] = (x, y);
            drivers.Add(new Driver(x, y, zone));
            if (run.Phase == DesalpePhase.Driving
                && (Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) <= DesalpeRules.DriverReach || Math.Sqrt((x - flock.X[0]) * (x - flock.X[0]) + (y - flock.Y[0]) * (y - flock.Y[0])) <= DesalpeRules.DriverReach))
                s.Drivers.Add(peer);
        }
        foreach (var h in s.Hands)
        {
            MoveHand(s, h, dt);
            drivers.Add(new Driver(h.X, h.Y, DesalpeRules.ZoneFoot));
        }
        if (run.Tick(dt, drivers)) DesalpeSettle(s);
        else if (run.Phase == DesalpePhase.Arrived && run.AtFair >= _desalpeFair)
        {
            // a fresh herd goes up to the alp for the next drive; the dress stays
            s.Count++;
            s.Run = new DesalpeRun(DesalpeGround.SeedOf(s.Site), s.Count, s.Meters, run.Dress);
            s.Drivers.Clear();
            s.Hands.Clear();
            s.Settled = false;
            GD.Print($"[desalpe] herd {s.Count} is up on the alp");
            DesalpeBroadcast(s);
        }
    }

    /// <summary>A farmhand walks to its flank slot at the pace of a man herding: the rear, a flank in the field, or the far side of a stray.</summary>
    private static void MoveHand(DesalpeSrv s, DesalpeHand h, float dt)
    {
        var f = s.Run.Flock;
        h.Since += dt;
        var (cx, cy) = f.Centroid();
        var (tx, ty) = f.Route.Tangent(f.LeaderS);
        double nx = -ty, ny = tx, gx, gy;
        // a stray is theirs after a short while (a man needs to notice it); the second one goes after it, the first holds the rear
        int stray = -1;
        double best = double.MaxValue;
        if (h.Slot % 2 == 1 && h.Since > 6)
            for (int i = 1; i < f.Count; i++)
            {
                if (f.State[i] != CowState.Stray) continue;
                double d = f.Distance(i, h.X, h.Y);
                if (d < best) { best = d; stray = i; }
            }
        if (stray >= 0)
        {
            f.Route.Project(f.X[stray], f.Y[stray], out _);
            double sArc = f.Route.Project(f.X[stray], f.Y[stray], out _);
            var (rx, ry) = f.Route.At(sArc);
            double ox = f.X[stray] - rx, oy = f.Y[stray] - ry, ol = Math.Max(Math.Sqrt(ox * ox + oy * oy), 0.1);
            gx = f.X[stray] + ox / ol * 3.0; gy = f.Y[stray] + oy / ol * 3.0;
        }
        else if (h.Slot % 2 == 1) { gx = cx - tx * 3 + nx * 12; gy = cy - ty * 3 + ny * 12; }
        else { gx = cx - tx * 9; gy = cy - ty * 9; }
        double dx = gx - h.X, dy = gy - h.Y, dl = Math.Sqrt(dx * dx + dy * dy);
        double move = Math.Min(dl, 1.8 * dt);
        if (dl > 1e-6) { h.X += dx / dl * move; h.Y += dy / dl * move; }
    }

    private void DesalpeSettle(DesalpeSrv s)
    {
        var r = s.Run.Result!;
        int batzen = DesalpeRules.BatzenFor(r.Score);
        var keys = new List<string>();
        int paid = 0;
        foreach (long peer in s.Drivers.ToArray())
        {
            if (_adventure.KeyOf(peer) is { } k) keys.Add(k);
            int granted = Award(peer, s.Festival, batzen, $"drove the herd down, {r.Score} points ({r.Arrived}/{r.Total} cows)", DesalpeRules.DriveGame, r.Score);
            paid++;
            if (Connected(peer)) RpcId(peer, MethodName.TellDesalpeResult, r.Score, granted, r.Arrived, r.Total, r.Seconds, r.Dressed);
            else if (!_networked && peer > 0) TellDesalpeResult(r.Score, granted, r.Arrived, r.Total, r.Seconds, r.Dressed);
        }
        if (r.Arrived > 0 && keys.Count > 0) s.Vote.Add($"Herd {s.Count}", r, s.Run.Dress, keys);
        s.Hands.Clear();
        s.Settled = true;
        LastDesalpe = (r, paid);
        GD.Print($"[desalpe] herd {s.Count} down: {r.Arrived}/{r.Total} cows, {r.Lost} lost, {r.Seconds} s (par {s.Run.Par:F0}), {r.Score} points, {paid} driver(s) paid {batzen} Batzen each at most");
        DesalpeBroadcast(s);
    }

    // ---- what the server tells ----------------------------------------------------------------------

    private DesalpeView ViewOf(DesalpeSrv s) => new(s.Festival, s.Instance, (int)s.Run.Phase, s.Count, s.Meters, s.Run.Par, s.Run.Dress, s.Run.Elapsed, s.Hands.Count,
        s.Vote.Standings().Select(e => new DesalpeEntryView(e.Id, e.Label, e.Score, e.Arrived, e.Dressed, e.Votes, e.Dress)).ToArray());

    private void DesalpeBroadcast(DesalpeSrv s)
    {
        var view = ViewOf(s);
        DesalpeNow = view;
        DesalpeChanged?.Invoke();
        if (!_networked) return;
        string json = JsonSerializer.Serialize(view, _json);
        foreach (long peer in _adventure.Peers.ToArray())
            if (peer > 0 && Connected(peer)) RpcId(peer, MethodName.TellDesalpe, json);
    }

    private void DesalpeWelcome(long peer)
    {
        if (_ds == null || !Connected(peer)) return;
        RpcId(peer, MethodName.TellDesalpe, JsonSerializer.Serialize(ViewOf(_ds), _json));
        RpcId(peer, MethodName.TellDesalpeSnap, DesalpeWire.Pack(_ds.Run.Phase, _ds.Run.Flock));
    }

    private void DesalpeSnap(DesalpeSrv s)
    {
        var bytes = DesalpeWire.Pack(s.Run.Phase, s.Run.Flock);
        if (!_networked)
        {
            TellDesalpeSnap(bytes);
            return;
        }
        var (cx, cy) = s.Run.Flock.Centroid();
        foreach (long peer in _adventure.Peers.ToArray())
        {
            if (peer <= 0 || !Connected(peer) || _adventure.PositionOf?.Invoke(peer) is not { } at) continue;
            double x = at.E - s.Site.E - cx, y = at.N - s.Site.N - cy;
            if (x * x + y * y <= 500 * 500) RpcId(peer, MethodName.TellDesalpeSnap, bytes);
        }
    }

    // ---- the three requests (a human's, a bot's) -----------------------------------------------------

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> puts <paramref name="dress"/> on cow <paramref name="cow"/> while the herd is on the alp.
    /// A body must stand within 6 m of the cow (a bot has none). A bare cow dressed for the first time in a run pays a little. Null when it counted.
    /// </summary>
    public string? ServeDesalpeDress(long peer, int cow, int dress)
    {
        if (!Authority) return "Not here.";
        if (DesalpeEnsure() is not { } s || RunningOf(s.Festival) is not { } run || FestivalCatalog.Get(s.Festival) is not { } def) return Tell(peer, "There is no herd to dress now.");
        if (!def.IsOpen(DesalpeRules.DressGame, run.Day)) return Tell(peer, "The herd is not on the alp yet.");
        if (peer > 0 && !_adventure.Active(peer)) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (s.Run.Phase != DesalpePhase.Grazing) return Tell(peer, "The herd is on its way: dress the next one.");
        if (cow < 0 || cow >= s.Run.Dress.Length || dress is < 0 or > 255 || !DesalpeRules.Valid((byte)dress)) return Tell(peer, "That cow cannot wear that.");
        if (peer > 0)
        {
            if (_adventure.PositionOf?.Invoke(peer) is not { } at) return Tell(peer, "Where are you?");
            double x = at.E - s.Site.E - s.Run.Flock.X[cow], y = at.N - s.Site.N - s.Run.Flock.Y[cow];
            if (x * x + y * y > (DesalpeRules.DressReach + 3) * (DesalpeRules.DressReach + 3)) return Tell(peer, "Go to the cow to dress it.");
        }
        byte was = s.Run.Dress[cow];
        if (!s.Run.TryDress(cow, (byte)dress)) return Tell(peer, "That cow cannot wear that.");
        if (was == 0 && dress != 0 && peer > 0 && s.Paid.Add((peer, cow, s.Count)))
            Award(peer, s.Festival, DesalpeRules.DressBatzen, $"dressed cow {cow}", DesalpeRules.DressGame, DesalpeRules.DressPoints);
        DesalpeBroadcast(s);
        return null;
    }

    /// <summary>
    /// Server (and a bot's way in): starts the drive. Needs the drive open (day 3), the herd on the alp and a body at the alp sign. A drive with fewer than
    /// two people near the herd gets farmhands to make up the two flanks. Null when it started.
    /// </summary>
    public string? ServeDesalpeStart(long peer)
    {
        if (!Authority) return "Not here.";
        if (DesalpeEnsure() is not { } s || RunningOf(s.Festival) is not { } run || FestivalCatalog.Get(s.Festival) is not { } def) return Tell(peer, "There is no herd to drive now.");
        if (!def.IsOpen(DesalpeRules.DriveGame, run.Day)) return Tell(peer, $"The Alpabzug starts on day {def.OpensOn(DesalpeRules.DriveGame)}.");
        if (peer > 0 && !_adventure.Active(peer)) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (s.Run.Phase != DesalpePhase.Grazing) return Tell(peer, "The herd is already on its way.");
        var sign = s.Run.Route.Start;
        int near = 0;
        bool starter = peer < 0;
        foreach (long p in _adventure.Peers.ToArray())
        {
            if (p <= 0 || !_adventure.Active(p) || _adventure.PositionOf?.Invoke(p) is not { } at) continue;
            double x = at.E - s.Site.E - sign.X, y = at.N - s.Site.N - sign.Y;
            double d2 = x * x + y * y;
            if (d2 <= (DesalpeRules.StartReach + 40) * (DesalpeRules.StartReach + 40)) near++;
            if (p == peer && d2 <= (DesalpeRules.StartReach + 5) * (DesalpeRules.StartReach + 5)) starter = true;
        }
        if (!starter) return Tell(peer, "Go to the alp sign to start the drive.");
        s.Run.Start();
        s.Drivers.Clear();
        s.Settled = false;
        // two flanks and a rear need hands: the farmhands make up what the people near the herd do not
        for (int i = s.Hands.Count; i < Math.Max(0, 2 - near); i++) AddHand(s, -9001 - i);
        GD.Print($"[desalpe] herd {s.Count} starts down with {near} person(s) and {s.Hands.Count} farmhand(s)");
        DesalpeBroadcast(s);
        return null;
    }

    /// <summary>A bot (a check, a brain) joins the drive as a farmhand of its own: it holds a flank and earns nothing. Null when it joined.</summary>
    public string? ServeDesalpeBot(long botPeer)
    {
        if (!Authority) return "Not here.";
        if (DesalpeEnsure() is not { } s) return "There is no herd.";
        if (s.Hands.Count >= 4) return "The herd has hands enough.";
        if (s.Hands.Any(h => h.Peer == botPeer)) return "That hand is already with the herd.";
        AddHand(s, botPeer);
        DesalpeBroadcast(s);
        return null;
    }

    private static void AddHand(DesalpeSrv s, long peer)
    {
        var f = s.Run.Flock;
        var (tx, ty) = f.Route.Tangent(0);
        var (cx, cy) = f.Centroid();
        s.Hands.Add(new DesalpeHand { Peer = peer, Slot = s.Hands.Count, X = cx - tx * 14, Y = cy - ty * 14 });
    }

    /// <summary>
    /// Server: <paramref name="peer"/> gives its one vote to a herd in the fair's ring. Needs the vote open (day 4), a human, a body at the ring, a herd it did
    /// not drive, no vote yet this festival. Pays a little. Null when it counted.
    /// </summary>
    public string? ServeDesalpeVote(long peer, int entry)
    {
        if (!Authority) return "Not here.";
        if (peer <= 0) return "Only people vote.";
        if (DesalpeEnsure() is not { } s || RunningOf(s.Festival) is not { } run || FestivalCatalog.Get(s.Festival) is not { } def) return Tell(peer, "There is no fair now.");
        if (!def.IsOpen(DesalpeRules.VoteGame, run.Day)) return Tell(peer, $"The vote opens on day {def.OpensOn(DesalpeRules.VoteGame)}.");
        if (!_adventure.Active(peer) || _adventure.KeyOf(peer) is not { } key) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at || s.Site.DistanceTo(at.E, at.N) > DesalpeRules.VoteReach + 10) return Tell(peer, "Go to the fair's ring to vote.");
        if (s.Vote.TryVote(key, entry) is { } why) return Tell(peer, why);
        Award(peer, s.Festival, DesalpeRules.VoteBatzen, $"voted for herd {entry}", DesalpeRules.VoteGame, DesalpeRules.VotePoints);
        if (Connected(peer)) RpcId(peer, MethodName.TellDesalpeVoted, entry);
        else if (!_networked) TellDesalpeVoted(entry);
        DesalpeBroadcast(s);
        return null;
    }

    // ---- the owner's requests ---------------------------------------------------------------------

    /// <summary>Client: dress a cow (offline the local player is the authority).</summary>
    public void AskDesalpeDress(int cow, byte dress)
    {
        if (Authority) ServeDesalpeDress(AdventureService.LocalPeer, cow, dress);
        else RpcId(1, MethodName.RequestDesalpeDress, cow, (int)dress);
    }

    public void AskDesalpeStart()
    {
        if (Authority) ServeDesalpeStart(AdventureService.LocalPeer);
        else RpcId(1, MethodName.RequestDesalpeStart);
    }

    public void AskDesalpeVote(int entry)
    {
        if (Authority) ServeDesalpeVote(AdventureService.LocalPeer, entry);
        else RpcId(1, MethodName.RequestDesalpeVote, entry);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDesalpeDress(int cow, int dress)
    {
        if (_server) ServeDesalpeDress(Multiplayer.GetRemoteSenderId(), cow, dress);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDesalpeStart()
    {
        if (_server) ServeDesalpeStart(Multiplayer.GetRemoteSenderId());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDesalpeVote(int entry)
    {
        if (_server) ServeDesalpeVote(Multiplayer.GetRemoteSenderId(), entry);
    }

    // ---- server -> owner --------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellDesalpe(string json)
    {
        try
        {
            DesalpeNow = JsonSerializer.Deserialize<DesalpeView>(json, _json);
        }
        catch (JsonException e)
        {
            GD.PushWarning($"[desalpe] unreadable state from the server: {e.Message}");
            return;
        }
        DesalpeChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void TellDesalpeSnap(byte[] bytes)
    {
        DesalpeSnapshot = bytes;
        DesalpeSnapped?.Invoke(bytes);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellDesalpeResult(int score, int batzen, int arrived, int total, int seconds, int dressed) =>
        DesalpeScored?.Invoke(score, batzen, arrived, total, seconds, dressed);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellDesalpeVoted(int entry) => DesalpeVoted?.Invoke(entry);
}
