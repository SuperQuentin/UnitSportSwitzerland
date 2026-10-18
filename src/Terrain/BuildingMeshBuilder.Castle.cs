using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Castles (#859): what a solid <see cref="Castles"/> names gets besides its stone walls (whose
/// slits, windows and corbels the building shader draws from <see cref="CastleWallFlag"/>):
/// crenellations along the open edges of its flat roofs (the wall walks and the tower tops) and a
/// flag on the tallest solid of each castle. Appended after the facades like the doors, so a tile's
/// buildings stay one surface and one draw call. Visual only: collision is the surveyed solid.
/// </summary>
public static partial class BuildingMeshBuilder
{
    /// <summary>Parapet and merlons, metres: the wall carried up past the walk, then teeth and gaps.</summary>
    public const float ParapetHeight = 0.9f, MerlonHeight = 0.8f, MerlonWidth = 0.75f, CrenelWidth = 0.55f, ParapetDepth = 0.5f;
    /// <summary>A flat roof's edge shorter than this is a step in the TIN, not a wall head.</summary>
    private const float ParapetMinEdge = 1.2f;
    /// <summary>A castle whose tallest solid rises less than this above its foot flies no flag.</summary>
    private const float FlagMinHeight = 12f;

    /// <summary>Roofs of a castle: old tiles, deeper than a village's.</summary>
    private static readonly Color CastleRoof = new(0.47f, 0.26f, 0.19f);

    // limestone, grey rubble, lime render, warm sandstone: one per castle, by its name. Kept off
    // white: the lit styles blow a white wall out and the masonry with it
    private static readonly Color[] CastleStones =
    {
        new(0.78f, 0.74f, 0.64f), new(0.66f, 0.65f, 0.61f), new(0.82f, 0.80f, 0.74f), new(0.74f, 0.64f, 0.50f),
    };

    /// <summary>A castle's stone, the same on all its solids, weathered a little by its age.</summary>
    private static Color CastleStone(int castle, ushort year)
    {
        var c = CastleStones[(int)(Core.Fnv.Unit(Castles.List[castle].Name + "|stone") * CastleStones.Length) % CastleStones.Length];
        // kept up, not abandoned: a 12th-century castle gets 40% of what ApplyAge does to a farmhouse
        return c.Lerp(ApplyAge(c, year), 0.4f);
    }

    /// <summary>
    /// The crenellations and flags of a tile's castles, in the door lists' layout: vertices,
    /// colours and the UV2.y flag (always 0: plain, no windows).
    /// </summary>
    private static void AppendCastles(BuildingTile tile, BuildingTypeMap types, List<Vector3> v, List<Color> c, List<float> f)
    {
        var tallest = new Dictionary<int, int>(); // castle -> its tallest solid in this tile
        for (int bi = 0; bi < tile.Buildings.Count; bi++)
        {
            var b = tile.Buildings[bi];
            int castle = types.PartOf(bi) == BuildingPart.None ? types.CastleOf(bi) : -1;
            if (castle < 0 || !Drawn(b)) continue;
            var stone = CastleStone(castle, b.YearBuilt).SrgbToLinear() * 0.94f;
            AppendParapets(b, stone, v, c, f);
            if (!tallest.TryGetValue(castle, out int t) || b.MaxY - b.MinY > tile.Buildings[t].MaxY - tile.Buildings[t].MinY)
                tallest[castle] = bi;
        }
        foreach (var bi in tallest.Values.Order())
        {
            var b = tile.Buildings[bi];
            if (b.MaxY - b.MinY < FlagMinHeight) continue;
            // the apex: the highest vertex (a spire's tip, a keep's roof)
            var top = new Vector3(0f, float.MinValue, 0f);
            var tri = b.Triangles;
            for (int k = 0; k + 2 < tri.Length; k += 3)
                if (tri[k + 1] > top.Y) top = new Vector3(tri[k], tri[k + 1], tri[k + 2]);
            AppendFlag(top, v, c, f);
        }
    }

    /// <summary>
    /// A parapet with merlons along every open edge of the solid's flat roofs: an edge of the flat
    /// faces that no other flat face shares, and past which nothing of the solid stands as high (a
    /// drop, not the foot of a taller tower).
    /// </summary>
    private static void AppendParapets(Building b, Color stone, List<Vector3> v, List<Color> c, List<float> f)
    {
        var flats = new List<int>();
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, d, e) = b.Tri(t);
            if (FlatRoof(a, d, e, b.MinY)) flats.Add(t);
        }
        if (flats.Count == 0) return;

        static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 20f), (int)MathF.Round(p.Y * 20f), (int)MathF.Round(p.Z * 20f));
        var edges = new Dictionary<((int, int, int), (int, int, int)), (int Count, Vector3 P, Vector3 Q, Vector3 R)>();
        foreach (int t in flats)
        {
            var (a, d, e) = b.Tri(t);
            for (int k = 0; k < 3; k++)
            {
                var p = k == 0 ? a : k == 1 ? d : e;
                var q = k == 0 ? d : k == 1 ? e : a;
                var r = k == 0 ? e : k == 1 ? a : d;
                var kp = Key(p);
                var kq = Key(q);
                var key = kp.CompareTo(kq) < 0 ? (kp, kq) : (kq, kp);
                edges[key] = edges.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.P, seen.Q, seen.R) : (1, p, q, r);
            }
        }

        foreach (var edge in edges.Values)
        {
            var (count, p, q, r) = edge;
            if (count != 1) continue;
            var along = new Vector3(q.X - p.X, 0f, q.Z - p.Z);
            float len = along.Length();
            if (len < ParapetMinEdge || Mathf.Abs(q.Y - p.Y) > 0.3f) continue;
            along /= len;
            // outward (away from the flat face's third corner) always on the same side of along:
            // every edge then runs the same way round the roof, and starting each one a parapet's
            // depth in leaves each corner to exactly one of its two edges (no coplanar overlap)
            var o = new Vector3(along.Z, 0f, -along.X);
            if (o.Dot(r - p) > 0f)
            {
                (p, q) = (q, p);
                along = -along;
                o = -o;
            }
            float y = Mathf.Max(p.Y, q.Y);
            var mid = (p + q) * 0.5f + o * 0.4f;
            if (SurfaceAbove(b, mid.X, mid.Z, y - 0.5f)) continue;

            // the parapet, its outer face flush with the wall below, then the teeth on it
            Box(v, c, f, p, along, o, ParapetDepth, len, y, y + ParapetHeight, -ParapetDepth, 0f, stone);
            float run = len - ParapetDepth;
            int teeth = Mathf.Max(1, Mathf.RoundToInt((run + CrenelWidth) / (MerlonWidth + CrenelWidth)));
            float pitch = run / teeth;
            float w = Mathf.Min(MerlonWidth, pitch * 0.6f);
            for (int i = 0; i < teeth; i++)
            {
                float s = ParapetDepth + i * pitch + (pitch - w) * 0.5f;
                Box(v, c, f, p, along, o, s, s + w, y + ParapetHeight, y + ParapetHeight + MerlonHeight,
                    -ParapetDepth, 0f, stone * 1.03f);
            }
        }
    }

    /// <summary>Whether a non-wall face of the solid stands at or above <paramref name="y"/> over a plan point.</summary>
    private static bool SurfaceAbove(Building b, float x, float z, float y)
    {
        var t = b.Triangles;
        for (int i = 0; i + 8 < t.Length; i += 9)
        {
            if (t[i + 1] < y && t[i + 4] < y && t[i + 7] < y) continue;
            float ax = t[i], az = t[i + 2], bx = t[i + 3], bz = t[i + 5], cx = t[i + 6], cz = t[i + 8];
            float area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            if (MathF.Abs(area) < 0.01f) continue;
            float w0 = ((bx - x) * (cz - z) - (bz - z) * (cx - x)) / area;
            float w1 = ((cx - x) * (az - z) - (cz - z) * (ax - x)) / area;
            float w2 = 1f - w0 - w1;
            if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
            if (w0 * t[i + 1] + w1 * t[i + 4] + w2 * t[i + 7] >= y) return true;
        }
        return false;
    }

    /// <summary>
    /// A flagpole on the apex and a Swiss flag on it: a square red cloth with the white cross (arms
    /// 1/6 wide and 1/6 longer than wide, drawn on a 6 x 6 grid's middle two rows and columns), facing east.
    /// </summary>
    private static void AppendFlag(Vector3 apex, List<Vector3> v, List<Color> c, List<float> f)
    {
        var pole = new Color(0.30f, 0.29f, 0.28f).SrgbToLinear();
        var red = new Color(0.85f, 0.10f, 0.12f).SrgbToLinear();
        var white = new Color(0.96f, 0.96f, 0.94f).SrgbToLinear();
        const float height = 7f, side = 2.4f;
        Box(v, c, f, apex, Vector3.Right, Vector3.Back, -0.12f, 0.12f, apex.Y - 0.5f, apex.Y + height + 0.1f, -0.12f, 0.12f, pole); // its top inside the cap
        Box(v, c, f, apex, Vector3.Right, Vector3.Back, -0.18f, 0.18f, apex.Y + height, apex.Y + height + 0.2f, -0.18f, 0.18f, pole * 1.3f);
        float cell = side / 6f, top = apex.Y + height - 0.1f;
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
            {
                bool cross = (i is 2 or 3 && j is >= 1 and <= 4) || (j is 2 or 3 && i is >= 1 and <= 4);
                float a0 = 0.12f + i * cell, y1 = top - j * cell;
                Quad(v, c, f, new Vector3(apex.X + a0, y1 - cell, apex.Z), new Vector3(apex.X + a0 + cell, y1 - cell, apex.Z),
                    new Vector3(apex.X + a0 + cell, y1, apex.Z), new Vector3(apex.X + a0, y1, apex.Z), cross ? white : red);
            }
    }

    // both windings, like the doors: the building shader culls back faces and the TIN's own faces are wound either way
    private static void Quad(List<Vector3> v, List<Color> c, List<float> f, Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color col)
    {
        v.Add(a); v.Add(b); v.Add(cc); v.Add(a); v.Add(cc); v.Add(d);
        v.Add(a); v.Add(cc); v.Add(b); v.Add(a); v.Add(d); v.Add(cc);
        for (int i = 0; i < 12; i++) c.Add(col);
        for (int i = 0; i < 12; i++) f.Add(0f);
    }

    /// <summary>A box in an edge's frame: along [a0, a1] from <paramref name="at"/>, absolute heights [y0, y1], out [o0, o1].</summary>
    private static void Box(List<Vector3> v, List<Color> c, List<float> f, Vector3 at, Vector3 along, Vector3 out_,
        float a0, float a1, float y0, float y1, float o0, float o1, Color col)
    {
        Vector3 P(float a, float y, float o) => new Vector3(at.X, y, at.Z) + along * a + out_ * o;
        var side = col * 0.9f;
        Quad(v, c, f, P(a0, y0, o1), P(a1, y0, o1), P(a1, y1, o1), P(a0, y1, o1), col);  // out
        Quad(v, c, f, P(a0, y0, o0), P(a1, y0, o0), P(a1, y1, o0), P(a0, y1, o0), side); // in
        Quad(v, c, f, P(a0, y1, o0), P(a1, y1, o0), P(a1, y1, o1), P(a0, y1, o1), col);  // top
        Quad(v, c, f, P(a0, y0, o0), P(a0, y1, o0), P(a0, y1, o1), P(a0, y0, o1), side); // ends
        Quad(v, c, f, P(a1, y0, o0), P(a1, y1, o0), P(a1, y1, o1), P(a1, y0, o1), side);
    }
}
