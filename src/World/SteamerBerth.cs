using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// The paddle steamer lies at the CGN landing at Nyon (#303): alongside its pier (#377), at the
/// berth of the landing "Nyon (lac)" (<see cref="Landings"/>), its port gangway open onto the pier's
/// head. Whenever the tile under the berth comes into the streamed rings and no steamer of the berth
/// is there, one is placed; taken away, it is put back the next time the area loads. One check per
/// tile load, nothing per frame. The server decides and spawns it (<see cref="VehicleManager.Place"/>),
/// so every player sees the same one; offline the client does. With no landings (a region built
/// before #377), it lies on the nearest water deep enough to float it (<see cref="FindBerth"/>) from
/// the landing at 46.382049 N, 6.243945 E (the pier off the Quai des Alpes; lacote-tourisme.ch,
/// torpille.ch), along the shore, as before.
///
/// <para>
/// Legacy tiles (no lake bed yet, #298) model the lake as 0.12 m of water: nothing there floats a
/// 1.64 m draught, so no steamer is placed (logged) until the bathymetry is in.
/// </para>
/// </summary>
public partial class SteamerBerth : Node
{
    public const double Lat = 46.382049, Lon = 6.243945;
    public const string BerthName = "veh_steamer_nyon";
    /// <summary>The landing (<see cref="Landings.Find"/>) whose berth it lies at.</summary>
    public const string LandingName = "Nyon";
    /// <summary>The keel under the still water at a berth: the steamer's floating draught (it settles there).</summary>
    public const float FloatDraught = SteamerLines.Draught - 0.04f;

    /// <summary>Water under the keel the berth wants, beyond the draught, m.</summary>
    public const float Clearance = 0.6f;
    /// <summary>Water a few metres either side of the hull, m: no bank under its collision box or its paddle boxes.</summary>
    public const float MarginDepth = 1.2f;
    /// <summary>How far from the landing the berth is looked for, m.</summary>
    public const float SearchRadius = 400f;

    private readonly ChunkManager _chunks;
    private readonly double _e, _n;
    private bool _busy;

    public SteamerBerth(ChunkManager chunks)
    {
        Name = "SteamerBerth";
        _chunks = chunks;
        (_e, _n) = SwissProjection.ToLv95(Lat, Lon);
    }

    /// <summary>Nyon's berth alongside its pier, when the landings have one that floats the steamer.</summary>
    public static LandingBerth? PierBerth => Landings.Find(LandingName)?.Berth is { Fits: true } b ? b : null;

    /// <summary>The tile that places it: the berth's, else the old landing point's.</summary>
    private TileId Tile => PierBerth is { } b ? TileId.FromLv95(b.E, b.N) : TileId.FromLv95(_e, _n);

    public override void _Ready()
    {
        _chunks.TileEntered += OnTileEntered;
        Multiplayer.ConnectedToServer += DropLocal;
    }

    public override void _ExitTree()
    {
        _chunks.TileEntered -= OnTileEntered;
        Multiplayer.ConnectedToServer -= DropLocal;
    }

    /// <summary>A local steamer from an offline session would double the server's.</summary>
    private void DropLocal() => VehicleManager.Instance?.GetNodeOrNull(BerthName)?.QueueFree();

    private bool Decides => Net.NetworkManager.DedicatedServer || Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer;

