using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// Aircraft parked at the airports' stands (#422): A320s with airstairs docked at L1 and L2, an
/// AN-124 and the military freighter on the cargo apron, for anyone to take. Where they stand is
/// <c>airports.json</c> (the preprocessor's <c>--airports</c>: OSM stands on swissTLM3D's paved
/// area, <see cref="AirportPlanner"/>; a fixture course plans its own), read from the chunk source or
/// <c>--airports &lt;file&gt;</c>. The server decides and places them (<see cref="VehicleManager.Place"/>,
/// the server the authority at rest), so every player sees the same; offline the client does. A
/// stand's aircraft is put there when its tile comes into the rings; one taken away (flown off,
/// wrecked, or cleared when nobody was near) is put back after <see cref="RespawnSeconds"/>, once
/// nothing stands on it and nobody is within <see cref="ClearOfPlayers"/>: its stairs, wherever
/// they were pushed, come back docked with it. A check every few seconds, nothing per frame. No AI
/// traffic. See <c>docs/notes/world/airports.md</c>.
/// </summary>
public partial class AirportStands : Node
{
    public const string Prefix = "veh_stand_";

    /// <summary>How long a taken aircraft's stand stays empty before a new one is put there, s (checks shorten it).</summary>
    public static double RespawnSeconds { get; set; } = 300;

    /// <summary>Nobody within this of a stand when its aircraft is put back, m: it does not appear under someone's nose.</summary>
    public const float ClearOfPlayers = 300f;

    /// <summary>A stand is filled only with a player within this, m (vehicles further off are cleared as lonely anyway).</summary>
    public const float PlaceRadius = 2800f;

    private readonly ChunkManager _chunks;
    /// <summary>The stands whose tiles have been seen, and since when each stood empty (NaN: filled; -inf: never filled).</summary>
    private readonly Dictionary<string, (AirportStand Stand, double EmptySince)> _stands = new();
    private AirportIndex _index = new();
    /// <summary>A320 stands filled whose stairs are still to come.</summary>
    private readonly HashSet<string> _stairsDue = new();
    private double _sinceCheck;
    private readonly List<Vector3> _players = new();

    public static AirportStands? Instance { get; private set; }

    /// <summary>The airports read at boot (empty, never null, before or without any).</summary>
    public AirportIndex Index => _index;

    public AirportStands(ChunkManager chunks)
    {
        Name = "AirportStands";
        _chunks = chunks;
    }

    public AirportStands() : this(null!) { }

    public override void _EnterTree() => Instance = this;

