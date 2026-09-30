using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// What a race is run over, the same on server and client: ordered checkpoints and a line to
/// measure progress along. A <b>ground</b> course is a <see cref="RaceRoute"/> (the main road from
/// the host) with a checkpoint every 200 m counted from each entrant's own grid slot; an
/// <b>air</b> course is a straight line of 3D gates, one every 400 m, each a sphere to fly through.
///
/// <para>
/// The grid is two columns staggered <see cref="GridGap"/> behind the start. On the ground every
/// entrant's finish is its own slot's arc plus the race length, so a rear slot drives no further
/// than the front one. In the air the clock starts at the entrant's own pass through gate 0
/// (a flying start at the same speed for everyone), which makes the stagger free.
/// </para>
/// </summary>
public sealed class RaceCourse
{
    public const float CheckpointEvery = 200f, OnRouteReach = 30f;
    public const float GateEvery = 400f, GateRadius = 40f;
    /// <summary>Fore-aft gap between consecutive grid slots, and the rearmost slot's arc on a route.</summary>
    public const float GridGap = 8f, GridLead = 12f;
    /// <summary>Air grid: the front slot this far behind gate 0, the two columns this far either side.</summary>
    public const float AirGridBack = 100f, AirGridSide = 12f;
    public const float MaxAirDistance = 20000f;

    public readonly bool Air;
    /// <summary>Ground only.</summary>
    public readonly RaceRoute? Route;
    /// <summary>Air only: gate 0 is the start line, the last gate the finish.</summary>
    public readonly Vector3[] Gates;
    /// <summary>Ground: the distance each entrant runs from its slot. Air: gate 0 to the last gate.</summary>
    public readonly float Length;
    /// <summary>Air only: the height the grid is held at until GO.</summary>
    public readonly float GridAltitude;

    private RaceCourse(bool air, RaceRoute? route, Vector3[] gates, float length, float gridAltitude)
    {
        Air = air;
        Route = route;
        Gates = gates;
        Length = length;
        GridAltitude = gridAltitude;
    }

    public static RaceCourse Ground(RaceRoute route, float length) => new(false, route, System.Array.Empty<Vector3>(), length, 0f);

    public static RaceCourse Airborne(Vector3[] gates, float gridAltitude)
    {
        float length = 0;
        for (int i = 1; i < gates.Length; i++) length += gates[i].DistanceTo(gates[i - 1]);
        return new(true, null, gates, length, gridAltitude);
    }

    /// <summary>The course a server sent (<c>Setup</c>): the same arrays either way.</summary>
    public static RaceCourse FromWire(bool air, Vector3[] centre, float[] width, Vector3[] gates, float length, float gridAltitude) =>
        air ? Airborne(gates, gridAltitude) : Ground(RaceRoute.FromPoints(centre, width), length);

    /// <summary>Checkpoints before the finish. Air: every gate but the last, which is the finish.</summary>
    public int Checkpoints => Air ? Gates.Length - 1 : Mathf.FloorToInt(Length / CheckpointEvery);

    /// <summary>Ground: where a slot stands along the route. Slot 0 is the front, the rearmost is at <see cref="GridLead"/>.</summary>
    public static float StartArc(int slot, int count) => GridLead + GridGap * (count - 1 - slot);

    /// <summary>Ground: the longest race a route holds for this many entrants (the front slot's finish, plus a run-off).</summary>
    public static float MaxLength(RaceRoute route, int count) => route.Length - StartArc(0, count) - 60f;

    /// <summary>Air: the direction of the course, flat.</summary>
    public Vector3 Heading => RaceRoute.Flat(Gates[1] - Gates[0]).Normalized();

    /// <summary>A grid slot: where it is and which way it faces (flat).</summary>
    public (Vector3 At, Vector3 Forward) Slot(int slot, int count)
    {
        float side = slot % 2 == 0 ? -1f : 1f;
        if (Air)
        {
            var f = Heading;
            var p = Gates[0] - f * (AirGridBack + GridGap * slot) + f.Cross(Vector3.Up) * side * AirGridSide;
            return (new Vector3(p.X, GridAltitude, p.Z), f);
        }
        var r = Route!;
        int i = r.NearestCentreIndexAt(StartArc(slot, count));
        var fwd = RaceRoute.Flat(r.Centre[Mathf.Min(i + 1, r.Centre.Count - 1)] - r.Centre[Mathf.Max(i - 1, 0)]).Normalized();
        return (r.Centre[i] + fwd.Cross(Vector3.Up) * side * r.Width[i] * 0.25f, fwd);
    }

