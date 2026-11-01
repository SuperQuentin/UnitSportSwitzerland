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

    /// <summary>Metres: the most an upper wall bottom (not on the base) standing over the ground is footed, under a walk-through clearance.</summary>
    public const float OffBaseMax = 1.5f;

    /// <summary>
    /// Metres: the tallest plinth wall under a base (a basement storey, a terrace wall). Past it the data
    /// is wrong (a base 123 m up) and that piece is left as it is.
    /// </summary>
    public const float PlinthMax = 12f;

    private static readonly float[] Dips = [0.25f, 0.5f, 0.75f];

    /// <summary>One piece of footing: the base edge's two ends, the ground under each, its building, how deep it goes, and whether it is a plinth wall.</summary>
    internal readonly record struct Footing(Vector3 A, Vector3 B, float GroundA, float GroundB, int Building, float Below = FootingBelow, bool Plinth = false);

    /// <summary>
    /// The ground a tile drawn at <paramref name="ringStride"/> shows, from its full grid: the footings are
    /// sized on what the ring draws, not on the stride-1 grid the chunk build loads for the fine rings.
    /// </summary>
    internal static ChunkGrid? RingGround(ChunkGrid? full, int ringStride) =>
        full != null && ringStride > full.Stride && ringStride % full.Stride == 0 && (ChunkFormat.GridSize - 1) % ringStride == 0
            ? full.Decimate(ringStride) : full;

    /// <summary>The footings of a tile drawn at <paramref name="ringStride"/> (what the chunk build makes).</summary>
    internal static List<Footing> FootingsForRing(BuildingTile tile, ChunkGrid? full, int ringStride) =>
        Footings(tile, RingGround(full, ringStride));

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
    /// The footings of a tile's buildings over <paramref name="ground"/> (the ground the tile draws): every
    /// bottom edge of a wall (<see cref="BottomEdges"/>), in pieces, where it stands above the ground by more
    /// than 5 cm, each piece on its own. Up to <see cref="FootingMax"/> a plain footing; up to
    /// <see cref="PlinthMax"/> a plinth wall (a basement storey, a darker masonry); a piece higher than that is
    /// a data error and stays as it is. An edge not on the base is footed only up to <see cref="OffBaseMax"/>,
    /// under a walk-through clearance: an upper storey over a passage stays open. Measured over the local
    /// world's 308,759 buildings (Oct 2026): 57% sit in the ground, 28% stand up to 1 m above it somewhere,
    /// 14% 1-3 m, 1% 3-10 m and 65 more than 10 m (up to 123 m: data errors).
    /// </summary>
    internal static List<Footing> Footings(BuildingTile tile, ChunkGrid? ground, float below = FootingBelow)
    {
        var footings = new List<Footing>();
        if (ground == null) return footings;
        float Ground(Vector3 p) => (float)ground.SampleMeshHeight(tile.Id.MinE + p.X, tile.Id.MaxN - p.Z);
        for (int bi = 0; bi < tile.Buildings.Count; bi++)
        {
            var b = tile.Buildings[bi];
            if (!Drawn(b)) continue;
            foreach (var (p, q, onBase) in BottomEdges(b))
            {
                float len = new Vector2(q.X - p.X, q.Z - p.Z).Length();
                int pieces = Mathf.Max(1, Mathf.CeilToInt(len / FootingStep));
                for (int i = 0; i < pieces; i++)
                {
                    var e0 = p.Lerp(q, (float)i / pieces);
                    var e1 = p.Lerp(q, (float)(i + 1) / pieces);
                    float g0 = Ground(e0), g1 = Ground(e1);
                    float gap = Mathf.Max(e0.Y - g0, e1.Y - g1);
                    if (gap <= 0.05f) continue;                          // standing in the ground
                    if (gap > (onBase ? PlinthMax : OffBaseMax)) continue; // data error / a passage: as it is
                    float a0 = Mathf.Min(g0, e0.Y), a1 = Mathf.Min(g1, e1.Y);
                    // the foot is a straight line between the two ends; the ground between them (a stride-1 quad
                    // mesh, or a coarse ring's plane) can dip under it, so the piece goes as deep as the deepest dip
                    float dip = 0;
                    foreach (float u in Dips)
                        dip = Mathf.Max(dip, Mathf.Lerp(a0, a1, u) - Ground(e0.Lerp(e1, u)));
                    footings.Add(new Footing(e0, e1, a0, a1, bi, below + dip, onBase && gap > FootingMax));
                }
            }
        }
        return footings;
    }

    /// <summary>
    /// The footings into the extra geometry: foundation masonry, the building's own wall colour
    /// darkened (a castle's stone, a house's render gone to its plinth), flagged <see cref="FootingFlag"/>.
    /// </summary>
    private static void AppendFootings(BuildingTile tile, BuildingTypeMap types, List<Footing> footings,
        List<Vector3> v, List<Color> c, List<float> f)
    {
        var colours = new Dictionary<int, Color>();
        foreach (var ft in footings)
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
                ft.B, ft.A, ft.Plinth ? PlinthShade(colour) : colour);
            for (int i = start; i < f.Count; i++) f[i] = FootingFlag;
        }
    }

    /// <summary>A plinth wall's masonry: a basement storey darker than the footing and the wall above it.</summary>
    private static Color PlinthShade(Color footing) => new(footing.R * 0.75f, footing.G * 0.75f, footing.B * 0.75f, 1f);

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
