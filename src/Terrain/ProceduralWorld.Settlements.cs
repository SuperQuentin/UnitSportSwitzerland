using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

public sealed partial class ProceduralWorld
{
    // ---- layout ------------------------------------------------------------------------------
    //
    // Villages sit on the main road, one per 2.6 km slot along the valley (slot 0, at the centre,
    // always exists). Each is a row of houses either side of the road plus one or two side
    // streets climbing away from the river. Farms are scattered on their own over the floor and
    // the lower slopes, with alpine huts higher up. Everything is planned in LV95 and handed to
    // whichever tile holds a building's centre, as the real extractor does.

    private const double VillageSpacing = 2600;
    private const double RoadStep = 3;   // polyline spacing in x; under the 4 m the drape expects

    private sealed record Street(List<(double E, double N)> Points, RoadClass Class);

    private readonly record struct Footprint(double E, double N, double UE, double UN,
        double HalfLength, double HalfWidth);

    private sealed record Plan(Footprint Rect, BuildingKind Kind, double WallHeight, double Pitch,
        byte Floors, ushort Year, bool Tower = false);

    private sealed record Village(double X, double HalfLength, List<Street> Streets, List<Plan> Buildings);

    private readonly Dictionary<int, Village?> _villages = new();

    private Village? VillageAt(int slot)
    {
        lock (_villages)
            if (_villages.TryGetValue(slot, out var cached)) return cached;
        var village = PlanVillage(slot);
        lock (_villages) _villages[slot] = village;
        return village;
    }

    private IEnumerable<Village> VillagesNear(double minX, double maxX)
    {
        int first = (int)Math.Floor((minX - 1200) / VillageSpacing);
        int last = (int)Math.Ceiling((maxX + 1200) / VillageSpacing);
        for (int slot = first; slot <= last; slot++)
            if (VillageAt(slot) is { } v && v.X + v.HalfLength + 450 >= minX && v.X - v.HalfLength - 450 <= maxX)
                yield return v;
    }

    /// <summary>A point on the main road and its unit direction and north-side normal, in LV95.</summary>
    private ((double E, double N) P, (double E, double N) T, (double E, double N) Nrm) RoadFrame(double x)
    {
        double slope = AxisSlope(x);
        double len = Math.Sqrt(1 + slope * slope);
        var t = (1 / len, slope / len);
        return ((CenterE + x, CenterN + Axis(x)), t, (-t.Item2, t.Item1));
    }

