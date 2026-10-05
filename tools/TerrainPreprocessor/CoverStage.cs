using System.Diagnostics;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Network;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Tools.Preprocessor;

public static class CoverStage
{
    /// <summary>Clearance kept either side of a carriageway edge, in metres.</summary>
    private const double RoadClearance = 2.5;

    /// <summary>Marks every lattice cell lying within a road corridor of any of <paramref name="tiles"/>.</summary>
    private static bool[] BuildRoadMask(IEnumerable<RoadTile> tiles)
    {
        var mask = new bool[CoverFormat.Size * CoverFormat.Size];
        double spacing = ChunkFormat.SpacingM;

        foreach (var seg in tiles.SelectMany(t => t.Segments))
        {
            // tunnels and bridges need the widest clearance: their portals and abutments
            // are exactly where a stray tree ruins the shot
            // a street's sidewalk and verge (#119) are part of the corridor
            var a = seg.Attributes;
            double side = Math.Max(a.Left.Reach, a.Right.Reach);   // bike paths and their shift off a turn lane (#120) included
            double radius = seg.Width * 0.5 + side + RoadClearance
                + ((seg.Flags & (RoadFlags.Tunnel | RoadFlags.Bridge)) != 0 ? 4.0 : 0.0);
            int cells = (int)Math.Ceiling(radius / spacing);

            for (int i = 0; i < seg.PointCount - 1; i++)
            {
                var (ax, az) = (seg.Points[i * 3], seg.Points[i * 3 + 2]);
                var (bx, bz) = (seg.Points[(i + 1) * 3], seg.Points[(i + 1) * 3 + 2]);
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                int steps = Math.Max(1, (int)Math.Ceiling(len / spacing));

                for (int st = 0; st <= steps; st++)
                {
                    double t = (double)st / steps;
                    double x = ax + (bx - ax) * t;
                    double z = az + (bz - az) * t;
                    int c0 = (int)(x / spacing), r0 = (int)(z / spacing);

                    for (int dr = -cells; dr <= cells; dr++)
                        for (int dc = -cells; dc <= cells; dc++)
                        {
                            int c = c0 + dc, r = r0 + dr;
                            if ((uint)c >= CoverFormat.Size || (uint)r >= CoverFormat.Size) continue;
                            if (dc * dc + dr * dr > cells * cells) continue;
                            mask[r * CoverFormat.Size + c] = true;
                        }
                }
            }
        }
        return mask;
    }

    /// <summary>How far past a town street's paved edge (its sidewalks included) the open ground is paved.</summary>
    private const double PaveReach = 8.0;

    /// <summary>
    /// Paves the open ground (no TLM cover: <see cref="CoverClass.Open"/>, drawn as grass) along the
    /// streets of a town (#119): within <see cref="PaveReach"/> of a street's paved edge, under its
    /// junction caps, where the <see cref="UrbanField"/> says urban. A city's medians, traffic
    /// islands, bridgeheads and the strips between parallel carriageways are stone and concrete,
    /// and TLM maps none of it; parks, gardens and other mapped cover stay as they are.
    /// </summary>
    private static long PaveStreets(TileId id, RoadTile tile, byte[] cells, UrbanField field)
    {
        int n = CoverFormat.Size;
        double spacing = ChunkFormat.SpacingM;
        var near = new bool[n * n];
        foreach (var seg in tile.Segments)
        {
            if (seg.PointCount < 2 || !seg.Attributes.Has(RoadAttrFlags.Urban)) continue;
            if ((seg.Flags & (RoadFlags.Bridge | RoadFlags.Tunnel)) != 0 || seg.Class > RoadClass.Square) continue;
            var a = seg.Attributes;
            double radius = seg.Width * 0.5 + Math.Max(a.Left.Reach, a.Right.Reach) + PaveReach;
            int cr = (int)Math.Ceiling(radius / spacing);
            for (int i = 0; i < seg.PointCount - 1; i++)
            {
                double ax = seg.Points[i * 3], az = seg.Points[i * 3 + 2], bx = seg.Points[i * 3 + 3], bz = seg.Points[i * 3 + 5];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                int c0 = Math.Max(0, (int)((Math.Min(ax, bx) - radius) / spacing)), c1 = Math.Min(n - 1, (int)((Math.Max(ax, bx) + radius) / spacing) + 1);
                int r0 = Math.Max(0, (int)((Math.Min(az, bz) - radius) / spacing)), r1 = Math.Min(n - 1, (int)((Math.Max(az, bz) + radius) / spacing) + 1);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        double x = c * spacing, z = r * spacing;
                        double t = len < 1e-9 ? 0 : Math.Clamp(((x - ax) * (bx - ax) + (z - az) * (bz - az)) / (len * len), 0, 1);
                        double dx = ax + (bx - ax) * t - x, dz = az + (bz - az) * t - z;
                        if (dx * dx + dz * dz <= radius * radius) near[r * n + c] = true;
                    }
            }
        }
        // the junction caps themselves, and a reach round them
        foreach (var cap in tile.Junctions)
        {
            if (cap.Layer != 0) continue;
            var v = cap.Vertices;
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            for (int k = 0; k < v.Length / 3; k++)
            {
                minX = Math.Min(minX, v[k * 3]); maxX = Math.Max(maxX, v[k * 3]);
                minZ = Math.Min(minZ, v[k * 3 + 2]); maxZ = Math.Max(maxZ, v[k * 3 + 2]);
            }
            for (int r = Math.Max(0, (int)((minZ - PaveReach) / spacing)); r <= Math.Min(n - 1, (int)((maxZ + PaveReach) / spacing) + 1); r++)
                for (int c = Math.Max(0, (int)((minX - PaveReach) / spacing)); c <= Math.Min(n - 1, (int)((maxX + PaveReach) / spacing) + 1); c++)
                    near[r * n + c] = true;
        }

