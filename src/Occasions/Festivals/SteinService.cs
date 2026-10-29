using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Occasions.Festivals;

/// <summary>An attempt the server opened: the seed of its meter, the stone's class, when it began (server time) and where it is held.</summary>
public sealed record SteinAttempt(int Id, int Seed, int Class, double Begun, string Festival);

/// <summary>A put that landed, as every peer near the ground is told it. <see cref="Foul"/> is null for a throw.</summary>
public sealed record SteinLanding(long Thrower, string Name, int Attempt, int Class, double Metres, double Angle, double Speed, double Power,
    int Batzen, int Points, string? Foul, double E, double N, float Yaw);

/// <summary>
/// Steinstossen over the wire (#1269 part 3), at <c>World/Adventure/Festivals/Stein</c> on the server and every client. The
/// server opens an attempt (a seed and a start time), the client plays it and sends only the six press stamps and the camera's
/// pitch; the server checks them (<see cref="SteinRules.Implausible"/>), works out the meter, the grades, the power and the
/// distance (<see cref="SteinRules.Resolve"/>), pays through <see cref="FestivalService.Award"/> and tells everyone within
/// <see cref="HearM"/> of the ground where the stone fell. A bot takes the same path (<see cref="ServeBot"/>): it earns nothing, it
/// makes up the field. Offline the client plays the server's part. Docs: <c>docs/notes/occasions/festival-steinstossen.md</c>.
/// </summary>
public partial class SteinService : Node
{
    public const string NodeName = "Stein";

    /// <summary>Who is told of a landing: everyone with a body this near the stone, m.</summary>
    public const double HearM = 300;

    public static SteinService? Instance { get; private set; }

    private FestivalService _festivals = null!;
    private AdventureService _adventure = null!;
    private bool _server, _networked;
    private readonly Dictionary<long, SteinAttempt> _open = new();
    private readonly Dictionary<long, (long Second, int Count)> _rate = new();
    private int _nextId;

    /// <summary>Authority: the landings since the start (checks).</summary>
    public int Throws { get; private set; }

    /// <summary>Client: an attempt began: its id, seed, class and start (server time).</summary>
    public event Action<SteinAttempt>? Began;

    /// <summary>Client: a put landed near this client (its own included).</summary>
    public event Action<SteinLanding>? Landed;

    /// <summary>Client: the server said no: why.</summary>
    public event Action<string>? Refused;

    public static SteinService Create(FestivalService festivals, AdventureService adventure, bool server, bool networked)
    {
        var s = new SteinService { Name = NodeName, _festivals = festivals, _adventure = adventure, _server = server, _networked = networked };
        festivals.AddChild(s);
        return s;
    }

    private bool Authority => _server || !_networked;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Connected(long peer) => _networked && peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    // ---- where and when ------------------------------------------------------------------------

    /// <summary>The running festival that has the stone open now and the place it is held (null: none, or not held in this region).</summary>
    public (RunningFestival Run, FestivalDef Def, ResolvedSite Site)? Open()
    {
        foreach (var run in _festivals.Running)
            if (run.Site is { } site && FestivalCatalog.Get(run.Id) is { } def && def.IsOpen(SteinRules.GameId, run.Day))
                return (run, def, site);
        return null;
    }

    /// <summary>Why the stone is not open for <paramref name="peer"/>, or null (also the client's check before it asks).</summary>
    private string? Closed(out (RunningFestival Run, FestivalDef Def, ResolvedSite Site) open)
    {
        open = default;
        foreach (var run in _festivals.Running)
        {
            if (FestivalCatalog.Get(run.Id) is not { } def || def.Game(SteinRules.GameId) == null) continue;
            if (run.Site is not { } site) return $"{run.Title} is not held in this region.";
            if (!def.IsOpen(SteinRules.GameId, run.Day)) return $"The stone opens on day {def.OpensOn(SteinRules.GameId)}.";
            open = (run, def, site);
            return null;
        }
        return "There is no stone to put now.";
    }

    // ---- the server ----------------------------------------------------------------------------

    /// <summary>
    /// Server (and a bot's way in): opens an attempt for <paramref name="peer"/> with a stone of class <paramref name="cls"/>. Needs
    /// the stone open, a body at the ground, an adventuring human with attempts left today (a bot has no limit and no Batzen). Returns
    /// the attempt, or null when refused (the player was told why).
    /// </summary>
    public SteinAttempt? ServeBegin(long peer, int cls)
    {
        if (!Authority || cls < 0 || cls >= SteinRules.Classes.Length) return null;
        if (peer > 0 && !Allowed(peer)) return No(peer, "Too fast.");
        if (Closed(out var o) is { } why) return No(peer, why);
        if (peer > 0 && !_adventure.Active(peer)) return No(peer, "No adventuring now.");
        if (!AtGround(peer, o.Site)) return No(peer, "Walk up to the stone first.");
        if (peer > 0 && _festivals.PlaysToday(peer, o.Def.Id, SteinRules.GameId) >= SteinRules.AttemptsPerDay)
            return No(peer, $"That is {SteinRules.AttemptsPerDay} puts today; the stone rests until tomorrow.");

        int id = ++_nextId;
        var a = new SteinAttempt(id, id * 7919 + (int)(ClockSync.ServerNow * 1000) % 10007, cls, ClockSync.ServerNow, o.Def.Id);
        _open[peer] = a;
        if (Connected(peer)) RpcId(peer, MethodName.TellBegin, a.Id, a.Seed, a.Class, a.Begun);
        else if (!_networked) TellBegin(a.Id, a.Seed, a.Class, a.Begun);
        return a;
    }