    private Village? PlanVillage(int slot)
    {
        var rng = new Random(unchecked(slot * 7919 + 104729));
        if (slot != 0 && rng.NextDouble() > 0.8) return null;
        double x = slot * VillageSpacing + (slot == 0 ? 0 : (rng.NextDouble() - 0.5) * 1400);
        double halfLength = slot == 0 ? 330 : 160 + rng.NextDouble() * 260;
        var streets = new List<Street>();
        var plans = new List<Plan>();

        // side streets, north of the road, away from the river
        var streetXs = new List<double> { x + 25 };
        if (halfLength > 260) streetXs.Add(x + (rng.NextDouble() < 0.5 ? -1 : 1) * halfLength * 0.6);
        foreach (double sx in streetXs)
        {
            var (p, t, nrm) = RoadFrame(sx);
            double length = 170 + rng.NextDouble() * 150;
            double bend = (rng.NextDouble() - 0.5) * 0.5;   // radians over the street's length
            var points = new List<(double E, double N)>();
            double e = p.E, n = p.N, heading = Math.Atan2(nrm.N, nrm.E);
            for (double s = 0; s <= length; s += RoadStep)
            {
                points.Add((e, n));
                heading += bend * RoadStep / length;
                e += Math.Cos(heading) * RoadStep;
                n += Math.Sin(heading) * RoadStep;
            }
            streets.Add(new Street(points, RoadClass.Minor));

            // houses along both sides of it, starting clear of the main road's frontage
            for (int side = -1; side <= 1; side += 2)
                for (double s = 34 + rng.NextDouble() * 8; s < length - 8;)
                {
                    int i = Math.Min((int)(s / RoadStep), points.Count - 2);
                    var a = points[i];
                    var b = points[i + 1];
                    double dl = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
                    var dir = ((b.E - a.E) / dl, (b.N - a.N) / dl);
                    var left = (-dir.Item2, dir.Item1);
                    double width = 9 + rng.NextDouble() * 4, depth = 10 + rng.NextDouble() * 3;
                    double setback = 7 + rng.NextDouble() * 3 + depth / 2;
                    var c = (E: a.E + left.Item1 * side * setback, N: a.N + left.Item2 * side * setback);
                    if (rng.NextDouble() < 0.85)
                        plans.Add(House(rng, c, dir, width, depth, s < 80 ? 0.2 : 0.0));
                    s += width + 5 + rng.NextDouble() * 10;
                }
        }

        // the main street: a row each side, thinning toward the village's ends
        double mainStreet = streetXs[0];
        for (int side = -1; side <= 1; side += 2)
        {
            double along = -halfLength;
            while (along < halfLength)
            {
                double width = 9 + rng.NextDouble() * 5, depth = 10 + rng.NextDouble() * 4;
                double cx = x + along + width / 2;
                along += width + 5 + rng.NextDouble() * 12;

                // leave the mouths of the side streets and the church plot clear
                if (streetXs.Any(sx => Math.Abs(cx - sx) < width / 2 + 9)) continue;
                if (side > 0 && Math.Abs(cx - (mainStreet + 32)) < 22) continue;
                double edge = Math.Abs(cx - x) / halfLength;
                if (rng.NextDouble() > 0.92 - 0.5 * edge * edge) continue;

                var (p, t, nrm) = RoadFrame(cx);
                double setback = 9 + rng.NextDouble() * 5 + depth / 2;
                var c = (E: p.E + nrm.E * side * setback, N: p.N + nrm.N * side * setback);
                // barns at the ends of the village, a few apartment blocks in the middle
                if (edge > 0.75 && rng.NextDouble() < 0.4)
                    plans.Add(new Plan(new Footprint(c.E, c.N, t.E, t.N, 9 + rng.NextDouble() * 3, 6.5),
                        BuildingKind.Agricultural, 5, 24, 0, (ushort)(1880 + rng.Next(80))));
                else
                    plans.Add(House(rng, c, t, width, depth, edge < 0.3 ? 0.3 : 0.0));
            }
        }

        // the church, beside the first side street, turned to face down the valley
        {
            var (p, t, nrm) = RoadFrame(mainStreet + 32);
            var c = (E: p.E + nrm.E * 26, N: p.N + nrm.N * 26);
            plans.Add(new Plan(new Footprint(c.E, c.N, nrm.E, nrm.N, 12, 5.5), BuildingKind.Sacral,
                9, 40, 0, 1650));
            var tower = (E: c.E - nrm.E * 14.5, N: c.N - nrm.N * 14.5);
            plans.Add(new Plan(new Footprint(tower.E, tower.N, nrm.E, nrm.N, 2.6, 2.6), BuildingKind.Sacral,
                24, 0, 0, 1650, Tower: true));
        }

        return new Village(x, halfLength, streets, plans);
    }

    private static Plan House(Random rng, (double E, double N) c, (double E, double N) along,
        double width, double depth, double apartmentChance)
    {
        if (rng.NextDouble() < apartmentChance)
            return new Plan(new Footprint(c.E, c.N, along.E, along.N, width / 2 + 3, depth / 2 + 1),
                BuildingKind.Apartment, 9 + rng.Next(2) * 3, 0, (byte)(3 + rng.Next(2)),
                (ushort)(1960 + rng.Next(55)));

        // ridge along the street, or across it for the older, narrower houses
        bool gableToStreet = rng.NextDouble() < 0.35;
        var u = gableToStreet ? (-along.N, along.E) : along;
        double halfLength = (gableToStreet ? depth : width) / 2;
        double halfWidth = (gableToStreet ? width : depth) / 2;
        return new Plan(new Footprint(c.E, c.N, u.Item1, u.Item2, halfLength, halfWidth), BuildingKind.House,
            5.2 + rng.NextDouble() * 1.4, 28 + rng.NextDouble() * 12, 2, (ushort)(1850 + rng.Next(170)));
    }

