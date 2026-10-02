using Godot;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Audio.Live;

/// <summary>
/// Live web radio in cars, heard in sync by everyone (#179). One node at <c>World/WebRadio</c> on
/// both sides (RPC path).
///
/// <para>
/// A live stream each client fetched for itself would be seconds apart between machines (every
/// Icecast connect starts with its own burst). So the <b>server</b> tunes each station once
/// (<see cref="StationTap"/>), stamps every sample on the shared clock and relays it as 8-bit µ-law
/// (16 kHz mono, 128 kbit/s per station, a PS1 kind of fidelity) to the clients that asked. A
/// client plays sample n at <c>T0 + Delay + n / Rate</c> on <see cref="ClockSync.ServerNow"/>, so
/// every speaker is on the same sample, give or take the clock's few milliseconds. Clients need
/// no ffmpeg and never touch the station's URL.
/// </para>
///
/// <para>
/// Who hears what: a car's station is its driver's replicated <see cref="FootPlayer.CarRadio"/>,
/// a parked car's is in its <see cref="VehicleState"/>. Each client hangs a
/// <see cref="WebRadioSpeaker"/> on every such source and asks the server (<c>Want</c>) for the
/// stations of the ones within <see cref="HearRadius"/>. A station nobody wants is closed after
/// <see cref="CloseAfter"/>.
/// </para>
/// </summary>
public partial class WebRadio : Node
{
    public const string NodeName = "WebRadio";
    public const string SpeakerName = "WebRadio";
    /// <summary>The node of a car stereo's CD speaker (#211), beside the station's.</summary>
    public const string CdSpeakerName = "CarCd";

    public const int Rate = 16000;
    /// <summary>A fifth of a second: the unit sent, and the slots of the client's buffer.</summary>
    public const int ChunkSamples = Rate / 5;
    /// <summary>Seconds every listener plays behind the server's clock: room for the round trip and a resend.</summary>
    public const double Delay = 2.0;
    /// <summary>Beyond this from the listener a radio is not asked for.</summary>
    public const float HearRadius = 50f;
    private const int MaxWanted = 3;
    /// <summary>A station stays asked for this long after its last radio went: getting out of a car hands it to the parked one.</summary>
    private const double KeepFor = 4;
    private const double CloseAfter = 20;
    private const int Channel = 3;

    public static WebRadio? Instance { get; private set; }

    /// <summary>Client: every player body on this machine, local and remote.</summary>
    public Func<IEnumerable<FootPlayer>>? Players { get; set; }

    /// <summary>Client: where the ears are (the camera).</summary>
    public Func<Vector3?>? Listener { get; set; }

    // server
    private readonly Dictionary<long, int[]> _subs = new();
    private readonly Dictionary<int, StationTap> _taps = new();
    private readonly List<(long Start, short[] Samples)> _chunks = new();
    // client
    private readonly Dictionary<int, StationBuffer> _buffers = new();
    private int[] _wanted = Array.Empty<int>();
    private readonly Dictionary<int, double> _lastNear = new();
    private double _sinceScan;

    /// <summary>On every peer; whichever is the server (the offline game too) tunes the stations.</summary>
    public static WebRadio Create(Node world)
    {
        var radio = new WebRadio { Name = NodeName };
        world.AddChild(radio);
        return radio;
    }

    public override void _EnterTree() => Instance = this;

