using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// The paddle steamer lies at the CGN landing at Nyon (#303): 46.382049 N, 6.243945 E (the pier off
/// the Quai des Alpes; lacote-tourisme.ch, torpille.ch). Whenever the tile under it comes into the
/// streamed rings and no steamer of the berth is there, one is placed on the nearest water deep
/// enough to float it (<see cref="FindBerth"/>), lying along the shore; taken away, it is put back
/// the next time the area loads. One check per tile load, nothing per frame. The server decides and
/// spawns it (<see cref="VehicleManager.Place"/>), so every player sees the same one; offline the
/// client does. Piers as structures are a follow-up: today it lies off the shore at the landing.
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

    /// <summary>Water under the keel the berth wants, beyond the draught, m.</summary>
    public const float Clearance = 1.0f;
    /// <summary>How far from the landing the berth is looked for, m.</summary>
    public const float SearchRadius = 400f;

    private readonly ChunkManager _chunks;
    private readonly TileId _tile;
    private readonly double _e, _n;
    private bool _busy;

    public SteamerBerth(ChunkManager chunks)
    {
        Name = "SteamerBerth";
        _chunks = chunks;
        (_e, _n) = SwissProjection.ToLv95(Lat, Lon);
        _tile = TileId.FromLv95(_e, _n);
    }

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
        if (id != _tile || _busy || !Decides) return;
        if (VehicleManager.Instance is not { } vehicles || _chunks.Origin is not { } origin) return;
        if (vehicles.GetNodeOrNull<VehicleBody>(BerthName) is { Wrecked: false }) return;
        _busy = true;
        try
        {
            // the water layer comes with the tile a moment later on some sources: give it a second
            await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree() || !Decides) return;
            var landing = origin.ToWorld(_e, _n, 0);
            if (FindBerth(_chunks, landing) is not { } berth)
            {
                GD.Print($"[steamer] no water at Nyon deep enough for the steamer within {SearchRadius:0} m of the landing (legacy lake tiles are 0.12 m deep until #298)");
                return;
            }
            if (vehicles.GetNodeOrNull(BerthName) is { } old) old.Free();
            Place(vehicles, origin, berth.Keel, berth.Yaw, BerthName);
            var (e, n) = origin.ToLv95(berth.Keel);
            GD.Print($"[steamer] lies at the Nyon landing: LV95 {e:F0}/{n:F0}, {berth.FromLanding:F0} m from it, {berth.Depth:F1} m of water");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Puts a steamer, nobody aboard, its keel at <paramref name="keel"/> (world, this peer's frame) heading <paramref name="yaw"/>.</summary>
    public static string? Place(VehicleManager vehicles, WorldOrigin origin, Vector3 keel, float yaw, string name)
    {
        var ride = Rideable.Create(RideKind.Steamer)!;
        // on the wire as LV95 (NetPlace): every peer puts it in its own frame
        var state = new VehicleState(RideKind.Steamer, origin.ToGlobal(keel), yaw, Vector3.Zero, ride.MaxHealth,
            EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0);
        return vehicles.Place(state, name);
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
                    foreach (float s in new[] { -36f, -18f, 18f, 36f })
                        foreach (float c in new[] { -4.5f, 4.5f })
                            if (Depth(p + bow * s + beam * c) < need) { floats = false; break; }
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