    private async void OnTileEntered(TileId id)
    {
        if (id != Tile || _busy || !Decides) return;
        if (VehicleManager.Instance is not { } vehicles || _chunks.Origin is not { } origin) return;
        if (vehicles.GetNodeOrNull<VehicleBody>(BerthName) is { Wrecked: false }) return;
        _busy = true;
        try
        {
            // The tile's water layer (and its neighbours', where the hull reaches) arrives after the
            // tile itself: looked for every 2 s for half a minute. At once, the search found nothing
            // on the real Nyon tiles though they are 30 m deep (#303).
            var landing = origin.ToWorld(_e, _n, 0);
            Berth? found = null;
            for (int attempt = 0; attempt < 15 && found == null; attempt++)
            {
                await ToSignal(GetTree().CreateTimer(2.0), SceneTreeTimer.SignalName.Timeout);
                if (!IsInsideTree() || !Decides) return;
                found = PierBerth is { } pier ? AtPier(_chunks, origin, pier) : FindBerth(_chunks, landing);
            }
            if (found is not { } berth)
            {
                GD.Print($"[steamer] no water at Nyon deep enough for the steamer within {SearchRadius:0} m of the landing ({Survey(_chunks, landing)})");
                return;
            }
            if (vehicles.GetNodeOrNull(BerthName) is { } old) old.Free();
            // alongside the pier: its gangway on that side open onto the head
            byte gangway = PierBerth is { } at ? (byte)(1 << at.Side) : (byte)0;
            Place(vehicles, origin, berth.Keel, berth.Yaw, BerthName, gangway);
            var (e, n) = origin.ToLv95(berth.Keel);
            GD.Print(PierBerth != null
                ? $"[steamer] lies alongside the Nyon pier: LV95 {e:F0}/{n:F0}, heading {PierBerth.Heading:F0}°, {berth.Depth:F1} m of water, gangway open"
                : $"[steamer] lies at the Nyon landing: LV95 {e:F0}/{n:F0}, {berth.FromLanding:F0} m from it, {berth.Depth:F1} m of water");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Puts a steamer, nobody aboard, its keel at <paramref name="keel"/> (world, this peer's frame) heading <paramref name="yaw"/>.</summary>
    /// <param name="gangways">Gangways standing open (bit 0 port, 1 starboard).</param>
    public static string? Place(VehicleManager vehicles, WorldOrigin origin, Vector3 keel, float yaw, string name, byte gangways = 0)
    {
        var ride = Rideable.Create(RideKind.Steamer)!;
        // on the wire as LV95 (NetPlace): every peer puts it in its own frame
        var state = new VehicleState(RideKind.Steamer, origin.ToGlobal(keel), yaw, Vector3.Zero, ride.MaxHealth,
            EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0, DoorsOpen: gangways);
        return vehicles.Place(state, name);
    }

    /// <summary>
    /// A pier's berth (#377) once the water there has loaded: the keel at the floating draught
    /// under the still level, heading along the pier's face. Null while the water is not here yet.
    /// </summary>
    public static Berth? AtPier(ChunkManager chunks, WorldOrigin origin, LandingBerth pier)
    {
        var keel = Landings.KeelWorld(pier, origin, FloatDraught);
        if (!chunks.TryGetWater(keel, out float still, out _)) return null;
        keel.Y = still - FloatDraught;
        return new Berth(keel, Landings.Yaw(pier), (float)pier.Depth, 0f);
    }

    /// <summary>For the log when no berth is found: how much of the search area has water loaded, and the deepest.</summary>
    private static string Survey(ChunkManager chunks, Vector3 landing)
    {
        int wet = 0, all = 0;
        float deepest = 0f;
        for (float x = -SearchRadius; x <= SearchRadius; x += 20f)
            for (float z = -SearchRadius; z <= SearchRadius; z += 20f)
            {
                var p = landing + new Vector3(x, 0, z);
                all++;
                if (!chunks.TryGetWater(p, out float still, out _)) continue;
                wet++;
                if (chunks.TryGetHeight(p, out float bed)) deepest = Mathf.Max(deepest, still - bed);
            }
        return $"water loaded at {wet} of {all} samples, deepest {deepest:F1} m";
    }

    public readonly record struct Berth(Vector3 Keel, float Yaw, float Depth, float FromLanding);

    /// <summary>
    /// The nearest spot to <paramref name="landing"/> (world) where the whole hull floats with
    /// <see cref="Clearance"/> under its keel, lying along the shore (square to the way the bed falls
    /// away): rings out from the landing every 4 m, 12 headings tried at each spot. Null when there
    /// is none within <see cref="SearchRadius"/>.
    /// </summary>
    public static Berth? FindBerth(ChunkManager chunks, Vector3 landing)
    {
        float need = SteamerLines.Draught + Clearance;
        float Depth(Vector3 p) => chunks.TryGetWater(p, out float still, out _) && chunks.TryGetHeight(p, out float bed) ? still - bed : -1f;
        for (float r = 0f; r <= SearchRadius; r += 4f)
        {
            int around = Mathf.Max(1, Mathf.CeilToInt(Mathf.Tau * r / 4f));
            Berth? best = null;
            for (int k = 0; k < around; k++)
            {
                float a = Mathf.Tau * k / around;
                var p = landing + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r;
                float d = Depth(p);
                if (d < need) continue;
                // offshore: the way the bed falls away, from the depths 30 m round
                var offshore = Vector3.Zero;
                for (int j = 0; j < 8; j++)
                {
                    var dir = new Vector3(Mathf.Cos(Mathf.Tau * j / 8f), 0, Mathf.Sin(Mathf.Tau * j / 8f));
                    offshore += dir * Mathf.Max(0f, Depth(p + dir * 30f));
                }
                var along = offshore.LengthSquared() > 1e-4f ? offshore.Normalized().Cross(Vector3.Up) : Vector3.Forward;
                for (int t = 0; t < 12 && best == null; t++)
                {
                    var bow = along.Rotated(Vector3.Up, Mathf.Pi * t / 12f * (t % 2 == 0 ? 1f : -1f));
                    var beam = bow.Cross(Vector3.Up);
                    bool floats = true;
                    // the whole hull and a margin round it (its box, the paddle boxes, a gangway's plank):
                    // checked at its ends and sides only, it lay against the quay at Nyon with its
                    // collision box on the bank, two metres out of the water
                    for (float s = -40f; s <= 40.1f && floats; s += 5f)
                        foreach (float c in new[] { -8f, -4.5f, 0f, 4.5f, 8f })
                            if (Depth(p + bow * s + beam * c) < (Mathf.Abs(c) > 5f ? MarginDepth : need)) { floats = false; break; }
                    if (!floats) continue;
                    chunks.TryGetWater(p, out float still, out _);
                    float yaw = Mathf.Atan2(-bow.X, -bow.Z);
                    best = new Berth(p with { Y = still - SteamerLines.Draught + 0.04f }, yaw, d, r);
                }
                if (best != null) return best;
            }
        }
        return null;
    }
}