    /// <summary>
    /// Whether moving <paramref name="from"/> → <paramref name="to"/> reached checkpoint
    /// <paramref name="k"/> (k == <see cref="Checkpoints"/>: the finish) for an entrant that
    /// started at <paramref name="startArc"/>. A gate is a sphere tested against the whole step,
    /// so a fast plane cannot jump through it between two frames.
    /// </summary>
    public bool Reached(int k, Vector3 from, Vector3 to, float startArc)
    {
        if (Air) return k < Gates.Length && SegmentDistance(Gates[k], from, to) <= GateRadius;
        float target = startArc + (k >= Checkpoints ? Length : (k + 1) * CheckpointEvery);
        int i = Route!.NearestCentre(to);
        return Route.Arc[i] >= target && Route.Off(to) < OnRouteReach;
    }

    /// <summary>Metres still to go, for the HUD.</summary>
    public float Remaining(int next, Vector3 at, float startArc)
    {
        if (!Air) return Mathf.Max(0f, startArc + Length - Route!.Arc[Route.NearestCentre(at)]);
        if (next >= Gates.Length) return 0f;
        float d = at.DistanceTo(Gates[next]);
        for (int i = next + 1; i < Gates.Length; i++) d += Gates[i].DistanceTo(Gates[i - 1]);
        return d;
    }

    private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 1e-6f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return p.DistanceTo(a + ab * t);
    }

    // ------------------------------------------------------------------------------------
    // building an air course (server)
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Gates on the straight line from <paramref name="from"/> towards <paramref name="to"/>.
    ///
    /// <para>
    /// Built on the <b>server</b> from its own terrain files: it only streams heights around
    /// players, but it holds every tile on disk, and the decimated (coarse) tiles are a few KB
    /// each — twenty of them for a 20 km course. So no client has to be trusted with the course
    /// and nothing needs sending up. The coarse lattice misses a spike between its vertices, so
    /// each gate takes the highest sample around it.
    /// </para>
    ///
    /// <para>
    /// Plane and helicopter gates sit 60 m over the terrain. Gliders (paraglider, wingsuit) have
    /// no engine: their grid is held 300 m up and each gate sits 25 m over the terrain but never
    /// below a glide slope a little steeper than the craft's own, and the course ends where the
    /// terrain rises out of gliding reach.
    /// </para>
    /// </summary>
    public static async Task<(RaceCourse? Course, string Why)> BuildAirAsync(
        IChunkSource source, WorldOrigin origin, Vector3 from, Vector3 to, RideKind kind)
    {
        var dir = RaceRoute.Flat(to - from);
        float distance = Mathf.Min(dir.Length(), MaxAirDistance);
        if (distance < GateEvery) return (null, "that is too close to race to");
        dir = dir.Normalized();

        var tiles = new System.Collections.Generic.Dictionary<TileId, ChunkGrid?>();
        const float Step = 50f;
        float back = AirGridBack + GridGap * 32 + Step;
        var heights = new System.Collections.Generic.List<float>();
        for (float s = -back; s <= distance + Step; s += Step)
        {
            var (e, n) = origin.ToLv95(from + dir * s);
            var id = TileId.FromLv95(e, n);
            if (!tiles.TryGetValue(id, out var grid))
            {
                try { grid = await source.LoadCoarseChunkAsync(id) ?? await source.LoadChunkAsync(id); }
                catch (System.Exception) { grid = null; }
                tiles[id] = grid;
            }
            if (grid == null) return (null, "there is no terrain along that course");
            heights.Add((float)grid.SampleHeight(e, n));
        }
        float Highest(float s, float reach)
        {
            float best = float.MinValue;
            for (int i = 0; i < heights.Count; i++)
                if (Mathf.Abs(-back + i * Step - s) <= reach) best = Mathf.Max(best, heights[i]);
            return best;
        }

        bool glider = kind is RideKind.Paraglider or RideKind.Wingsuit;
        // a wingsuit falls ~80 m before it flies; slopes: what the pilot is asked for, and what the craft can do
        var (loss, asked, best) = kind == RideKind.Wingsuit ? (80f, 2.2f, 2.6f) : (0f, 7.5f, 8.5f);
        float grid0 = glider ? Highest(-back / 2, back / 2) + 300f : 0f;
        var gates = new System.Collections.Generic.List<Vector3>();
        for (float s = 0; s <= distance + 1f; s += GateEvery)
        {
            var p = from + dir * s;
            float y;
            if (!glider) y = Highest(s, 150f) + 60f;
            else
            {
                float ground = Highest(s, 25f) + 25f;
                float run = s + AirGridBack + GridGap * 16;
                if (ground > grid0 - loss - run / best) break;   // out of gliding reach
                y = Mathf.Max(ground, grid0 - loss - run / asked);
            }
            gates.Add(new Vector3(p.X, y, p.Z));
        }
        if (gates.Count < 2)
            return (null, glider ? "the terrain rises out of gliding reach that way — race downhill" : "that course is too short");
        return (Airborne(gates.ToArray(), glider ? grid0 : gates[0].Y), "");
    }
}