    public override void _Ready() => Multiplayer.PeerDisconnected += id => _subs.Remove(id);

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        foreach (var tap in _taps.Values) tap.Dispose();
        _taps.Clear();
    }

    private bool Online => NetLink.Online(this);

    /// <summary>The audio heard of a station on this machine, or null when none has arrived.</summary>
    public StationBuffer? Buffer(int station) => _buffers.GetValueOrDefault(station);

    public override void _Process(double delta)
    {
        if (!NetworkManager.DedicatedServer)
        {
            _sinceScan += delta;
            if (_sinceScan >= 0.25)
            {
                _sinceScan = 0;
                Scan();
            }
        }
        if (NetLink.IsServer(this)) Serve(delta);
    }

    // ---- client: speakers on the sources, and asking for their stations ----------------------

    private void Scan()
    {
        var sources = new List<(Node3D Node, int Station)>();
        var discs = new List<(Node3D Node, RadioPlay? Cd)>();
        foreach (var p in Players?.Invoke() ?? Enumerable.Empty<FootPlayer>())
            if (IsInstanceValid(p))
            {
                sources.Add((p, p.PlayingCarRadio));
                discs.Add((p, p.PlayingCarCd));
            }
        if (VehicleManager.Instance is { } vehicles)
            foreach (var node in vehicles.GetChildren())
                if (node is VehicleBody v)
                {
                    sources.Add((v, v.Wrecked ? 0 : v.Radio));
                    discs.Add((v, v.Wrecked ? null : RadioPlay.Decode(v.Cd)));
                }
        // a CD in a car stereo (#211) is the boombox's clock-driven speaker, no relay: every
        // client fetches the Ogg once and plays it from ServerNow − StartedAt. Not headless, like
        // a radio in the world (no speaker to drive, no file worth downloading).
        if (DisplayServer.GetName() != "headless")
            foreach (var (node, cd) in discs) UpdateCd(node, cd);

        var ear = Listener?.Invoke();
        var near = new List<(float Dist, int Station)>();
        foreach (var (node, station) in sources)
        {
            var speaker = node.GetNodeOrNull<WebRadioSpeaker>(SpeakerName);
            if (Stations.For(station) == null)
            {
                speaker?.QueueFree();
                continue;
            }
            if (speaker == null)
            {
                speaker = new WebRadioSpeaker { Name = SpeakerName, Position = new Vector3(0, 1f, 0) };
                node.AddChild(speaker);
            }
            speaker.Station = station;
            float d = ear is { } at ? at.DistanceTo(node.GlobalPosition) : 0f;
            if (d < HearRadius) near.Add((d, station));
        }
        double now = ClockSync.LocalNow;
        foreach (var (_, station) in near) _lastNear[station] = now;
        foreach (var gone in _lastNear.Where(kv => now - kv.Value > KeepFor).Select(kv => kv.Key).ToList()) _lastNear.Remove(gone);
        // nearest first, then the ones that just went (their buffer is kept warm)
        var want = near.OrderBy(n => n.Dist).Select(n => n.Station)
            .Concat(_lastNear.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
            .Distinct().Take(MaxWanted).OrderBy(s => s).ToArray();
        if (want.SequenceEqual(_wanted)) return;
        _wanted = want;
        foreach (var gone in _buffers.Keys.Except(want).ToList()) _buffers.Remove(gone);
        GD.Print($"[webradio] listening to {(want.Length == 0 ? "nothing" : string.Join(", ", want.Select(Stations.Name)))}");
        if (NetLink.IsServer(this)) _subs[Multiplayer.GetUniqueId()] = want;
        else if (NetLink.Online(this)) RpcId(1, MethodName.Want, want);
    }

    private static void UpdateCd(Node3D node, RadioPlay? cd)
    {
        var speaker = node.GetNodeOrNull<RadioSpeaker>(CdSpeakerName);
        if (cd is not { } play)
        {
            if (speaker != null) speaker.On = false;   // kept, silent, as for a held radio
            return;
        }
        if (speaker == null)
        {
            speaker = new RadioSpeaker { Name = CdSpeakerName, Position = new Vector3(0, 1f, 0) };
            node.AddChild(speaker);
        }
        speaker.CdId = play.CdId;
        speaker.StartedAt = play.StartedAt;
        speaker.Length = play.Length;
        speaker.On = true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Want(int[] stations)
    {
        if (!Multiplayer.IsServer()) return;
        _subs[Multiplayer.GetRemoteSenderId()] = stations.Where(s => Stations.For(s) != null).Distinct().Take(MaxWanted).ToArray();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = Channel)]
    private void Chunk(int station, double t0, long start, byte[] mulaw) => Receive(station, t0, start, mulaw);

    private void Receive(int station, double t0, long start, byte[] mulaw)
    {
        if (!_wanted.Contains(station)) return;
        if (!_buffers.TryGetValue(station, out var buffer) || buffer.T0 != t0)
            _buffers[station] = buffer = new StationBuffer(t0);
        buffer.Put(start, mulaw);
    }

    // ---- server: one tap per wanted station, chunks to whoever wants it ----------------------

    private readonly HashSet<int> _wantedIds = new();
    private readonly List<int> _tapIds = new();

    private void Serve(double delta)
    {
        // reused, not rebuilt every frame (#221)
        var wanted = _wantedIds;
        wanted.Clear();
        foreach (var s in _subs.Values) wanted.UnionWith(s);
        if (wanted.Count == 0 && _taps.Count == 0) return;   // nobody listens, no tap to close
        foreach (int id in wanted)
            if (!_taps.ContainsKey(id) && Stations.For(id) is { } station) _taps[id] = new StationTap(station);

        long me = Multiplayer.GetUniqueId();
        _tapIds.Clear();
        _tapIds.AddRange(_taps.Keys);
        foreach (int id in _tapIds)
        {
            var tap = _taps[id];
            if (!wanted.Contains(id))
            {
                tap.Unwanted += delta;
                if (tap.Unwanted > CloseAfter)
                {
                    GD.Print($"[webradio] {tap.Station.Name} closed: nobody listens");
                    tap.Dispose();
                    _taps.Remove(id);
                }
                continue;
            }
            tap.Unwanted = 0;
            _chunks.Clear();
            tap.Pump(_chunks);
            foreach (var (start, samples) in _chunks)
            {
                var bytes = MuLaw.Encode(samples);
                foreach (var (peer, stations) in _subs)
                {
                    if (!stations.Contains(id)) continue;
                    if (peer == me) Receive(id, tap.T0, start, bytes);
                    else RpcId(peer, MethodName.Chunk, id, tap.T0, start, bytes);
                }
            }
        }
    }
}