    /// <summary>
    /// Server (and a bot's way in): the put of <paramref name="peer"/>'s open attempt. <paramref name="trusted"/> skips the stamps' plausibility
    /// check (a bot's are made by the server itself). Returns what landed, or null when refused.
    /// </summary>
    public SteinLanding? ServeAttempt(long peer, int attempt, double[] stamps, double pitch, bool trusted = false)
    {
        if (!Authority) return null;
        if (!_open.TryGetValue(peer, out var a) || a.Id != attempt) return Fail(peer, "No put is open; pick the stone up again.");
        _open.Remove(peer);
        if (ClockSync.ServerNow - a.Begun > 180) return Fail(peer, "That put took too long.");
        if (Closed(out var o) is { } why) return Fail(peer, why);
        if (o.Def.Id != a.Festival) return Fail(peer, "That festival is over.");
        if (!AtGround(peer, o.Site)) return Fail(peer, "You walked away from the stone.");
        if (!trusted && SteinRules.Implausible(stamps, a.Begun, ClockSync.ServerNow) is { } bad) return Fail(peer, bad);

        var r = SteinRules.Resolve(a.Seed, a.Class, new SteinThrow(stamps, pitch));
        int granted = 0;
        if (peer > 0)
            granted = _festivals.Award(peer, o.Def.Id, r.Batzen, $"put the {SteinRules.Class(a.Class).Kg} kg stone {r.Metres:F2} m", SteinRules.GameId, r.Points);
        var g = SteinGround.Of(o.Site);
        var landing = new SteinLanding(peer, _adventure.NameOf?.Invoke(peer) ?? "", a.Id, a.Class, r.Metres, r.Angle, r.Speed, r.Power,
            granted, r.Points, r.Foul, g.E, g.N, g.Yaw);
        Throws++;
        GD.Print($"[stein] {(peer > 0 ? "peer " + peer : "bot " + peer)}: {SteinRules.Class(a.Class).Kg} kg, {(r.Foul ?? $"{r.Metres:F2} m at {r.Angle:F0} deg, power {r.Power:F2}")}; +{granted} Batzen");
        Tell(landing, o.Site);
        return landing;
    }

    /// <summary>A bot puts the stone (<paramref name="skill"/> 0 to 1): the server path a player takes, with the presses a person of that skill would make.</summary>
    public SteinLanding? ServeBot(long bot, double skill, Random rng, int cls = 0)
    {
        if (ServeBegin(bot, cls) is not { } a) return null;
        var t = SteinRules.BotAttempt(skill, rng, a.Seed, a.Class, a.Begun);
        return ServeAttempt(bot, a.Id, t.Stamps, t.Pitch, trusted: true);
    }

    private bool AtGround(long peer, ResolvedSite site)
    {
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return peer < 0;   // a bot's body may be unknown to the server's lookup
        return SteinGround.Of(site).DistanceTo(at.E, at.N) <= SteinRules.ReachM + 1.5;
    }

    private SteinAttempt? No(long peer, string why)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellNo, why);
        else if (!_networked && peer > 0) TellNo(why);
        return null;
    }

    private SteinLanding? Fail(long peer, string why)
    {
        No(peer, why);
        return null;
    }

    private bool Allowed(long peer)
    {
        long second = (long)GameClock.Now;
        var (s, n) = _rate.GetValueOrDefault(peer);
        n = s == second ? n + 1 : 1;
        _rate[peer] = (second, n);
        return n <= 8;
    }

    private void Tell(SteinLanding l, ResolvedSite site)
    {
        var g = SteinGround.Of(site);
        if (!_networked) { Landed?.Invoke(l); return; }
        foreach (int p in Multiplayer.GetPeers())
        {
            bool near = p == l.Thrower || _adventure.PositionOf?.Invoke(p) is { } at && g.DistanceTo(at.E, at.N) <= HearM;
            if (near) RpcId(p, MethodName.TellLanded, l.Thrower, l.Name, l.Attempt, l.Class, l.Metres, l.Angle, l.Speed, l.Power, l.Batzen, l.Points, l.Foul ?? "", l.E, l.N, l.Yaw);
        }
        // a dedicated server shows nothing; the host of a listen game is not a case here (offline is not networked)
    }

    // ---- the owner's requests ------------------------------------------------------------------

    /// <summary>Client: lift the stone of class <paramref name="cls"/> (opens an attempt).</summary>
    public void AskBegin(int cls)
    {
        if (Authority) ServeBegin(AdventureService.LocalPeer, cls);
        else RpcId(1, MethodName.RequestBegin, cls);
    }

    /// <summary>Client: the put of the open attempt: the six press stamps (server time) and the camera's pitch at the push.</summary>
    public void AskThrow(int attempt, double[] stamps, double pitch)
    {
        if (Authority) ServeAttempt(AdventureService.LocalPeer, attempt, stamps, pitch);
        else RpcId(1, MethodName.RequestThrow, attempt, stamps, pitch);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBegin(int cls)
    {
        if (_server) ServeBegin(Multiplayer.GetRemoteSenderId(), cls);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestThrow(int attempt, double[] stamps, double pitch)
    {
        if (_server) ServeAttempt(Multiplayer.GetRemoteSenderId(), attempt, stamps, pitch);
    }

    // ---- server -> client ----------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellBegin(int id, int seed, int cls, double begun) => Began?.Invoke(new SteinAttempt(id, seed, cls, begun, ""));

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellNo(string why)
    {
        GD.Print($"[stein] refused: {why}");
        Refused?.Invoke(why);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellLanded(long thrower, string name, int attempt, int cls, double metres, double angle, double speed, double power,
        int batzen, int points, string foul, double e, double n, float yaw)
        => Landed?.Invoke(new SteinLanding(thrower, name, attempt, cls, metres, angle, speed, power, batzen, points, foul.Length == 0 ? null : foul, e, n, yaw));
}
