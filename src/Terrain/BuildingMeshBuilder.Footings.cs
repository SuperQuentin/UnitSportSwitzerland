using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Footings: a building's base carried down to the ground where the ground falls away under it.
///
/// <para>
/// The preprocessor seats every solid on the median ground of its footprint, its base
/// <see cref="FoundationDepth"/> under it (BuildingExtractor). On a slope that buries the uphill side
/// and leaves the downhill side standing on air: a dark slot under a house, daylight under a castle's
/// wall. Each wall's bottom edge, cut into lengths of at most <see cref="FootingStep"/>, is given a
/// footing down to the ground the tile's terrain mesh draws there, and
/// <see cref="FootingBelow"/> into it. Only a couple of metres are filled: a base more than
/// <see cref="FootingMax"/> above the ground is not a building on a slope but one the data leaves in
/// the air (on a bridge, over an underpass, or simply wrong), and is left as it is.
/// </para>
///
/// <para>
/// A pure function of the tile and its terrain, so server and clients build the same footings, and
/// they collide like the wall above them.
/// </para>
/// </summary>
public static partial class BuildingMeshBuilder
{
    /// <summary>
    /// Metres: the most of a base a footing fills, its longest piece, and how far it goes into the
    /// ground (past the 0.35 m the drawn terrain sinks under a road blend, TerrainMeshBuilder.VisualBlendClearance).
    /// </summary>
    public const float FootingMax = 3f, FootingStep = 4f, FootingBelow = 0.5f;

    /// <summary>
    /// UV2.y of a footing's vertices: plain foundation masonry, no facade (its UV.y is -1 like a
    /// door's), and in the realistic style not the roof texture that UV.y &lt; 0 otherwise means.
    /// </summary>
    public const float FootingFlag = -1f;

    /// <summary>One piece of footing: the base edge's two ends, the ground under each, and its building.</summary>
    internal readonly record struct Footing(Vector3 A, Vector3 B, float GroundA, float GroundB, int Building, float Below = FootingBelow);

    /// <summary>The footings of a tile drawn at <paramref name="ringStride"/>, from its full grid as the chunk build gets it (a ring finer than the coarse tile loads the stride-1 grid, the coarse ring its stride-10 one).</summary>
    internal static List<Footing> FootingsForRing(BuildingTile tile, ChunkGrid? full, int ringStride) =>
        Footings(tile, full != null && ringStride >= ChunkFormat.CoarseStride ? full.Decimate(ringStride) : full);

