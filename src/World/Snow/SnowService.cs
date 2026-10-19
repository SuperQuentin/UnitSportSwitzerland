using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Snow;

/// <summary>
/// The ski slopes' snow (#887), on every peer. The weather is generated from the ski day alone
/// (<see cref="SnowWeather"/>), so the crowd's share of the wear costs no traffic; the server adds
/// what only it knows (an admin's <c>/weather</c> and the metres real players and bots ski on each
/// slope) and sends them: the override on change and on join, the real wear every
/// <see cref="WearSendSeconds"/> while it changes. At 06:00 every peer starts a new ski day with
/// no wear. docs/notes/world/ski-snow.md.
/// </summary>
public partial class SnowService : Node
{
    public const string NodeName = "Snow";
    /// <summary>Every peer generates the same weather from this; there is no per-world seed to share yet.</summary>
    public const int Seed = 887;
    private const double WearSendSeconds = 5;
    private const double ScanSeconds = 0.5;
    /// <summary>Wear per metre skied on a slope one metre wide: a 1 km run down a 30 m slope adds ~0.08 (a busy day for the crowd is ~1).</summary>
    private const float WearPerMetre = 1f / 400f;

    public static SnowService? Instance { get; private set; }

    private ChunkManager? _chunks;
    private PisteIndex? _index;
    private WeatherOverride? _override;
    private readonly Dictionary<ulong, float> _wear = new();
    private readonly HashSet<ulong> _dirty = new();
    private readonly Dictionary<ulong, (Vector3 At, ulong Slope)> _lastSeen = new();
    private long _wearDay = long.MinValue;
    private double _sinceScan, _sinceSend;
    private int _version;

    /// <summary>Bumped whenever the weather or any wear changes: layers redraw on a new value.</summary>
    public int Version => _version;

    public long Day { get; private set; }
    public double Hour { get; private set; } = 12;
    public DayWeather Today { get; private set; }
    public DayWeather Yesterday { get; private set; }
    public WeatherOverride? Override => _override;
    public PisteIndex? Index => _index;

    public static SnowService Create(Node world, ChunkManager chunks)
    {
        var s = new SnowService { Name = NodeName, _chunks = chunks, _index = new PisteIndex(chunks) };
        world.AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Tick();
        // --weather "snow 30" (offline, or a server's starting weather): the /weather words, held
        if (Deciding && Core.CmdArgs.Value("--weather") is { } words)
        {
            string reply = Command(words, mayChange: true, out bool set);
            if (set) Command("hold", mayChange: true, out _);
            GD.Print($"[snow] {reply}");
        }
    }

    private bool Deciding => !NetLink.Online(this) || Multiplayer.IsServer();

    /// <summary>The real wear on a slope today.</summary>
    public float RealWear(ulong slope) => _wear.TryGetValue(slope, out float w) ? w : 0;

    /// <summary>The snow on a slope at an altitude, now.</summary>
    public SnowState StateAt(PisteRecord slope, float altitude) =>
        SnowModel.Evaluate(Today, Yesterday, Hour, altitude, slope.Sun, slope, RealWear(slope.Id));

    /// <summary>The slope and its snow under a world point, if there is one.</summary>
    public bool TryAt(Vector3 world, out PisteRecord slope, out SnowState state, out float mask)
    {
        state = default;
        slope = null!;
        if (_index?.At(world, out mask) is not { } found) { mask = 0; return false; }
        slope = found;
        state = StateAt(found, world.Y);
        return true;
    }

    public override void _Process(double delta)
    {
        Tick();
        _index?.Trim(600);
        if (!Deciding) return;
        _sinceScan += delta;
        if (_sinceScan >= ScanSeconds)
        {
            _sinceScan = 0;
            ScanSkiers();
        }
        _sinceSend += delta;
        if (_sinceSend >= WearSendSeconds && _dirty.Count > 0 && NetLink.Online(this))
        {
            _sinceSend = 0;
            Rpc(MethodName.Wear, Pack(_dirty));
            _dirty.Clear();
        }
    }