    public override async void _Ready()
    {
        _chunks.TileEntered += OnTileEntered;
        // a check's server refills sooner (tools/airportnetcheck.sh)
        if (double.TryParse(CmdArgs.Value("--standrespawn"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double respawn))
            RespawnSeconds = respawn;
        var index = await LoadAsync(_chunks.Source);
        if (!IsInsideTree()) return;
        _index = index ?? new AirportIndex();
        if (_index.Airports.Count > 0)
            GD.Print($"[airports] {_index.Airports.Count} airports, {_index.Stands().Count()} stands ({string.Join(", ", _index.Airports.Select(a => $"{a.Code} {a.Stands.Count}"))})");
        // tiles that entered before the file was read
        var seen = new List<(TileId Id, int Stride)>();
        _chunks.ListTiles(seen);
        foreach (var (id, _) in seen) OnTileEntered(id);
    }

    public override void _ExitTree()
    {
        _chunks.TileEntered -= OnTileEntered;
        if (Instance == this) Instance = null;
    }

    /// <summary><c>--airports &lt;file&gt;</c> when given (an <c>airports.json</c> tried out beside tiles it was not written into), else the chunk source's.</summary>
    private static async Task<AirportIndex?> LoadAsync(IChunkSource? source)
    {
        if (CmdArgs.Value("--airports", notFlag: true) is { } file)
        {
            try { return AirportIndex.FromJson(await System.IO.File.ReadAllTextAsync(file)); }
            catch (Exception e) { GD.PushWarning($"[airports] {file}: {e.Message}"); }
        }
        return source == null ? null : await source.LoadAirportsAsync();
    }

    private bool Decides => Net.NetworkManager.DedicatedServer || Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer;

    private void OnTileEntered(TileId id)
    {
        if (!Decides) return;
        foreach (var (_, stand) in _index.Stands())
            if (TileId.FromLv95(stand.E, stand.N) == id && !_stands.ContainsKey(stand.Id))
                _stands[stand.Id] = (stand, double.NegativeInfinity);   // never filled: as soon as the ground is here
        _sinceCheck = double.MaxValue;
    }

    public override void _Process(double delta)
    {
        if (_stands.Count == 0) return;
        _sinceCheck += delta;
        if (_sinceCheck < 3.0) return;
        _sinceCheck = 0;
        if (!Decides || VehicleManager.Instance is not { } vehicles || _chunks.Origin is not { } origin) return;
        Review(vehicles, origin);
    }

    /// <summary>The node name of a stand's aircraft (its stairs add <c>_door</c>).</summary>
    public static string NameOf(AirportStand stand) => Prefix + Sanitize(stand.Id);

    private static string Sanitize(string id)
    {
        var chars = id.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (!char.IsAsciiLetterOrDigit(chars[i])) chars[i] = '_';
        return new string(chars);
    }

    public static RideKind KindOf(StandUse use) => use switch
    {
        StandUse.Heavy => RideKind.An124,
        StandUse.Military => RideKind.Freighter,
        _ => RideKind.A320,
    };

    /// <summary>Puts an aircraft at every stand that has none and may have one now. Returns how many were put.</summary>
    public int Review(VehicleManager vehicles, WorldOrigin origin)
    {
        double now = GameClock.Now;
        int placed = 0;
        _players.Clear();
        if (vehicles.PlayerPositions?.Invoke() is { } players) _players.AddRange(players);
        foreach (var id in _stands.Keys.ToList())
        {
            var (stand, empty) = _stands[id];
            string name = NameOf(stand);
            var there = vehicles.GetNodeOrNull(name);
            if (there is VehicleBody { Wrecked: false } plane)
            {
                if (!double.IsNaN(empty)) _stands[id] = (stand, double.NaN);
                // an A320's stairs once it has settled on its gear, docked where it stands
                if (_stairsDue.Contains(id) && plane.Posed && plane.Velocity.LengthSquared() < 0.01f)
                {
                    _stairsDue.Remove(id);
                    AirstairsDock.PlaceAt(vehicles, plane.Capture() with { Name = name }, name);
                }
                continue;
            }
            // taken (or wrecked, or cleared as lonely): from now on it waits its time
            if (double.IsNaN(empty)) { _stands[id] = (stand, now); continue; }
            // a wreck still lying there keeps the name until the housekeeping clears it
            if (there != null) continue;
            if (!double.IsNegativeInfinity(empty) && now - empty < RespawnSeconds) continue;
            var at = origin.ToWorld(stand.E, stand.N, 0);
            if (_players.Count > 0 && !_players.Any(p => MathX.FlatDistance(p, at) < PlaceRadius)) continue;
            if (!_chunks.TryGetHeight(at, out float ground)) continue;
            var size = AirportPlanner.SizeOf(stand.Use);
            if (!double.IsNegativeInfinity(empty) && !Clear(vehicles, at, name, (float)(size.Span / 2 + 3))) continue;
            if (Fill(vehicles, origin, stand, at with { Y = ground }) == 0) continue;
            if (KindOf(stand.Use) == RideKind.A320) _stairsDue.Add(id);
            _stands[id] = (stand, double.NaN);
            placed++;
        }
        if (placed > 0) GD.Print($"[airports] {placed} aircraft parked at their stands, {_stands.Count} stands known");
        return placed;
    }

    /// <summary>The stand's aircraft and, for an A320, its stairs docked at L1 and L2 (any left over from before are cleared first).</summary>
    private static int Fill(VehicleManager vehicles, WorldOrigin origin, AirportStand stand, Vector3 at)
    {
        string name = NameOf(stand);
        // the stand's own stairs from last time, wherever they were pushed, come back docked: cleared
        // now, the stand filled at the next review (a node of the same name must be gone first)
        int cleared = 0;
        foreach (var node in vehicles.GetChildren())
            if (node is VehicleBody v && v.Name.ToString().StartsWith(name + "_", StringComparison.Ordinal))
            {
                if (!v.IsQueuedForDeletion()) v.QueueFree();
                cleared++;
            }
        if (cleared > 0) return 0;
        var kind = KindOf(stand.Use);
        if (Airliner.For(kind) is not { } ride) return 0;
        bool stairs = kind == RideKind.A320;   // placed at the next review, once it stands (Review)
        var state = new VehicleState(kind, origin.ToGlobal(at), -Mathf.DegToRad((float)stand.Heading), Vector3.Zero, ride.MaxHealth,
            EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: VehicleState.Now,
            // boarding: L1 and L2 open at the stairs; the rest as a pilot leaves one (gear down)
            // (an airliner keeps its doors in Flags bits 13-16, VehicleBody)
            Flags: ride.PackFlags() & ~(15 << 13) | (stairs ? (1 | 4) << 13 : 0));
        return vehicles.Place(state, name) == null ? 0 : 1;
    }

    /// <summary>No other vehicle within <paramref name="radius"/> of the stand and nobody within <see cref="ClearOfPlayers"/>.</summary>
    private bool Clear(VehicleManager vehicles, Vector3 at, string name, float radius)
    {
        foreach (var node in vehicles.GetChildren())
            if (node is VehicleBody v && !v.IsQueuedForDeletion() && !v.Name.ToString().StartsWith(name + "_", StringComparison.Ordinal)
                && MathX.FlatDistance(v.GlobalPosition, at) < radius) return false;
        foreach (var p in _players)
            if (MathX.FlatDistance(p, at) < ClearOfPlayers) return false;
        return true;
    }

    /// <summary>For checks: the stand an aircraft node stands for, or null.</summary>
    public AirportStand? StandOf(string nodeName)
    {
        foreach (var (stand, _) in _stands.Values)
            if (NameOf(stand) == nodeName) return stand;
        return null;
    }

    /// <summary>For checks: the stands known (their tiles seen), with whether each is filled now.</summary>
    public IEnumerable<(AirportStand Stand, bool Filled)> Known()
    {
        foreach (var (stand, empty) in _stands.Values) yield return (stand, double.IsNaN(empty));
    }
}