        long paved = 0;
        for (int i = 0; i < near.Length; i++)
        {
            if (!near[i] || cells[i] != (byte)CoverClass.Open) continue;
            double e = id.MinE + (i % n) * spacing, nn = id.MaxN - (i / n) * spacing;
            if (field.Density(e, nn) < UrbanField.UrbanAt) continue;
            cells[i] = (byte)CoverClass.TownPaving;
            paved++;
        }
        return paved;
    }

    /// <summary>Ground less than this above a tunnel's roof is its concrete deck.</summary>
    private const double ThinCover = 2.5;

    /// <summary>
    /// The concrete roof of a shallow tunnel (#119): over its footprint (the bore's width, a metre
    /// to spare), where the ground stands less than <see cref="ThinCover"/> above the bore's roof,
    /// open ground and town paving become <see cref="CoverClass.TunnelRoof"/>. Ground is
    /// only carved at the portals now (<c>TunnelCarver.PortalReach</c>), so a cut-and-cover tunnel
    /// under a town keeps its cover; this is what that cover is made of.
    /// </summary>
    private static long RoofTunnels(RoadTile tile, byte[] cells, ChunkGrid grid)
    {
        int n = CoverFormat.Size;
        double spacing = ChunkFormat.SpacingM;
        long roofed = 0;
        foreach (var seg in tile.Segments)
        {
            if ((seg.Flags & RoadFlags.Tunnel) == 0 || seg.PointCount < 2 || RoadFormat.IsWatercourse(seg.Class)) continue;
            double half = RoadFormat.TunnelWidth(seg.Class) * 0.5 + 1.0;
            double roof = RoadFormat.TunnelHeight(seg.Class);
            for (int i = 0; i < seg.PointCount - 1; i++)
            {
                double ax = seg.Points[i * 3], ay = seg.Points[i * 3 + 1], az = seg.Points[i * 3 + 2];
                double bx = seg.Points[i * 3 + 3], by = seg.Points[i * 3 + 4], bz = seg.Points[i * 3 + 5];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                int c0 = Math.Max(0, (int)((Math.Min(ax, bx) - half) / spacing)), c1 = Math.Min(n - 1, (int)((Math.Max(ax, bx) + half) / spacing) + 1);
                int r0 = Math.Max(0, (int)((Math.Min(az, bz) - half) / spacing)), r1 = Math.Min(n - 1, (int)((Math.Max(az, bz) + half) / spacing) + 1);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        double x = c * spacing, z = r * spacing;
                        double t = len < 1e-9 ? 0 : Math.Clamp(((x - ax) * (bx - ax) + (z - az) * (bz - az)) / (len * len), 0, 1);
                        double dx = ax + (bx - ax) * t - x, dz = az + (bz - az) * t - z;
                        if (dx * dx + dz * dz > half * half) continue;
                        int k = r * n + c;
                        var cover = (CoverClass)cells[k];
                        if (cover is not (CoverClass.Open or CoverClass.TownPaving)) continue;
                        double over = grid.HeightMetersAt(c, r) - (ay + (by - ay) * t + roof);
                        if (over > ThinCover) continue;
                        cells[k] = (byte)CoverClass.TunnelRoof;
                        roofed++;
                    }
            }
        }
        return roofed;
    }


    /// <summary>
    /// A laid-out car park (#499), after the network stage has written it. Two jobs, both of which
    /// need the finished <c>.road</c> and so cannot happen in the stage itself:
    ///
    /// <para>
    /// <b>The pattern comes off.</b> Cells under an <see cref="AreaPropType.ParkingPad"/> become
    /// <see cref="CoverClass.ParkingPaved"/>, which carries no <see cref="SurfacePattern"/>. Without
    /// this every laid-out lot shows its real bays <i>and</i> the old world-aligned grid bleeding a
    /// cell past its edge. A lot the planner rejected keeps its class and its grid.
    /// </para>
    ///
    /// <para>
    /// <b>The planters get their trees.</b> One tree at the centre of each
    /// <see cref="AreaPropType.ParkingIsland"/>, into the tile's <c>.trees</c> rather than as a
    /// prop, so a car park's trees get the same LOD, per-ring thinning and pooled collision as
    /// every other tree in the game (`perf-lod-trees`, `trees-solid`).
    /// </para>
    /// </summary>
    private static (long Cells, int Trees) LaidOutLots(
        TileId id, RoadTile tile, byte[] cells, CoverExtractor extractor)
    {
        int n = CoverFormat.Size;
        double spacing = ChunkFormat.SpacingM;
        long repainted = 0;
        int planted = 0;

        foreach (var area in tile.AreaProps)
        {
            if (area.Type == AreaPropType.ParkingIsland && area.Vertices.Length >= 9)
            {
                // the planter's centre, and the ground it stands on: its own kerbed top
                double cx = 0, cy = 0, cz = 0;
                int count = area.Vertices.Length / 3;
                for (int v = 0; v < count; v++)
                {
                    cx += area.Vertices[v * 3];
                    cy += area.Vertices[v * 3 + 1];
                    cz += area.Vertices[v * 3 + 2];
                }
                cx /= count; cy /= count; cz /= count;

                // a car park tree is a planted broadleaf, 4.5 to 7 m, hashed off its position so a
                // rebuild grows the same one
                ulong h = ParkingPlanner.Key((id.MinE + cx, id.MaxN - cz));
                float height = 4.5f + (h & 0xFF) / 255f * 2.5f;
                extractor.AddTree(id, new CoverExtractor.TreeInstance(
                    (float)cx, (float)(cy + area.Height), (float)cz, height, 3));
                planted++;
                continue;
            }
            if (area.Type != AreaPropType.ParkingPad || area.Vertices.Length < 9) continue;

            // every lattice cell whose centre falls in one of the pad's triangles
            for (int t = 0; t + 2 < area.Indices.Length; t += 3)
            {
                var (ax, az) = (area.Vertices[area.Indices[t] * 3], area.Vertices[area.Indices[t] * 3 + 2]);
                var (bx, bz) = (area.Vertices[area.Indices[t + 1] * 3], area.Vertices[area.Indices[t + 1] * 3 + 2]);
                var (cx2, cz2) = (area.Vertices[area.Indices[t + 2] * 3], area.Vertices[area.Indices[t + 2] * 3 + 2]);

                int c0 = Math.Max(0, (int)(Math.Min(ax, Math.Min(bx, cx2)) / spacing));
                int c1 = Math.Min(n - 1, (int)(Math.Max(ax, Math.Max(bx, cx2)) / spacing) + 1);
                int r0 = Math.Max(0, (int)(Math.Min(az, Math.Min(bz, cz2)) / spacing));
                int r1 = Math.Min(n - 1, (int)(Math.Max(az, Math.Max(bz, cz2)) / spacing) + 1);

                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        double x = c * spacing, z = r * spacing;
                        if (!InTriangle(x, z, ax, az, bx, bz, cx2, cz2)) continue;
                        int k = r * n + c;
                        // only a car park loses its pattern; paving, a square or open ground stay
                        if ((CoverClass)cells[k] is not (CoverClass.ParkingPublic
                            or CoverClass.ParkingPrivate or CoverClass.RestArea)) continue;
                        cells[k] = (byte)CoverClass.ParkingPaved;
                        repainted++;
                    }
            }
        }
        return (repainted, planted);
    }

    private static bool InTriangle(double px, double pz,
        double ax, double az, double bx, double bz, double cx, double cz)
    {
        double d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        double d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
        double d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    public static int Run(string tlmGpkg, string outDir, Dictionary<TileId, ChunkGrid> grids, string? overridesPath = null,
        string? rawDir = null)
    {
        if (!File.Exists(tlmGpkg))
        {
            Console.Error.WriteLine($"swissTLM3D GeoPackage not found: {tlmGpkg}");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var extractor = new CoverExtractor(tlmGpkg) { OverridesPath = overridesPath };

        var heightOf = TerrainSampler.For(grids);

        // roads are written before this stage, so their corridors can be masked out: the raw
        // extractor lines (they run through the junction areas the network stage trims away) and
        // the network stage's tile, whose motorway carriageways #117 moved outward and widened.
        // The full build runs the network stage before this one.
        foreach (var id in grids.Keys)
        {
            var tiles = new List<RoadTile>();
            foreach (var path in new[] { rawDir != null ? RawRoads.RoadPath(rawDir, id) : null,
                         Path.Combine(outDir, RoadFormat.FileName(id)) })
            {
                if (path is null || !File.Exists(path)) continue;
                using var fs = File.OpenRead(path);
                tiles.Add(RoadCodec.Decode(fs));
            }
            if (tiles.Count > 0) extractor.RoadMask[id] = BuildRoadMask(tiles);
        }

        extractor.Extract(grids.Keys.ToList(), heightOf);

        // in town, the ground along the streets is paved (#119)
        var field = new UrbanField(new Facades(outDir));
        long paved = 0, roofs = 0, lotCells = 0;
        int lotTrees = 0;
        foreach (var (id, cells) in extractor.Cover)
        {
            string road = Path.Combine(outDir, RoadFormat.FileName(id));
            if (!File.Exists(road)) continue;
            RoadTile tile;
            using (var fs = File.OpenRead(road)) tile = RoadCodec.Decode(fs);
            paved += PaveStreets(id, tile, cells, field);
            if (grids.TryGetValue(id, out var grid)) roofs += RoofTunnels(tile, cells, grid);
            var (lc, lt) = LaidOutLots(id, tile, cells, extractor);   // #499
            lotCells += lc;
            lotTrees += lt;
        }

        var histogram = new SortedDictionary<CoverClass, long>();
        long coveredCells = 0, totalCells = 0;
        foreach (var (id, cells) in extractor.Cover)
        {
            using (var fs = File.Create(Path.Combine(outDir, CoverFormat.FileName(id))))
                CoverFormat.Encode(id, cells, fs);

            foreach (byte b in cells)
            {
                totalCells++;
                if (b == 0) continue;
                coveredCells++;
                histogram[(CoverClass)b] = histogram.GetValueOrDefault((CoverClass)b) + 1;
            }
        }

        long treeTotal = 0;
        foreach (var (id, list) in extractor.Trees)
        {
            var trees = list.Select(t => new UnitSport.Terrain.Format.TreeInstance(t.X, t.Y, t.Z, t.Height, t.Kind)).ToList();
            using var fs = File.Create(Path.Combine(outDir, TreeFormat.FileName(id)));
            TreeFormat.Encode(id, trees, fs);
            treeTotal += trees.Count;
        }

        long ringTotal = extractor.LayerRings.Values.Sum();
        Console.WriteLine($"Cover: {ringTotal} rings from {extractor.LayerRings.Count} layers " +
                          $"over {extractor.Cover.Count} tiles in {sw.Elapsed.TotalSeconds:F1}s");
        foreach (var (layer, rings) in extractor.LayerRings.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {layer}: {rings} rings");
        Console.WriteLine($"  road corridors masked on {extractor.RoadMask.Count} tiles; {paved:N0} town vertices paved along the streets, {roofs:N0} over shallow tunnels concrete");
        Console.WriteLine($"  car parks: {lotCells:N0} vertices under a laid-out pad lost the bay pattern, {lotTrees} planters planted");
        Console.WriteLine($"  classified {100.0 * coveredCells / totalCells:F1}% of vertices; " +
                          string.Join(", ", histogram.Select(kv => $"{kv.Key}={100.0 * kv.Value / totalCells:F1}%")));
        Console.WriteLine($"  trees: {treeTotal:N0} across {extractor.Trees.Count} tiles " +
                          $"({extractor.ScatteredTrees:N0} scattered, {extractor.PlantedTrees:N0} planted, " +
                          $"{extractor.SurveyedTrees:N0} surveyed singles)");
        return 0;
    }
}
