using Godot;

namespace UnitSport.World;

/// <summary>
/// Walkers (#1033): pedestrians on sidewalks and paths, crossing at crosswalks, following their crossing's pedestrian group
/// (a two-stage crossing's halves, #1030). Seams of the shared base (#1060, <c>Traffic.Modes.cs</c>): this file is the mode's
/// own; the defaults below walk boxes along the walk graph at random, and there is no walk graph yet (density 0 by default).
/// </summary>
public partial class Traffic
{
    /// <summary>Whether walkers have landed: their density row shows in the settings (Settings → World, the solo screen).</summary>
    public static bool WalkersReady => false;

    /// <summary>A walker: a 0.5 × 1.7 × 0.4 m orange box at 1.35 m/s ± 25 %, spawned 25 m out while filling, then 60-300 m; dropped past 400 m, drawn to 250 m.</summary>
    private static readonly MoverLook WalkerLook = new(new Vector3(0.5f, 1.7f, 0.4f), new Color(0.95f, 0.55f, 0.15f),
        Cruise: 1.35f, Spread: 0.25f, Near: 25f, SpawnMin: 60f, SpawnMax: 300f, Despawn: 400f, Drawn: 250f);

    /// <summary>
    /// The walk graph (on a worker thread): sidewalks, paths, crosswalks (<c>CPAR</c> walker lines), in <c>tiles.Origin</c>'s frame.
    /// <c>LaneGraph.Empty(tiles.Origin)</c> and <c>Add</c> each edge with <c>Kind = MoverKind.Walker</c> and keys from
    /// <c>LaneGraph.Key</c>; null builds none (no walkers spawn).
    /// </summary>
    private static LaneGraph? BuildWalkGraph(ModeTiles tiles) => null;

    /// <summary>How likely a walker spawns on an edge (0 never: a crosswalk, a junction path).</summary>
    private static float WalkerWeight(LaneEdge edge) => 1f;

    /// <summary>A walker just placed on its route: its line (<see cref="SlowMover.Lateral"/>), speed, anything of its own.</summary>
    private void WalkerSpawned(SlowMover m) { }

    /// <summary>One tick of a walker's own rules (lights, zebras, islands): the speed it wants (0 to stand; set <see cref="SlowMover.Holding"/> when it waits on purpose).</summary>
    private float StepWalker(SlowMover m, float dt) => m.Cruise;

    /// <summary>Where a walker walks across its edge: metres right of the edge's line, in its direction.</summary>
    private float WalkerLateral(SlowMover m) => m.Lateral;

    /// <summary>A walker's next leg at a junction of the walk graph.</summary>
    private (LaneEdge, bool)? NextWalk(SlowMover m, (LaneEdge Edge, bool Forward) leg) => NextAny(_walks, leg);
}
