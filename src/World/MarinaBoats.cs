using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// Jetskis and speedboats parked along the harbour jetties (#383), for anyone to take. Where they lie
/// is <see cref="Jetty.BoatBerths"/>: the same on every peer and every restart. The server decides
/// and spawns them (<see cref="VehicleManager.Place"/>), so every player sees the same boats; offline
/// the client does. A jetty's boats are put there when its tile comes into the streamed rings (and
/// its water is loaded); one taken away is put back after <see cref="RespawnSeconds"/>, once its
/// place is clear and nobody is near. A check every few seconds over the jetties seen, nothing per
/// frame. They are ordinary parked boats: they float, sleep in a calm, and get #378's mooring spring.
/// See <c>docs/notes/world/landings.md</c>.
/// </summary>
public partial class MarinaBoats : Node
{
    public const string Prefix = "veh_marina_";

    /// <summary>How long a taken boat's place stays empty before a new one is put there, s (checks shorten it).</summary>
    public static double RespawnSeconds { get; set; } = 180;

    /// <summary>Nobody within this of a place when a boat is put back there, m: it does not appear under someone's nose.</summary>
    public const float ClearOfPlayers = 40f;

    /// <summary>
    /// A boat is put at its place only with a player within this, m: the rings reach harbours 10 km off,
    /// and a hundred boats floating out of sight are work for nothing.
    /// </summary>
    public const float PlaceRadius = 1500f;

    /// <summary>Water a place needs, m (a speedboat draws 0.28 m).</summary>
    public const float MinDepth = 0.7f;

    private readonly ChunkManager _chunks;
    /// <summary>The berths of the jetties whose tiles have been seen, and since when each stood empty (NaN: filled).</summary>
    private readonly Dictionary<string, (BoatBerth Berth, double EmptySince)> _berths = new();
    private readonly HashSet<string> _jettiesSeen = new();
    private double _sinceCheck;

    public static MarinaBoats? Instance { get; private set; }

    public MarinaBoats(ChunkManager chunks)
    {
        Name = "MarinaBoats";
        _chunks = chunks;
    }

    public MarinaBoats() : this(null!) { }

    public override void _EnterTree() => Instance = this;

    public override void _Ready()
    {
        _chunks.TileEntered += OnTileEntered;
        Landings.Changed += OnLandingsChanged;
    }

    public override void _ExitTree()
    {
        _chunks.TileEntered -= OnTileEntered;
        Landings.Changed -= OnLandingsChanged;
        if (Instance == this) Instance = null;
    }

    private bool Decides => Net.NetworkManager.DedicatedServer || Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer;

    private void OnLandingsChanged()
    {
        _berths.Clear();
        _jettiesSeen.Clear();
    }

    private void OnTileEntered(TileId id)
    {
        if (!Decides) return;
        foreach (var jetty in Landings.Current.Jetties)
        {
            var (e, n) = jetty.Ribbon.Middle;
            if (TileId.FromLv95(e, n) != id || !_jettiesSeen.Add(jetty.Id)) continue;
            foreach (var b in jetty.BoatBerths())
                _berths[b.Id] = (b, double.NegativeInfinity);   // never filled: as soon as the water is here
        }
        _sinceCheck = double.MaxValue;
    }

    public override void _Process(double delta)
    {
        if (_berths.Count == 0) return;
        _sinceCheck += delta;
        if (_sinceCheck < 3.0) return;
        _sinceCheck = 0;
        if (!Decides || VehicleManager.Instance is not { } vehicles || _chunks.Origin is not { } origin) return;
        Review(vehicles, origin);
    }

    /// <summary>Puts a boat at every place that has none and may have one now. Returns how many were put.</summary>
    public int Review(VehicleManager vehicles, WorldOrigin origin)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        int placed = 0;
        var players = vehicles.PlayerPositions?.Invoke().ToList();
        foreach (var id in _berths.Keys.ToList())
        {
            var (berth, empty) = _berths[id];
            string name = Prefix + id;
            if (vehicles.GetNodeOrNull(name) != null)
            {
                if (!double.IsNaN(empty)) _berths[id] = (berth, double.NaN);
                continue;
            }
            // taken: from now on it waits its time
            if (double.IsNaN(empty)) { _berths[id] = (berth, now); continue; }
            if (now - empty < RespawnSeconds && !double.IsNegativeInfinity(empty)) continue;
            var at = origin.ToWorld(berth.E, berth.N, 0);
            if (players != null && !players.Any(p => MathX.FlatDistance(p, at) < PlaceRadius)) continue;
            if (!_chunks.TryGetWater(at, out float level, out _) || !_chunks.TryGetHeight(at, out float bed) || level - bed < MinDepth) continue;
            if (!double.IsNegativeInfinity(empty) && !Clear(vehicles, at)) continue;
            var kind = berth.Speedboat ? RideKind.Speedboat : RideKind.Jetski;
            var ride = Rideable.Create(kind)!;
            var keel = at with { Y = level - (berth.Speedboat ? 0.28f : 0.23f) };
            var state = new VehicleState(kind, origin.ToGlobal(keel), -Mathf.DegToRad((float)berth.Heading), Vector3.Zero, ride.MaxHealth,
                EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0);
            if (vehicles.Place(state, name) == null) continue;
            _berths[id] = (berth, double.NaN);
            placed++;
        }
        if (placed > 0) GD.Print($"[marina] {placed} boat(s) moored along the jetties, {_berths.Count} places known");
        return placed;
    }

    /// <summary>No boat within 4 m of the place and nobody within <see cref="ClearOfPlayers"/>.</summary>
    private static bool Clear(VehicleManager vehicles, Vector3 at)
    {
        foreach (var node in vehicles.GetChildren())
            if (node is VehicleBody v && MathX.FlatDistance(v.GlobalPosition, at) < 4f) return false;
        foreach (var p in vehicles.PlayerPositions?.Invoke() ?? [])
            if (MathX.FlatDistance(p, at) < ClearOfPlayers) return false;
        return true;
    }

    /// <summary>For checks: the boats moored now, by name.</summary>
    public IEnumerable<VehicleBody> Moored(VehicleManager vehicles)
    {
        foreach (var node in vehicles.GetChildren())
            if (node is VehicleBody v && v.Name.ToString().StartsWith(Prefix, StringComparison.Ordinal)) yield return v;
    }
}