    /// <summary>Farms and alpine huts on a jittered 350 m grid, wherever the ground allows.</summary>
    private IEnumerable<Plan> FarmsNear(Site site, double minE, double minN, double maxE, double maxN)
    {
        const double cell = 350;
        for (int gi = (int)Math.Floor((minE - 60) / cell); gi <= (int)Math.Floor((maxE + 60) / cell); gi++)
            for (int gj = (int)Math.Floor((minN - 60) / cell); gj <= (int)Math.Floor((maxN + 60) / cell); gj++)
            {
                if (Noise.Hash01(gi, gj, 101) > 0.3) continue;
                double e = (gi + 0.15 + 0.7 * Noise.Hash01(gi, gj, 103)) * cell;
                double n = (gj + 0.15 + 0.7 * Noise.Hash01(gi, gj, 107)) * cell;
                double x = e - CenterE, y = n - CenterN;
                var column = ColumnAt(x);

                // clear of the road, the river, the railway and the villages
                double fromRoad = y - column.Axis;
                if (Math.Abs(fromRoad) < 45 || Math.Abs(fromRoad - RiverOffset) < 50
                    || Math.Abs(fromRoad - RailOffset) < 35) continue;
                if (Math.Abs(fromRoad) < 420
                    && VillagesNear(x, x).Any(v => Math.Abs(x - v.X) < v.HalfLength + 80)) continue;

                double angle = Noise.Hash01(gi, gj, 109) * Math.PI;
                var u = (Math.Cos(angle), Math.Sin(angle));
                double alt = Ground(site, e, n);
                if (alt > 2250) continue;
                if (CoverAt(site, e, n) is not CoverClass.Open) continue;

                var rng = new Random(unchecked(gi * 92821 ^ gj * 68917));
                if (alt > 1350)
                {
                    // an alpine hut: small, low, dark timber
                    yield return new Plan(new Footprint(e, n, u.Item1, u.Item2, 4.5, 3.5),
                        BuildingKind.Agricultural, 3.2, 26, 1, (ushort)(1800 + rng.Next(120)));
                    continue;
                }
                yield return new Plan(new Footprint(e, n, u.Item1, u.Item2, 6, 5), BuildingKind.House,
                    5.5, 32, 2, (ushort)(1850 + rng.Next(150)));
                var barn = (E: e + u.Item2 * 17, N: n - u.Item1 * 17);
                yield return new Plan(new Footprint(barn.E, barn.N, u.Item1, u.Item2, 11, 7.5),
                    BuildingKind.Agricultural, 5, 24, 0, (ushort)(1870 + rng.Next(100)));
            }
    }

    /// <summary>Cover at one point, classified the way the raster is, for siting farms.</summary>
    private CoverClass CoverAt(Site site, double e, double n)
    {
        const double d = CoverStep;
        double h = Ground(site, e, n);
        double gx = (Ground(site, e + d, n) - Ground(site, e - d, n)) / (2 * d);
        double gy = (Ground(site, e, n + d) - Ground(site, e, n - d)) / (2 * d);
        double x = e - CenterE, y = n - CenterN;
        var column = ColumnAt(x);
        bool waterOk = site.Blend == null || site.Blend.WaterAllowed(e, n);
        return Classify((float)h, (float)(Math.Atan(Math.Sqrt(gx * gx + gy * gy)) * 180 / Math.PI),
            (float)Noise.Fbm(x / 650, y / 650, 3, 51), (float)Noise.Fbm(x / 420, y / 420, 2, 67),
            (float)(h - column.Floor), y - column.Axis, Math.Abs(y - (column.Axis + RiverOffset)), waterOk);
    }

    private IEnumerable<Plan> PlansNear(Site site, double minE, double minN, double maxE, double maxN)
    {
        foreach (var v in VillagesNear(minE - CenterE, maxE - CenterE))
            foreach (var p in v.Buildings)
                if (p.Rect.E >= minE && p.Rect.E < maxE && p.Rect.N >= minN && p.Rect.N < maxN)
                    yield return p;
        foreach (var p in FarmsNear(site, minE, minN, maxE, maxN))
            if (p.Rect.E >= minE && p.Rect.E < maxE && p.Rect.N >= minN && p.Rect.N < maxN)
                yield return p;
    }

    // ---- roads -------------------------------------------------------------------------------

