using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Castles (#859): how a solid <see cref="Castles"/> names is dressed, by the building tradition of
/// its canton (<see cref="CastleRegion"/>) and what it is (<see cref="CastleKind"/>), and what it gets
/// besides its walls (whose stone, render, openings and corbels the building shader draws from
/// <see cref="CastleWallFlag"/>): crenellations on the open edges of its flat roofs, iron finials on
/// its peaks, wooden oriels on a Savoyard or Alpine fortress's long walls, the Swiss flag on each
/// castle's tallest solid and its canton's on the next. Appended after the facades like the doors, so
/// a tile's buildings stay one surface and one draw call. Visual only: collision is the surveyed solid.
/// Sources and rules: docs/notes/terrain/castles.md.
/// </summary>
public static partial class BuildingMeshBuilder
{
    /// <summary>Parapet and merlons, metres: the wall carried up past the walk, then teeth and gaps.</summary>
    public const float ParapetHeight = 0.9f, MerlonHeight = 0.8f, MerlonWidth = 0.75f, CrenelWidth = 0.55f, ParapetDepth = 0.5f;
    /// <summary>A flat roof's edge shorter than this is a step in the TIN, not a wall head.</summary>
    private const float ParapetMinEdge = 1.2f;
    /// <summary>A castle whose tallest solid rises less than this above its foot flies no flag.</summary>
    private const float FlagMinHeight = 12f;

    /// <summary>
    /// The castle wall's shader variant, UV2.y = <see cref="CastleWallFlag"/> + level + 2 foot + 4 x
    /// this: coursed rubble with Gothic windows and an arched corbel table; whitewashed render with
    /// dressed sandstone corners and cross-mullioned windows; a stately home's render, regular windows
    /// and painted shutters; a ruin's mossy rubble and empty openings; render under a half-timbered
    /// top storey.
    /// </summary>
    public const int CastleRubble = 0, CastleRendered = 1, CastleManor = 2, CastleRuin = 3, CastleTimbered = 4;

    /// <summary>
    /// How one castle solid is dressed: the shader variant, its wall and roof colours, and its shutters'
    /// paint as the shader picks it (under 0.45 green, under 0.75 grey-blue, else ox-blood), one a castle.
    /// </summary>
    private readonly record struct CastleDress(int Variant, Color Wall, Color Roof, CastleRegion Region, CastleKind Kind, float Paint = 0.2f);

    // The building stones and roofs of each tradition (sources in the note): Savoyard grey-beige
    // limestone rubble (Chillon), the Jura lakes' yellow Hauterive limestone, the plateau's grey-green
    // molasse sandstone, the Alps' dark gneiss and granite; brown and red clay tiles, and the grey
    // stone slabs (lauzes, piode) of Valais, Graubünden and Ticino. Kept off pure white: the lit
    // styles blow a white wall out and its detail with it. Chillon's name draws Savoyard stone 1
    // and roof 0: its limestone and its brown-grey tiles.
    private static readonly Color[][] RegionStones =
    {
        new Color[] { new(0.64f, 0.63f, 0.59f), new(0.72f, 0.69f, 0.61f), new(0.76f, 0.70f, 0.58f) }, // Savoy
        new Color[] { new(0.80f, 0.68f, 0.45f), new(0.76f, 0.66f, 0.48f) },                          // Jura
        new Color[] { new(0.62f, 0.63f, 0.55f), new(0.58f, 0.58f, 0.52f) },                          // Plateau
        new Color[] { new(0.66f, 0.64f, 0.58f), new(0.62f, 0.60f, 0.55f) },                          // East
        new Color[] { new(0.58f, 0.57f, 0.54f), new(0.62f, 0.60f, 0.56f) },                          // Alpine
        new Color[] { new(0.66f, 0.64f, 0.60f), new(0.60f, 0.59f, 0.56f) },                          // Raetia
        new Color[] { new(0.62f, 0.61f, 0.58f), new(0.56f, 0.55f, 0.53f) },                          // Ticino
    };
    private static readonly Color[][] RegionRoofs =
    {
        new Color[] { new(0.50f, 0.42f, 0.35f), new(0.53f, 0.34f, 0.26f) }, // Savoy: brown-grey, old red-brown
        new Color[] { new(0.50f, 0.36f, 0.28f), new(0.48f, 0.40f, 0.33f) }, // Jura: brown tiles
        new Color[] { new(0.56f, 0.28f, 0.20f), new(0.53f, 0.32f, 0.24f) }, // Plateau: red tiles
        new Color[] { new(0.56f, 0.28f, 0.20f), new(0.53f, 0.32f, 0.24f) }, // East: red tiles
        new Color[] { new(0.45f, 0.44f, 0.43f), new(0.41f, 0.41f, 0.43f) }, // Alpine: lauzes
        new Color[] { new(0.44f, 0.43f, 0.42f), new(0.47f, 0.43f, 0.40f) }, // Raetia: slabs
        new Color[] { new(0.47f, 0.46f, 0.44f), new(0.42f, 0.42f, 0.42f) }, // Ticino: piode
    };
    // whitewash and render: a fortress's lime-white, a stately home's cream, ochre and pale grey
    private static readonly Color[] Renders = { new(0.90f, 0.88f, 0.82f), new(0.88f, 0.85f, 0.77f) };
    private static readonly Color[] ManorRenders =
        { new(0.90f, 0.87f, 0.78f), new(0.88f, 0.86f, 0.82f), new(0.88f, 0.80f, 0.62f), new(0.82f, 0.82f, 0.80f) };
    private static readonly Color Slate = new(0.36f, 0.35f, 0.37f);

    private static Color Pick(Color[] palette, int castle, string what) =>
        palette[(int)(Core.Fnv.Unit(Castles.List[castle].Name + what) * palette.Length) % palette.Length];

    /// <summary>
    /// A solid is a keep when it is tall for its plan: whatever OSM calls its castle (Swiss mappers tag
    /// nearly every "Schloss" stately, Thun's keep included), it is dressed as a fortress.
    /// </summary>
    private static bool KeepLike(Building b, PlanBox? box)
    {
        float height = b.MaxY - b.MinY;
        return height >= 16f && height >= 1.1f * (box?.Short ?? height);
    }

    /// <summary>How a castle's solid is dressed: its tradition's materials, then what it is.</summary>
    private static CastleDress DressOf(int castle, Building b, PlanBox? box)
    {
        var region = Castles.RegionOf(castle);
        var kind = Castles.List[castle].Kind;
        var stone = Pick(RegionStones[(int)region], castle, "|stone");
        float paint = (float)Core.Fnv.Unit(Castles.List[castle].Name + "|shutters");
        var roof = Pick(RegionRoofs[(int)region], castle, "|roof");
        // a castle kept up is weathered a little: 40% of what ApplyAge does to a farmhouse
        Color Aged(Color c) => c.Lerp(ApplyAge(c, b.YearBuilt), 0.4f);

        if (kind == CastleKind.Ruin)
            return new CastleDress(CastleRuin, Aged(stone) * 0.86f, roof, region, kind);
        if (kind == CastleKind.Manor && !KeepLike(b, box))
        {
            // a country seat: rendered (the Jura's in its yellow stone), a mansard in slate now and then
            var wall = region == CastleRegion.Jura ? stone : Pick(ManorRenders, castle, "|render");
            if (region is CastleRegion.Savoy or CastleRegion.Jura or CastleRegion.Plateau && Core.Fnv.Unit(Castles.List[castle].Name + "|slate") < 0.25)
                roof = Slate;
            return new CastleDress(CastleManor, Aged(wall), roof, region, kind, paint);
        }
        int variant = region switch
        {
            CastleRegion.Plateau or CastleRegion.Raetia => CastleRendered,
            CastleRegion.East => CastleTimbered,
            _ => CastleRubble,
        };
        var walls = variant == CastleRubble ? stone : Pick(Renders, castle, "|render");
        return new CastleDress(variant, Aged(walls), roof, region, kind);
    }

    /// <summary>
    /// The crenellations, oriels, flags and finials of a tile's castles, in the door lists' layout:
    /// vertices, colours and the UV2.y flag (always 0: plain, no windows).
    /// </summary>
    private static void AppendCastles(BuildingTile tile, BuildingTypeMap types, List<Vector3> v, List<Color> c, List<float> f)
    {
        var heights = new Dictionary<int, List<int>>(); // castle -> its solids in this tile
        for (int bi = 0; bi < tile.Buildings.Count; bi++)
        {
            var b = tile.Buildings[bi];
            int castle = types.PartOf(bi) == BuildingPart.None ? types.CastleOf(bi) : -1;
            if (castle < 0 || !Drawn(b)) continue;
            var dress = DressOf(castle, b, types.Boxes[bi]);
            var stone = dress.Wall.SrgbToLinear() * 0.94f;
            AppendParapets(b, stone, dress, bi, v, c, f);
            if (dress.Variant == CastleRubble && dress.Region is CastleRegion.Savoy or CastleRegion.Jura or CastleRegion.Alpine)
                AppendOriels(b, bi, dress.Roof.SrgbToLinear(), v, c, f);
            if (dress.Kind == CastleKind.Ruin) continue; // no flag, no finials on a ruin
            if (!heights.TryGetValue(castle, out var list)) heights[castle] = list = new List<int>();
            list.Add(bi);
        }
        // the Swiss flag on each castle's tallest solid, its canton's on the next tallest
        var flags = new Dictionary<int, Vector3>();
        foreach (var (castle, solids) in heights.OrderBy(kv => kv.Key))
        {
            var ranked = solids.OrderByDescending(i => tile.Buildings[i].MaxY - tile.Buildings[i].MinY).ThenBy(i => i).Take(2).ToList();
            for (int k = 0; k < ranked.Count; k++)
            {
                var b = tile.Buildings[ranked[k]];
                if (b.MaxY - b.MinY < FlagMinHeight) continue;
                // the apex: the highest vertex (a spire's tip, a keep's roof)
                var top = new Vector3(0f, float.MinValue, 0f);
                var tri = b.Triangles;
                for (int t = 0; t + 2 < tri.Length; t += 3)
                    if (tri[t + 1] > top.Y) top = new Vector3(tri[t], tri[t + 1], tri[t + 2]);
                AppendFlag(top, k == 0 ? "CH" : Castles.List[castle].Canton, v, c, f);
                flags[ranked[k]] = top;
            }
        }
        foreach (var solids in heights.Values)
            foreach (int bi in solids)
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
    /// A parapet along every open edge of the solid's flat roofs (an edge of the flat faces that no
    /// other flat face shares, and past which nothing of the solid stands as high: a drop, not the foot
    /// of a taller tower), and on it the dress's teeth: square merlons; Ticino's swallowtail
    /// (Ghibelline) merlons, notched on top as on Bellinzona's walls; a ruin's broken ones, some gone and
    /// the rest worn to different heights; none on a stately home, whose terrace keeps a plain parapet.
    /// </summary>
    private static void AppendParapets(Building b, Color stone, CastleDress dress, int index, List<Vector3> v, List<Color> c, List<float> f)
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
            if (dress.Variant == CastleManor) continue;
            bool swallowtail = dress.Region == CastleRegion.Ticino && dress.Variant != CastleRuin;
            for (int i = 0; i < teeth; i++)
            {
                float s = ParapetDepth + i * pitch + (pitch - w) * 0.5f, top = y + ParapetHeight + MerlonHeight;
                if (dress.Variant == CastleRuin)
                {
                    double roll = Core.Fnv.Unit(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{index}|{p.X:F1}|{p.Z:F1}|{i}|merlon"));
                    if (roll < 0.35) continue;
                    top = y + ParapetHeight + MerlonHeight * (float)(0.25 + roll * 0.75);
                }
                if (swallowtail)
                {
                    // the body, then two horns with the notch between them
                    float horn = w * 0.36f;
                    Box(v, c, f, p, along, o, s, s + w, y + ParapetHeight, top - 0.35f, -ParapetDepth, 0f, stone * 1.03f);
                    Box(v, c, f, p, along, o, s, s + horn, top - 0.35f, top + 0.1f, -ParapetDepth, 0f, stone * 1.03f);
                    Box(v, c, f, p, along, o, s + w - horn, s + w, top - 0.35f, top + 0.1f, -ParapetDepth, 0f, stone * 1.03f);
                    continue;
                }
                Box(v, c, f, p, along, o, s, s + w, y + ParapetHeight, top, -ParapetDepth, 0f, stone * 1.03f);
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
    /// A flagpole on the apex and a square flag on it, facing east: the Swiss flag ("CH"), or a
    /// canton's, simplified to its colour fields on a 6 x 6 grid (the field and its main charge's
    /// colour; the bears, keys and eagles are beyond 2.4 m of cloth seen from the lake).
    /// </summary>
    private static void AppendFlag(Vector3 apex, string canton, List<Vector3> v, List<Color> c, List<float> f)
    {
        var pole = new Color(0.30f, 0.29f, 0.28f).SrgbToLinear();
        const float height = 7f, side = 2.4f;
        Box(v, c, f, apex, Vector3.Right, Vector3.Back, -0.12f, 0.12f, apex.Y - 0.5f, apex.Y + height + 0.1f, -0.12f, 0.12f, pole); // its top inside the cap
        Box(v, c, f, apex, Vector3.Right, Vector3.Back, -0.18f, 0.18f, apex.Y + height, apex.Y + height + 0.2f, -0.18f, 0.18f, pole * 1.3f);
        float cell = side / 6f, top = apex.Y + height - 0.1f;
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
            {
                float a0 = 0.12f + i * cell, y1 = top - j * cell;
                Quad(v, c, f, new Vector3(apex.X + a0, y1 - cell, apex.Z), new Vector3(apex.X + a0 + cell, y1 - cell, apex.Z),
                    new Vector3(apex.X + a0 + cell, y1, apex.Z), new Vector3(apex.X + a0, y1, apex.Z), FlagCell(canton, i, j).SrgbToLinear());
            }
    }

    private static readonly Color FlagRed = new(0.85f, 0.10f, 0.12f), FlagWhite = new(0.96f, 0.96f, 0.94f),
        FlagGreen = new(0.05f, 0.55f, 0.25f), FlagBlue = new(0.05f, 0.35f, 0.72f), FlagYellow = new(0.98f, 0.80f, 0.10f),
        FlagBlack = new(0.08f, 0.08f, 0.08f);

    /// <summary>One cell of a flag's 6 x 6 grid, column <paramref name="i"/> from the pole, row <paramref name="j"/> from the top.</summary>
    public static Color FlagCell(string canton, int i, int j)
    {
        bool cross = (i is 2 or 3 && j is >= 1 and <= 4) || (j is 2 or 3 && i is >= 1 and <= 4);
        bool middle = i is 2 or 3 && j is >= 1 and <= 4;
        return canton switch
        {
            "VD" => j < 3 ? FlagWhite : FlagGreen,                          // white over green
            "GE" => i < 3 ? FlagYellow : FlagRed,                           // the eagle's yellow, the key's red
            "FR" or "SO" or "OW" => j < 3 ? (canton == "FR" ? FlagBlack : FlagRed) : FlagWhite,
            "LU" => j < 3 ? FlagBlue : FlagWhite,
            "ZG" => j is 2 or 3 ? FlagBlue : FlagWhite,
            "VS" => i < 3 ? FlagWhite : FlagRed,
            "TI" => i < 3 ? FlagRed : FlagBlue,
            "NE" => i < 2 ? FlagGreen : i < 4 ? FlagWhite : FlagRed,
            "ZH" => i + j < 6 ? FlagWhite : FlagBlue,                       // halved bend sinister
            "TG" => i > j ? FlagGreen : FlagWhite,                          // per bend, green over white
            "BE" => Math.Abs(i - j) < 1 || Math.Abs(i - j - 1) < 1 ? FlagYellow : FlagRed, // the yellow bend
            "AG" => i < 3 ? FlagBlack : FlagBlue,
            "JU" => i < 3 ? FlagWhite : j % 2 == 0 ? FlagRed : FlagWhite,
            "BS" or "BL" => middle ? (canton == "BS" ? FlagBlack : FlagRed) : FlagWhite, // the crozier
            "AR" or "AI" or "SH" or "UR" => middle ? FlagBlack : canton == "SH" || canton == "UR" ? FlagYellow : FlagWhite,
            "SG" => middle ? FlagWhite : FlagGreen,                         // the fasces
            "NW" => middle ? FlagWhite : FlagRed,                           // the keys
            "GL" => middle ? FlagBlack : FlagRed,                           // Saint Fridolin
            "SZ" => (i == 1 && j <= 2) || (j == 1 && i <= 2) ? FlagWhite : FlagRed, // the small cross by the pole
            _ => cross ? FlagWhite : FlagRed,                               // CH, and GR (its three leagues are too fine)
        };
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
