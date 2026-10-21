using Godot;

namespace UnitSport.World;

/// <summary>
/// Riders (#1056): bikes on bike lanes, bike paths and the overlay's cycleways (contraflow too), through junctions on their own
/// paths (<c>JPTH</c> bike paths) and bike groups. Seams of the shared base (#1060, <c>Traffic.Modes.cs</c>): this file is the
/// mode's own; the defaults below ride boxes along the ride graph at random, and there is no ride graph yet (density 0 by default).
/// </summary>
public partial class Traffic
{
    /// <summary>Whether riders have landed: their density row shows in the settings (Settings → World, the solo screen).</summary>
    public static bool RidersReady => false;

    /// <summary>A rider: a 0.6 × 1.7 × 1.8 m green box at 5 m/s ± 25 %, spawned 40 m out while filling, then 100-600 m; dropped past 800 m, drawn to 400 m.</summary>
    private static readonly MoverLook RiderLook = new(new Vector3(0.6f, 1.7f, 1.8f), new Color(0.25f, 0.75f, 0.3f),
        Cruise: 5f, Spread: 0.25f, Near: 40f, SpawnMin: 100f, SpawnMax: 600f, Despawn: 800f, Drawn: 400f);

    /// <summary>
    /// The ride graph (on a worker thread): bike lanes, bike paths, cycleways, <c>JPTH</c> bike paths, roads riders share, in
    /// <c>tiles.Origin</c>'s frame. <c>LaneGraph.Empty(tiles.Origin)</c> and <c>Add</c> each edge with <c>Kind = MoverKind.Rider</c>
    /// and keys from <c>LaneGraph.Key</c>; null builds none (no riders spawn).
    /// </summary>
    private static LaneGraph? BuildRideGraph(ModeTiles tiles) => null;

    /// <summary>How likely a rider spawns on an edge (0 never: a junction path).</summary>
    private static float RiderWeight(LaneEdge edge) => 1f;

    /// <summary>A rider just placed on its route: its line (<see cref="SlowMover.Lateral"/>), speed, anything of its own.</summary>
    private void RiderSpawned(SlowMover m) { }

    /// <summary>
    /// One tick of a rider's own rules (bike signals, the bike box, giving way): the speed it wants (0 to stand; set
    /// <see cref="SlowMover.Holding"/> when it waits on purpose, <see cref="SlowMover.InLane"/> while it rides in a car lane, so cars follow it).
    /// </summary>
    private float StepRider(SlowMover m, float dt) => m.Cruise;

    /// <summary>Where a rider rides across its edge: metres right of the edge's line, in its direction (a bike lane's centre).</summary>
    private float RiderLateral(SlowMover m) => m.Lateral;

    /// <summary>A rider's next leg at a junction of the ride graph.</summary>
    private (LaneEdge, bool)? NextRide(SlowMover m, (LaneEdge Edge, bool Forward) leg) => NextAny(_rides, leg);
}