    /// <summary>World polylines passing near a box: the valley road, the railway, side streets.</summary>
    private IEnumerable<(List<(double E, double N)> Points, RoadClass Class)> LinesNear(
        double minE, double minN, double maxE, double maxN)
    {
        // the valley runs on as far as the fill domain does: no end of its own
        double x0 = Math.Floor((minE - CenterE - 2 * RoadStep) / RoadStep) * RoadStep;
        double x1 = Math.Ceiling((maxE - CenterE + 2 * RoadStep) / RoadStep) * RoadStep;
        if (x1 > x0)
        {
            var road = new List<(double, double)>();
            var rail = new List<(double, double)>();
            // the x lattice is global, so both tiles at a seam cut the same segment
            for (double x = x0; x <= x1 + 1e-6; x += RoadStep)
            {
                double axis = Axis(x);
                road.Add((CenterE + x, CenterN + axis));
                rail.Add((CenterE + x, CenterN + axis + RailOffset));
            }
            yield return (road, RoadClass.Road);
            yield return (rail, RoadClass.Railway);
        }

        foreach (var v in VillagesNear(minE - CenterE, maxE - CenterE))
            foreach (var s in v.Streets)
                yield return (s.Points, s.Class);
    }

    public RoadTile? BuildRoads(TileId id, Blend? blend = null)
    {
        CheckBlend(id, blend);
        var site = new Site(null, blend);
        double maxE = id.MinE + ChunkFormat.TileSizeM;
        var segments = new List<RoadSegment>();
        foreach (var (points, cls) in LinesNear(id.MinE, id.MinN, maxE, id.MaxN))
            foreach (var piece in Clip(points, id.MinE, id.MinN, maxE, id.MaxN))
            {
                var xyz = new float[piece.Count * 3];
                for (int i = 0; i < piece.Count; i++)
                {
                    var (e, n) = piece[i];
                    xyz[i * 3] = (float)(e - id.MinE);
                    xyz[i * 3 + 1] = (float)Ground(site, e, n);
                    xyz[i * 3 + 2] = (float)(id.MaxN - n);
                }
                segments.Add(new RoadSegment
                {
                    Class = cls,
                    Surface = cls == RoadClass.Railway ? RoadSurface.Unknown : RoadSurface.Paved,
                    Width = RoadFormat.DefaultWidth(cls),
                    Points = xyz,
                });
            }
        return segments.Count == 0 ? null : new RoadTile { Id = id, Segments = segments };
    }

