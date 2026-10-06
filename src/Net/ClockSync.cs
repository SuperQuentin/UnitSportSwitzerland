using Godot;

namespace UnitSport.Net;

/// <summary>
/// One clock every peer can read: the server's, at <c>World/Clock</c> on both sides.
///
/// <para>
/// Nothing else in the game needs the machines to agree on the time — a race countdown goes
/// out in seconds, a pose carries its sender's own clock — but a radio playing the same track
/// for everyone does: "this CD started at T" only means something if every listener can turn
/// T into "how far in am I". So a client pings the server, the pong carries the server's clock,
/// and the sample with the shortest round trip (least queueing, so its half is the truest
/// one-way delay) decides the offset. <see cref="ServerNow"/> is then local time plus that.
/// </para>
///
/// <para>
/// Offline and on the server itself the offset is zero: <c>Multiplayer.IsServer()</c> is true in
/// both cases, which is exactly right here. Static, because a process has one clock.
/// </para>
/// </summary>
public partial class ClockSync : Node
{
    public const string NodeName = "Clock";

    private const int Samples = 8;
    private const double FastPeriod = 0.2, FastFor = 3.0, SlowPeriod = 2.0;

    private static double _offset;
    private static double _serverUnixOffset;
    private static double _rtt = double.NaN;
    private static bool _synced;

    /// <summary>The server's clock, in seconds, as well as this peer can tell.</summary>
    public static double ServerNow => LocalNow + _offset;

    /// <summary>
    /// The server's wall clock (Unix seconds), as well as this peer can tell (#452): for stamps
    /// the server writes in Unix time because they outlive it on disk (a campfire's lighting, a
    /// vehicle's spawn), compared on a client whose own system clock may be minutes off. This
    /// machine's own wall clock offline, on the server, and before the first pong.
    /// </summary>
    public static double ServerUnixNow => _synced ? ServerNow + _serverUnixOffset : Time.GetUnixTimeFromSystem();

    /// <summary>This process's own monotonic clock, seconds; game time under <c>--fixed-fps</c> (<see cref="Core.GameClock"/>).</summary>
    public static double LocalNow => Core.GameClock.Fixed ? Core.GameClock.Now : Time.GetTicksUsec() / 1_000_000.0;

    /// <summary>Round trip to the server of the sample in use, or NaN before the first pong.</summary>
    public static double Rtt => _rtt;

    /// <summary>At least one pong has arrived; before that <see cref="ServerNow"/> is local time.</summary>
    public static bool Synced => _synced;

    private readonly (double Rtt, double Offset)[] _ring = new (double, double)[Samples];
    private int _ringCount, _ringNext;
    private int _seq;
    private double _startedAt, _nextPingAt;

    public static ClockSync Create(Node world)
    {
        var clock = new ClockSync { Name = NodeName };
        world.AddChild(clock);
        return clock;
    }

    public override void _ExitTree()
    {
        _startedAt = 0;
        _nextPingAt = 0;
        _offset = 0;
        _rtt = double.NaN;
        _synced = false;
    }

    private bool Online => NetLink.Online(this);

    // Paced on the wall clock (Core.RealClock), never on the engine's delta: under a time scale a
    // delta-accumulated period stretches by 1 / TimeScale, and this is the one cadence the sim and
    // env clocks are both derived from, so it has to hold its pace whatever the world is doing.
    public override void _Process(double delta)
    {
        if (!Online || Multiplayer.IsServer()) return;
        double real = Core.RealClock.Now;
        if (_startedAt == 0) _startedAt = real;
        if (real < _nextPingAt) return;
        _nextPingAt = real + (real - _startedAt < FastFor ? FastPeriod : SlowPeriod);
        RpcId(1, MethodName.Ping, ++_seq, LocalNow);
    }

    // Unreliable on purpose: a lost ping is a sample not taken, and a late one would only be a
    // worse sample. Reliable delivery retries would put queueing delay into the very thing
    // being measured.
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void Ping(int seq, double sentAt)
    {
        if (!Multiplayer.IsServer()) return;
        double now = LocalNow;
        RpcId(Multiplayer.GetRemoteSenderId(), MethodName.Pong, seq, sentAt, now, Time.GetUnixTimeFromSystem() - now);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void Pong(int seq, double sentAt, double serverNow, double serverUnixOffset)
    {
        _serverUnixOffset = serverUnixOffset;
        double now = LocalNow;
        double rtt = now - sentAt;
        if (rtt < 0 || rtt > 5) return;
        _ring[_ringNext] = (rtt, serverNow + rtt * 0.5 - now);
        _ringNext = (_ringNext + 1) % Samples;
        _ringCount = Math.Min(_ringCount + 1, Samples);

        int best = 0;
        for (int i = 1; i < _ringCount; i++)
            if (_ring[i].Rtt < _ring[best].Rtt) best = i;
        _offset = _ring[best].Offset;
        _rtt = _ring[best].Rtt;
        if (!_synced)
        {
            _synced = true;
            GD.Print($"[clock] synced to the server: offset {_offset:F3} s, rtt {_rtt * 1000:F0} ms");
        }
    }
}