    /// <summary>
    /// A solid's wall bottoms: the horizontal edges of its wall triangles that stand on its base (both
    /// ends within 5 cm of <see cref="Building.MinY"/>), and those that do not (a wall that starts above
    /// its base: an annex on a ledge, an upper storey, a wall on a terrace), up to a storey above it. An
    /// off-base edge is a bottom only if the wall is above it and no wall triangle is below it.
    /// </summary>
    internal static List<(Vector3 P, Vector3 Q, bool OnBase)> BottomEdges(Building b)
    {
        var edges = new List<(Vector3, Vector3, bool)>();
        var seen = new Dictionary<(int, int, int, int, int, int), int>();
        static (int, int, int) K(Vector3 v) => (Mathf.RoundToInt(v.X * 100f), Mathf.RoundToInt(v.Y * 100f), Mathf.RoundToInt(v.Z * 100f));
        static (int, int, int, int, int, int) Key(Vector3 p, Vector3 q)
        {
            var (a, c) = (K(p), K(q));
            return a.CompareTo(c) <= 0 ? (a.Item1, a.Item2, a.Item3, c.Item1, c.Item2, c.Item3) : (c.Item1, c.Item2, c.Item3, a.Item1, a.Item2, a.Item3);
        }
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, c, d) = b.Tri(t);
            if (IsRoof(a, c, d, float.MaxValue)) continue;
            for (int k = 0; k < 3; k++)
            {
                var p = k == 0 ? a : k == 1 ? c : d;
                var q = k == 0 ? c : k == 1 ? d : a;
                var key = Key(p, q);
                seen[key] = seen.GetValueOrDefault(key) + 1;
            }
        }
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, c, d) = b.Tri(t);
            if (IsRoof(a, c, d, float.MaxValue)) continue;
            for (int k = 0; k < 3; k++)
            {
                var p = k == 0 ? a : k == 1 ? c : d;
                var q = k == 0 ? c : k == 1 ? d : a;
                var r = k == 0 ? d : k == 1 ? a : c;
                float len = new Vector2(q.X - p.X, q.Z - p.Z).Length();
                if (len < 0.05f) continue;
                if (p.Y <= b.MinY + 0.05f && q.Y <= b.MinY + 0.05f) { edges.Add((p, q, true)); continue; }
                if (Mathf.Abs(p.Y - q.Y) < 0.05f && p.Y < b.MinY + 4f && r.Y > p.Y + 0.1f && seen[Key(p, q)] == 1)
                    edges.Add((p, q, false));
            }
        }
        return edges;
    }

    /// <summary>
    /// The footings of a tile's buildings over <paramref name="ground"/> (the tile's terrain): every
    /// bottom edge of a wall (both ends on the solid's base), in pieces, where it stands above the
    /// ground by more than 5 cm; none at all for a building whose base is anywhere more than
    /// <see cref="FootingMax"/> above it. Measured over the local world's 308,759 buildings (Oct
    /// 2026): 57% sit in the ground, 28% stand up to 1 m above it somewhere, 14% 1-3 m, 1% 3-10 m and
    /// 65 more than 10 m (up to 123 m: data errors).
    /// </summary>
    internal static List<Footing> Footings(BuildingTile tile, ChunkGrid? ground)
    {
        var footings = new List<Footing>();
        if (ground == null) return footings;
        float Ground(Vector3 p) => (float)ground.SampleMeshHeight(tile.Id.MinE + p.X, tile.Id.MaxN - p.Z);
        var mine = new List<Footing>();
        for (int bi = 0; bi < tile.Buildings.Count; bi++)
        {
            var b = tile.Buildings[bi];
            if (!Drawn(b)) continue;
            mine.Clear();
            bool inAir = false;
            for (int t = 0; t < b.TriangleCount && !inAir; t++)
            {
                var (a, c, d) = b.Tri(t);
                if (IsRoof(a, c, d, float.MaxValue)) continue;
                for (int k = 0; k < 3 && !inAir; k++)
                {
                    var p = k == 0 ? a : k == 1 ? c : d;
                    var q = k == 0 ? c : k == 1 ? d : a;
                    if (p.Y > b.MinY + 0.05f || q.Y > b.MinY + 0.05f) continue;
                    float len = new Vector2(q.X - p.X, q.Z - p.Z).Length();
                    if (len < 0.05f) continue;
                    int pieces = Mathf.Max(1, Mathf.CeilToInt(len / FootingStep));
                    for (int i = 0; i < pieces && !inAir; i++)
                    {
                        var e0 = p.Lerp(q, (float)i / pieces);
                        var e1 = p.Lerp(q, (float)(i + 1) / pieces);
                        float g0 = Ground(e0), g1 = Ground(e1);
                        float gap0 = e0.Y - g0, gap1 = e1.Y - g1;
                        if (gap0 > FootingMax || gap1 > FootingMax) { inAir = true; break; } // the whole building in the air
                        if (Mathf.Max(gap0, gap1) <= 0.05f) continue;                     // standing in the ground
                        mine.Add(new Footing(e0, e1, Mathf.Min(g0, e0.Y), Mathf.Min(g1, e1.Y), bi));
                    }
                }
            }
            // a building some of whose base is more than FootingMax up is left as it is, all of it:
            // half a footing under a house in the air would be worse than none
            if (!inAir) footings.AddRange(mine);
        }
        return footings;
    }

    /// <summary>
    /// The footings into the extra geometry: foundation masonry, the building's own wall colour
    /// darkened (a castle's stone, a house's render gone to its plinth), flagged <see cref="FootingFlag"/>.
    /// </summary>
    private static void AppendFootings(BuildingTile tile, BuildingTypeMap types, ChunkGrid? ground,
        List<Vector3> v, List<Color> c, List<float> f)
    {
        var colours = new Dictionary<int, Color>();
        foreach (var ft in Footings(tile, ground))
        {
            if (!colours.TryGetValue(ft.Building, out var colour))
            {
                var b = tile.Buildings[ft.Building];
                var type = types.TypeOf(ft.Building);
                int castle = types.PartOf(ft.Building) == BuildingPart.None ? types.CastleOf(ft.Building) : -1;
                var wall = castle >= 0 ? DressOf(castle, b, types.Boxes[ft.Building]).Wall : WallColor(KindOf(b, type), b.YearBuilt, type);
                colours[ft.Building] = colour = (wall * 0.7f).SrgbToLinear();
                colour.A = 1f;
            }
            int start = f.Count;
            Quad(v, c, f, new Vector3(ft.A.X, ft.GroundA - ft.Below, ft.A.Z), new Vector3(ft.B.X, ft.GroundB - ft.Below, ft.B.Z),
                ft.B, ft.A, colour);
            for (int i = start; i < f.Count; i++) f[i] = FootingFlag;
        }
    }

    /// <summary>The footings' collision, both windings (the shape is one-sided).</summary>
    private static void FootingCollision(BuildingTile tile, ChunkGrid? ground, List<Vector3> faces)
    {
        foreach (var ft in Footings(tile, ground))
        {
            Vector3 a = new(ft.A.X, ft.GroundA - ft.Below, ft.A.Z), b = new(ft.B.X, ft.GroundB - ft.Below, ft.B.Z);
            faces.Add(a); faces.Add(b); faces.Add(ft.B); faces.Add(a); faces.Add(ft.B); faces.Add(ft.A);
            faces.Add(a); faces.Add(ft.B); faces.Add(b); faces.Add(a); faces.Add(ft.A); faces.Add(ft.B);
        }
    }
}