    /// <summary>
    /// Cuts a polyline to a box, inserting the crossing points (Liang-Barsky per segment). Two
    /// tiles sharing an edge compute the same crossing from the same segment, so pieces meet.
    /// </summary>
    private static List<List<(double E, double N)>> Clip(List<(double E, double N)> line,
        double minE, double minN, double maxE, double maxN)
    {
        var pieces = new List<List<(double E, double N)>>();
        List<(double E, double N)>? current = null;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            double dx = b.E - a.E, dy = b.N - a.N;
            double t0 = 0, t1 = 1;
            if (!Edge(-dx, a.E - minE) || !Edge(dx, maxE - a.E)
                || !Edge(-dy, a.N - minN) || !Edge(dy, maxN - a.N)
                // only touching the box at a vertex that lies on its edge
                || t1 - t0 < 1e-9)
            {
                current = null;
                continue;
            }

            var start = (a.E + dx * t0, a.N + dy * t0);
            var end = (a.E + dx * t1, a.N + dy * t1);
            if (current == null || t0 > 0)
            {
                current = new List<(double E, double N)> { start };
                pieces.Add(current);
            }
            current.Add(end);
            if (t1 < 1) current = null;

            bool Edge(double p, double q)
            {
                if (p == 0) return q >= 0;
                double r = q / p;
                if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
                return true;
            }
        }
        pieces.RemoveAll(p => p.Count < 2);
        return pieces;
    }

    // ---- buildings ---------------------------------------------------------------------------

    public BuildingTile? BuildBuildings(TileId id, Blend? blend = null)
    {
        CheckBlend(id, blend);
        var site = new Site(null, blend);
        var buildings = new List<Building>();
        foreach (var plan in PlansNear(site, id.MinE, id.MinN, id.MinE + ChunkFormat.TileSizeM, id.MaxN))
            if (Solid(plan, id, site) is { } b) buildings.Add(b);
        return buildings.Count == 0 ? null : new BuildingTile { Id = id, Buildings = buildings };
    }

    /// <summary>
    /// A closed solid in the tile's frame: walls from 0.8 m below the lowest footprint corner, a
    /// gable roof with a short overhang (or a flat one, or a spire on a tower). Each triangle is
    /// wound by checking its normal against the side it must be seen from, not by bookkeeping
    /// the vertex order — the lesson from FranceBuildings' roofs.
    /// </summary>
    private Building? Solid(Plan plan, TileId tile, Site site)
    {
        var f = plan.Rect;
        var u = (E: f.UE, N: f.UN);
        var v = (E: -f.UN, N: f.UE);
        (double E, double N) At(double a, double b) =>
            (f.E + u.E * a + v.E * b, f.N + u.N * a + v.N * b);

        // counter-clockwise in LV95
        var ring = new[]
        {
            At(-f.HalfLength, -f.HalfWidth), At(f.HalfLength, -f.HalfWidth),
            At(f.HalfLength, f.HalfWidth), At(-f.HalfLength, f.HalfWidth),
        };
        double low = double.MaxValue, high = double.MinValue;
        foreach (var p in ring.Append((f.E, f.N)))
        {
            double g = Ground(site, p.Item1, p.Item2);
            low = Math.Min(low, g);
            high = Math.Max(high, g);
        }
        // a house does not stand on a cliff, nor in the river
        if (high - low > 4.5 || Math.Abs(f.N - CenterN - (Axis(f.E - CenterE) + RiverOffset)) < RiverBank + f.HalfWidth + 4)
            return null;

        double baseY = low - 0.8, eave = high + plan.WallHeight;
        var tris = new List<float>(64 * 9);

        void Tri((double E, double N) a, double ay, (double E, double N) b, double by,
            (double E, double N) c, double cy, double faceE, double faceY, double faceN)
        {
            // tile frame: X east, Y up, Z south
            double ax = a.E - tile.MinE, az = tile.MaxN - a.N;
            double bx = b.E - tile.MinE, bz = tile.MaxN - b.N;
            double cx = c.E - tile.MinE, cz = tile.MaxN - c.N;
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double wx = cx - ax, wy = cy - ay, wz = cz - az;
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            // Godot's front face is clockwise as seen, so the right-hand normal of a visible
            // triangle points AWAY from whoever is looking at it
            bool flip = nx * faceE + ny * faceY + nz * -faceN > 0;
            void Add(double x, double y, double z) { tris.Add((float)x); tris.Add((float)y); tris.Add((float)z); }
            Add(ax, ay, az);
            if (flip) { Add(cx, cy, cz); Add(bx, by, bz); }
            else { Add(bx, by, bz); Add(cx, cy, cz); }
        }
        void Quad((double E, double N) a, double ay, (double E, double N) b, double by,
            (double E, double N) c, double cy, (double E, double N) d, double dy,
            double faceE, double faceY, double faceN)
        {
            Tri(a, ay, b, by, c, cy, faceE, faceY, faceN);
            Tri(a, ay, c, cy, d, dy, faceE, faceY, faceN);
        }

        // walls
        for (int i = 0; i < 4; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % 4];
            // outward: the ring is counter-clockwise, so the outside is to the right of each edge
            double oe = b.N - a.N, on = -(b.E - a.E);
            Quad(a, baseY, a, eave, b, eave, b, baseY, oe, 0, on);
        }

        double top = eave;
        if (plan.Tower)
        {
            // a pyramid spire, four times the tower's width
            var apex = (f.E, f.N);
            top = eave + f.HalfWidth * 4.5;
            for (int i = 0; i < 4; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % 4];
                Tri(a, eave, b, eave, apex, top, b.N - a.N, 0.5, -(b.E - a.E));
            }
        }
        else if (plan.Pitch <= 0)
        {
            Quad(ring[0], eave, ring[1], eave, ring[2], eave, ring[3], eave, 0, 1, 0);
        }
        else
        {
            // ridge along u; overhang 0.5 m past every wall
            const double over = 0.5;
            double tan = Math.Tan(plan.Pitch * Math.PI / 180);
            double ridge = eave + f.HalfWidth * tan;
            double lip = eave - over * tan;
            double l = f.HalfLength + over, w = f.HalfWidth + over;
            var r0 = At(-l, 0);
            var r1 = At(l, 0);
            // gable ends, flush with the walls
            Tri(ring[1], eave, ring[2], eave, At(f.HalfLength, 0), ridge, u.E, 0, u.N);
            Tri(ring[3], eave, ring[0], eave, At(-f.HalfLength, 0), ridge, -u.E, 0, -u.N);
            // the two slopes
            Quad(At(-l, -w), lip, At(l, -w), lip, r1, ridge, r0, ridge, -v.E, 1, -v.N);
            Quad(At(l, w), lip, At(-l, w), lip, r0, ridge, r1, ridge, v.E, 1, v.N);
            top = ridge;
        }

        return new Building
        {
            Kind = plan.Kind,
            YearBuilt = plan.Year,
            Floors = plan.Floors,
            MinY = (float)baseY,
            MaxY = (float)top,
            Triangles = tris.ToArray(),
        };
    }

    // ---- where trees may not grow ------------------------------------------------------------

    /// <summary>Cells under a road (plus a verge) or a building (plus a garden strip).</summary>
    private bool[] BuildTreeMask(TileId id, Site site)
    {
        const int size = CoverFormat.Size;
        var blocked = new bool[size * size];
        double maxE = id.MinE + ChunkFormat.TileSizeM;
        const double margin = 20;

        void Stamp(double e, double n, double radius)
        {
            int c0 = (int)Math.Floor(e - id.MinE - radius), c1 = (int)Math.Ceiling(e - id.MinE + radius);
            int r0 = (int)Math.Floor(id.MaxN - n - radius), r1 = (int)Math.Ceiling(id.MaxN - n + radius);
            for (int r = Math.Max(r0, 0); r <= Math.Min(r1, size - 1); r++)
                for (int c = Math.Max(c0, 0); c <= Math.Min(c1, size - 1); c++)
                {
                    double de = id.MinE + c - e, dn = id.MaxN - r - n;
                    if (de * de + dn * dn <= radius * radius) blocked[r * size + c] = true;
                }
        }

        foreach (var (points, cls) in LinesNear(id.MinE - margin, id.MinN - margin, maxE + margin, id.MaxN + margin))
        {
            double radius = RoadFormat.DefaultWidth(cls) / 2 + 3;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                if (Math.Max(a.E, b.E) < id.MinE - radius || Math.Min(a.E, b.E) > maxE + radius
                    || Math.Max(a.N, b.N) < id.MinN - radius || Math.Min(a.N, b.N) > id.MaxN + radius) continue;
                double len = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
                int steps = Math.Max(1, (int)Math.Ceiling(len));
                for (int s = 0; s <= steps; s++)
                    Stamp(a.E + (b.E - a.E) * s / steps, a.N + (b.N - a.N) * s / steps, radius);
            }
        }

        foreach (var p in PlansNear(site, id.MinE - margin, id.MinN - margin, maxE + margin, id.MaxN + margin))
        {
            var f = p.Rect;
            double reach = Math.Sqrt(f.HalfLength * f.HalfLength + f.HalfWidth * f.HalfWidth) + 3;
            int c0 = (int)Math.Floor(f.E - id.MinE - reach), c1 = (int)Math.Ceiling(f.E - id.MinE + reach);
            int r0 = (int)Math.Floor(id.MaxN - f.N - reach), r1 = (int)Math.Ceiling(id.MaxN - f.N + reach);
            for (int r = Math.Max(r0, 0); r <= Math.Min(r1, size - 1); r++)
                for (int c = Math.Max(c0, 0); c <= Math.Min(c1, size - 1); c++)
                {
                    double de = id.MinE + c - f.E, dn = id.MaxN - r - f.N;
                    double a = de * f.UE + dn * f.UN, b = -de * f.UN + dn * f.UE;
                    if (Math.Abs(a) <= f.HalfLength + 3 && Math.Abs(b) <= f.HalfWidth + 3)
                        blocked[r * size + c] = true;
                }
        }
        return blocked;
    }
}