    /// <summary>The ski day and hour from the environment clock; a new day clears the wear.</summary>
    private void Tick()
    {
        double clockHour = WorldClock.CurrentHour;
        long calendarDay = (long)Math.Floor((WorldClock.EnvNow + WorldClock.HourShift) / 86400.0);
        // a ski day runs 06:00 to 06:00: before six it is still yesterday's night
        long day = clockHour < SnowClock.ResetHour ? calendarDay - 1 : calendarDay;
        double hour = clockHour < SnowClock.ResetHour ? clockHour + 24 : clockHour;
        bool newDay = day != Day || _wearDay == long.MinValue;
        Day = day;
        Hour = hour;
        if (newDay)
        {
            Today = SnowWeather.ForDay(day, Seed, _override);
            Yesterday = SnowWeather.ForDay(day - 1, Seed, _override?.Sticky == true ? _override : null);
            _version++;
        }
        if (_wearDay != day)
        {
            _wearDay = day;
            _wear.Clear();
            _dirty.Clear();
            _lastSeen.Clear();
            _version++;
        }
    }

    // ---- real skiers (the deciding peer) ----------------------------------------------------------

    private void ScanSkiers()
    {
        if (_index == null) return;
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
        {
            if (node is not FootPlayer p || p.RideKindId != (int)RideKind.Skis) continue;
            ulong key = p.GetInstanceId();
            var at = p.GlobalPosition;
            var slope = _index.At(at, out _);
            if (slope != null && _lastSeen.TryGetValue(key, out var last) && last.Slope == slope.Id)
            {
                float metres = Mathf.Min(at.DistanceTo(last.At), 40f);
                if (metres > 0.5f)
                {
                    _wear[slope.Id] = RealWear(slope.Id) + metres * WearPerMetre / Math.Max(4f, slope.WidthM);
                    _dirty.Add(slope.Id);
                    _version++;
                }
            }
            _lastSeen[key] = (at, slope?.Id ?? 0);
        }
    }

    private byte[] Pack(IEnumerable<ulong> slopes)
    {
        var list = new List<ulong>(slopes);
        var bytes = new byte[8 + list.Count * 10];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, _wearDay);
        for (int i = 0; i < list.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8 + i * 10), list[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16 + i * 10), (ushort)Math.Clamp(RealWear(list[i]) * 1000, 0, ushort.MaxValue));
        }
        return bytes;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Wear(byte[] bytes)
    {
        if (bytes.Length < 8) return;
        long day = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        Tick();
        if (day != _wearDay) return; // a ski day that ended here already
        for (int o = 8; o + 10 <= bytes.Length; o += 10)
            _wear[BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(o))] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(o + 8)) / 1000f;
        _version++;
    }

    // ---- the weather override ---------------------------------------------------------------------

    /// <summary>
    /// <c>/weather</c> with words: an admin's change on the deciding peer (sent to every client), or
    /// nothing but the reply when <paramref name="mayChange"/> is false. Returns the chat reply.
    /// </summary>
    public string Command(string args, bool mayChange, out bool changed)
    {
        changed = false;
        if (args.Trim().Length == 0) return WeatherCommand.Describe(Today, Hour, _override);
        if (!mayChange) return "Changing the weather is an admin command.";
        var next = WeatherCommand.Apply(_override, Day, args, out string? error);
        if (error != null) return error;
        SetOverride(next);
        changed = true;
        if (NetLink.Online(this) && Multiplayer.IsServer()) Rpc(MethodName.Weather, Json(next));
        return WeatherCommand.Describe(Today, Hour, _override);
    }

    /// <summary>Server: tells a newly connected peer the override and today's real wear.</summary>
    public void SendTo(long peerId)
    {
        if (!Multiplayer.IsServer()) return;
        RpcId(peerId, MethodName.Weather, Json(_override));
        if (_wear.Count > 0) RpcId(peerId, MethodName.Wear, Pack(_wear.Keys));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Weather(string json)
    {
        try
        {
            SetOverride(json.Length == 0 ? null : JsonSerializer.Deserialize<WeatherOverride>(json));
        }
        catch (JsonException e)
        {
            GD.PushWarning($"[snow] unreadable weather from the server: {e.Message}");
        }
    }

    private static string Json(WeatherOverride? ov) => ov == null ? "" : JsonSerializer.Serialize(ov);

    private void SetOverride(WeatherOverride? ov)
    {
        _override = ov;
        Today = SnowWeather.ForDay(Day, Seed, ov);
        Yesterday = SnowWeather.ForDay(Day - 1, Seed, ov?.Sticky == true ? ov : null);
        _version++;
    }
}