/// <summary>
/// What a client holds of one station: the last few seconds by sample index, in slots of one
/// chunk. A sample that never arrived reads as silence.
/// </summary>
public sealed class StationBuffer
{
    private const int Slots = 64;   // ~13 s
    private readonly long[] _starts = new long[Slots];
    private readonly byte[][] _data = new byte[Slots][];

    public double T0 { get; }

    /// <summary>One past the newest sample received.</summary>
    public long End { get; private set; }

    public StationBuffer(double t0)
    {
        T0 = t0;
        Array.Fill(_starts, -1);
    }

    public void Put(long start, byte[] mulaw)
    {
        if (mulaw.Length != WebRadio.ChunkSamples || start < 0) return;
        int slot = (int)(start / WebRadio.ChunkSamples % Slots);
        _starts[slot] = start;
        _data[slot] = mulaw;
        End = Math.Max(End, start + mulaw.Length);
    }

    /// <summary>Whether the sample at <paramref name="index"/> arrived (not silence by absence).</summary>
    public bool Has(long index) => index >= 0 && _starts[(int)(index / WebRadio.ChunkSamples % Slots)] == index / WebRadio.ChunkSamples * WebRadio.ChunkSamples;

    public float Sample(long index)
    {
        if (index < 0) return 0f;
        long chunk = index / WebRadio.ChunkSamples;
        int slot = (int)(chunk % Slots);
        return _starts[slot] == chunk * WebRadio.ChunkSamples ? MuLaw.Decode(_data[slot][index - _starts[slot]]) : 0f;
    }
}

/// <summary>G.711 µ-law: 16-bit samples in 8 bits, the telephone's companding.</summary>
public static class MuLaw
{
    private const int Bias = 0x84, Clip = 32635;
    private static readonly float[] Table = BuildTable();

    public static byte[] Encode(short[] samples)
    {
        var bytes = new byte[samples.Length];
        for (int i = 0; i < samples.Length; i++) bytes[i] = Encode(samples[i]);
        return bytes;
    }

    public static byte Encode(short sample)
    {
        int s = sample;
        int sign = (s >> 8) & 0x80;
        if (sign != 0) s = -s;
        if (s > Clip) s = Clip;
        s += Bias;
        int exponent = 7;
        for (int mask = 0x4000; (s & mask) == 0 && exponent > 0; mask >>= 1) exponent--;
        int mantissa = (s >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static float Decode(byte b) => Table[b];

    private static float[] BuildTable()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            int u = ~i & 0xFF;
            int sign = u & 0x80, exponent = (u >> 4) & 7, mantissa = u & 0x0F;
            int s = (((mantissa << 3) + Bias) << exponent) - Bias;
            t[i] = (sign != 0 ? -s : s) / 32768f;
        }
        return t;
    }
}
