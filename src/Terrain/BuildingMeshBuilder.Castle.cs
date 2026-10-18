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

    // Stone and roofs, measured off photos of Chillon (grey-beige rubble, weathered brown-grey
    // tiles) and varied round them: one of each per castle, by its name. Kept off white: the lit
    // styles blow a white wall out and the masonry with it. Ordered so Chillon's name lands on
    // what it is (index 1 of both): the castle the trailer films.
    private static readonly Color[] CastleStones =
    {
        new(0.64f, 0.63f, 0.59f), // grey rubble
        new(0.72f, 0.69f, 0.61f), // Chillon's limestone
        new(0.76f, 0.70f, 0.58f), // warm limestone
        new(0.70f, 0.60f, 0.47f), // sandstone
        new(0.80f, 0.78f, 0.72f), // lime-washed
    };
    private static readonly Color[] CastleRoofs =
    {
        new(0.53f, 0.34f, 0.26f), // old red-brown tiles
        new(0.50f, 0.42f, 0.35f), // old tiles gone brown-grey (Chillon)
        new(0.36f, 0.35f, 0.37f), // slate
    };

    private static Color Pick(Color[] palette, int castle, string what) =>
        palette[(int)(Core.Fnv.Unit(Castles.List[castle].Name + what) * palette.Length) % palette.Length];

    /// <summary>A castle's stone, the same on all its solids, weathered a little by its age.</summary>
    private static Color CastleStone(int castle, ushort year)
    {
        var c = Pick(CastleStones, castle, "|stone");
        // kept up, not abandoned: a 12th-century castle gets 40% of what ApplyAge does to a farmhouse
        return c.Lerp(ApplyAge(c, year), 0.4f);
    }

    private static Color CastleRoof(int castle) => Pick(CastleRoofs, castle, "|roof");

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
            AppendOriels(b, bi, CastleRoof(castle).SrgbToLinear(), v, c, f);
            if (!tallest.TryGetValue(castle, out int t) || b.MaxY - b.MinY > tile.Buildings[t].MaxY - tile.Buildings[t].MinY)
                tallest[castle] = bi;
        }
        var flags = new Dictionary<int, Vector3>(); // solid -> where its flag stands
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
            flags[bi] = top;
        }
        for (int bi = 0; bi < tile.Buildings.Count; bi++)
            if (types.PartOf(bi) == BuildingPart.None && types.CastleOf(bi) >= 0 && Drawn(tile.Buildings[bi]))
                AppendFinials(tile.Buildings[bi], flags.TryGetValue(bi, out var flag) ? flag : null, v, c, f);
    }

    /// <summary>Iron finials, metres: a rod this tall on every peak, a knob on it.</summary>
    public const float FinialHeight = 2.2f;
    /// <summary>A roof vertex is a peak when nothing of the roof this close in plan stands as high.</summary>
    private const float PeakClear = 6f;

    /// <summary>
    /// An iron finial on every peak of the solid's pitched roofs (a tower's pyramid or cone, a
    /// hip's end): a roof vertex that tops two or more sloping faces, with no other roof vertex
    /// within <see cref="PeakClear"/> as high (so a ridge's run of equal vertices gets none), and
    /// not where the flag already stands.
    /// </summary>
    private static void AppendFinials(Building b, Vector3? flag, List<Vector3> v, List<Color> c, List<float> f)
    {
        var tops = new Dictionary<(int, int, int), (Vector3 P, int Faces)>();
        var roofPoints = new List<Vector3>();
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, d, e) = b.Tri(t);
            if (!IsRoof(a, d, e, float.MaxValue)) continue;
            roofPoints.Add(a); roofPoints.Add(d); roofPoints.Add(e);
            if (!PitchedRoof(a, d, e, out _, out _)) continue;
            var top = a.Y >= d.Y && a.Y >= e.Y ? a : d.Y >= e.Y ? d : e;
            var key = ((int)MathF.Round(top.X * 20f), (int)MathF.Round(top.Y * 20f), (int)MathF.Round(top.Z * 20f));
            tops[key] = tops.TryGetValue(key, out var seen) ? (seen.P, seen.Faces + 1) : (top, 1);
        }
        var iron = new Color(0.20f, 0.20f, 0.21f).SrgbToLinear();
        foreach (var (p, faces) in tops.Values)
        {
            if (faces < 2 || p.Y < b.MinY + 6f) continue;
            if (flag is { } at && at.DistanceTo(p) < 1f) continue;
            bool peak = true;
            foreach (var q in roofPoints)
            {
                float dx = q.X - p.X, dz = q.Z - p.Z, plan = dx * dx + dz * dz;
                if (plan > 0.09f && plan < PeakClear * PeakClear && q.Y >= p.Y - 0.05f) { peak = false; break; }
            }
            if (!peak) continue;
            Box(v, c, f, p, Vector3.Right, Vector3.Back, -0.05f, 0.05f, p.Y - 0.3f, p.Y + FinialHeight, -0.05f, 0.05f, iron);
            Box(v, c, f, p, Vector3.Right, Vector3.Back, -0.13f, 0.13f, p.Y + 1.15f, p.Y + 1.4f, -0.13f, 0.13f, iron * 1.2f);
        }
    }

    /// <summary>Wooden oriels (latrines, bretèches): one per this much wall, at most this many a solid.</summary>
    private const float OrielEvery = 22f;
    private const int OrielsPerSolid = 4;

    /// <summary>
    /// Wooden oriels hanging high on the solid's long outer walls, like the latrines over Chillon's
    /// lake front: a plank box on a beam floor, two struts down to the wall, a tiled pent roof, and
    /// a dark opening. Only on a rectangular wall run standing on the ground, 14 m long and 12 m tall
    /// at least; where along it is a hash of the solid and the run, the same on every peer.
    /// </summary>
    private static void AppendOriels(Building b, int index, Color roof, List<Vector3> v, List<Color> c, List<float> f)
    {
        var runs = new List<WallRun>();
        WallRuns(b, float.MaxValue, runs);
        var walls = new Dictionary<(Vector3, float), (WallRun Run, Vector3 On)>();
        for (int t = 0; t < runs.Count; t++)
        {
            var run = runs[t];
            if (!run.Level || run.Length < 14f || run.Top - run.Bottom < 12f || run.Bottom > b.MinY + CastleFootReach) continue;
            walls.TryAdd((run.Tangent, run.Start), (run, b.Tri(t).A));
        }
        var wood = new Color(0.38f, 0.27f, 0.17f).SrgbToLinear();
        var beam = new Color(0.27f, 0.19f, 0.12f).SrgbToLinear();
        var hole = new Color(0.06f, 0.05f, 0.05f).SrgbToLinear();
        int placed = 0;
        foreach (var (run, on) in walls.Values.OrderByDescending(w => w.Run.Length).ThenBy(w => w.Run.Start))
        {
            var along = run.Tangent;
            // outward: WallRuns turns each run's tangent so that this points away from the plan's centre
            var o = new Vector3(along.Z, 0f, -along.X);
            float plane = on.X * o.X + on.Z * o.Z;
            int count = Mathf.Max(1, Mathf.FloorToInt(run.Length / OrielEvery));
            for (int i = 0; i < count && placed < OrielsPerSolid; i++, placed++)
            {
                double roll = Core.Fnv.Unit(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{index}|{run.Start:F1}|{i}|oriel"));
                float u = run.Start + run.Length * (i + 0.25f + 0.5f * (float)roll) / count;
                var at = along * u + o * plane;
                float y = run.Top - 4.2f;
                Box(v, c, f, at, along, o, -1.4f, 1.4f, y - 0.16f, y, -0.05f, 1.22f, beam);    // the beam floor
                Box(v, c, f, at, along, o, -1.3f, 1.3f, y, y + 2.1f, -0.05f, 1.1f, wood);      // the plank box
                Box(v, c, f, at, along, o, -0.85f, 0.85f, y + 0.95f, y + 1.7f, 1.1f, 1.13f, hole); // its opening
                Vector3 P(float a, float up, float out_) => new Vector3(at.X, up, at.Z) + along * a + o * out_;
                // the pent roof, from high on the wall down past the box's front
                Quad(v, c, f, P(-1.5f, y + 2.7f, -0.05f), P(1.5f, y + 2.7f, -0.05f), P(1.5f, y + 2.0f, 1.4f), P(-1.5f, y + 2.0f, 1.4f), roof);
                // two struts from the wall out to the floor's front edge
                foreach (float a in new[] { -1.0f, 1.0f })
                {
                    Quad(v, c, f, P(a - 0.07f, y - 1.5f, -0.05f), P(a + 0.07f, y - 1.5f, -0.05f), P(a + 0.07f, y - 0.16f, 1.1f), P(a - 0.07f, y - 0.16f, 1.1f), beam);
                    Quad(v, c, f, P(a, y - 1.5f, -0.05f), P(a, y - 1.5f, 0.09f), P(a, y - 0.16f, 1.19f), P(a, y - 0.16f, 1.05f), beam);
                }
            }
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
