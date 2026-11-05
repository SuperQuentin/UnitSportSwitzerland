using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The farm fields on work frames (#1507, G2 B3 row 7; docs/notes/tools/cube-fields-pistes-photos.md): the LV95
/// build's fields (<see cref="FieldStage"/>: LWB per canton, the OSM fallback), read once with the same reader,
/// codes, ids and gating, then per frame each ring moved onto the frame's plane (OSM exactly from its latitude and
/// longitude, LWB from LV95 at the ground's height), prepared there (<see cref="FieldGeometry.Prepare"/>: 0.5 m,
/// 200 m², the same code), and written whole into every core leaf its bounds touch (as the LV95 build writes it
/// into every tile), its rings as anchor-frame (x, z) on the leaf's ground: <see cref="LeafKind.Fields"/>.
///
/// <code>
/// --cube-fields --cube-out REGION [--fields LWB_DIR] [--osm-pbf PBF] [--gwr data.sqlite] [--cube-box minE,minN,maxE,maxN] [--jobs N]
/// </code>
/// </summary>
public static class CubeFieldStage
{
    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-fields");

    private static readonly Regex ZipName = new(@"_(?<c>[A-Z]{2})_lv95\.zip$", RegexOptions.IgnoreCase);

    /// <summary>A field as read: LV95 rings (LWB) or latitude and longitude pairs (OSM), unprepared, and its LV95 box.</summary>
    private sealed record Raw(uint Id, CropKind Crop, FieldSource Source, ushort Lnf, List<double[]> Rings, bool LatLon,
        double MinE, double MinN, double MaxE, double MaxN);

    public static int Run(IReadOnlyList<string> args)
    {
        string? region = null, lwbDir = null, pbf = null, gwr = null;
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        int jobs = Math.Max(1, Environment.ProcessorCount - 2);
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-fields": break;
                case "--cube-out": region = args[++i]; break;
                case "--fields": lwbDir = args[++i]; break;
                case "--osm-pbf": pbf = args[++i]; break;
                case "--gwr": gwr = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    box = (v[0], v[1], v[2], v[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-fields: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null || (lwbDir == null && pbf == null)) { Console.Error.WriteLine("--cube-fields needs --cube-out REGION and --fields LWB_DIR and/or --osm-pbf PBF"); return 2; }

        var sw = Stopwatch.StartNew();
        var heights = new RegionHeights(region);
        var wanted = heights.Leaves.Where(k => box is not { } b || CubeFrames.InBox(k, b)).ToList();
        if (wanted.Count == 0) { Console.Error.WriteLine($"--cube-fields: no leaf with heights in {region}"); return 2; }
        var driver = new FrameDriver(wanted, 0, SwissSeaLevel.Instance, heights.FrameLevel);
        var fields = Read(lwbDir, pbf, gwr, jobs);
        Console.WriteLine($"cube fields: {fields.Count:N0} fields read ({fields.Count(f => f.Source == FieldSource.Lwb):N0} LWB), "
            + $"{wanted.Count} leaves in {driver.Frames.Count} frames, {sw.Elapsed.TotalSeconds:F0} s");

        var writer = new LeafWriter(region, driver.FrameLevel, [LeafKind.Fields]);
        long placed = 0, copies = 0, small = 0;
        foreach (var plan in driver.Frames)
        {
            var ground = new FrameGround(plan.Frame, heights.Read, driver.LeafLevel);
            var (qe0, qn0, qe1, qn1) = plan.QueryBox(Lv95Source.Instance);
            var leaves = plan.Core.Select(k => (Key: k, Box: CubeFrames.PlaneBox(plan.Transform(k), 1))).ToList();
            var into = plan.Core.ToDictionary(k => k, _ => new List<FieldPolygon>());
            foreach (var f in fields)
            {
                if (f.MaxE < qe0 || f.MinE > qe1 || f.MaxN < qn0 || f.MinN > qn1) continue;
                var rings = FieldGeometry.Prepare(f.Rings.Select(r => OnPlane(plan.Frame, ground, r, f.LatLon)).ToList());
                if (rings == null) { small++; continue; }
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                var outer = rings[0];
                for (int i = 0; i < outer.Length; i += 2)
                    (x0, y0, x1, y1) = (Math.Min(x0, outer[i]), Math.Min(y0, outer[i + 1]), Math.Max(x1, outer[i]), Math.Max(y1, outer[i + 1]));
                int n = 0;
                foreach (var (key, b) in leaves)
                {
                    if (x1 < b.X0 || x0 > b.X1 || y1 < b.Y0 || y0 > b.Y1) continue;
                    var t = plan.Transform(key);
                    var local = rings.Select(r => ToLeaf(t, ground, r)).ToList();
                    into[key].Add(new FieldPolygon(f.Id, f.Crop, f.Source, f.Lnf, local));
                    n++;
                }
                if (n > 0) { placed++; copies += n; }
            }
            var sections = into.ToDictionary(e => e.Key, e => e.Value.Count == 0 ? new List<LeafSection>()
                : [LeafFields.Encode(e.Value.OrderBy(p => p.Id).ThenBy(p => p.Source).ToList())]);
            writer.WriteFrame(plan.Cell, sections);
            heights.Reload(plan.Cell);
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {plan.Core.Count} leaves, {sections.Count(e => e.Value.Count > 0)} with fields; {sw.Elapsed.TotalSeconds:F0} s");
        }
        var manifestPath = Path.Combine(region, CubeManifest.FileName);
        var sources = File.Exists(manifestPath) ? CubeManifest.FromJson(File.ReadAllText(manifestPath)).Sources : new List<string>();
        const string source = "farm fields: LWB Nutzungsflaechen (geodienste.ch) and OpenStreetMap farmland/meadow (ODbL)";
        if (!sources.Contains(source)) sources.Add(source);
        LeafWriter.WriteManifest(region, driver.LeafLevel, driver.FrameLevel, sources);
        Console.WriteLine($"cube fields: {placed:N0} field placements by frame, {copies:N0} leaf copies, {small:N0} under {FieldGeometry.MinAreaM2:F0} m² on a plane; "
            + $"{sw.Elapsed.TotalSeconds:F0} s, peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return 0;
    }

    /// <summary>The LV95 build's fields, as <see cref="FieldStage"/> reads and gates them, unprepared.</summary>
    private static List<Raw> Read(string? lwbDir, string? pbf, string? gwr, int jobs)
    {
        var all = new List<Raw>();
        var zips = lwbDir != null && Directory.Exists(lwbDir)
            ? Directory.GetFiles(lwbDir, "*.zip").Where(z => ZipName.IsMatch(z)).OrderByDescending(z => new FileInfo(z).Length).ToList() : [];
        Parallel.ForEach(zips, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(jobs, zips.Count)) }, zip =>
        {
            string canton = ZipName.Match(zip).Groups["c"].Value.ToUpperInvariant();
            var mine = new List<Raw>();
            LwbReader.ReadZip(zip, item =>
            {
                if (FieldCodes.Of(item.Code) is not { } crop || crop == CropKind.None) return;
                for (int part = 0; part < item.Polygons.Count; part++)
                    mine.Add(Boxed(new Raw(FieldGeometry.LwbId(canton, item.Identifier, part), crop, FieldSource.Lwb, (ushort)item.Code,
                        item.Polygons[part], false, 0, 0, 0, 0)));
            });
            lock (all) all.AddRange(mine);
        });
        // every tile an LWB field's box touches, as the LV95 build's tiles with an LWB field
        var lwbTiles = new HashSet<TileId>();
        foreach (var f in all)
            for (int te = (int)Math.Floor((f.MinE + 1) / 1000); te <= (int)Math.Floor((f.MaxE - 1) / 1000); te++)
                for (int tn = (int)Math.Floor((f.MinN + 1) / 1000); tn <= (int)Math.Floor((f.MaxN - 1) / 1000); tn++)
                    lwbTiles.Add(new TileId(te, tn));
        if (pbf != null)
        {
            Func<double, double, bool> gated = gwr != null && File.Exists(gwr)
                ? OsmFields.GatedMask.FromGwr(gwr).Contains
                : (e, n) => !lwbTiles.Contains(TileId.FromLv95(e, n));
            foreach (var p in OsmFields.Load(pbf, jobs, (lat, lon) => (lat, lon)))
            {
                var outer = p.Rings[0];
                double la = 0, lo = 0;
                for (int i = 0; i < outer.Length; i += 2) { la += outer[i]; lo += outer[i + 1]; }
                var (ce, cn) = SwissGeodesy.ToLv95(la / (outer.Length / 2), lo / (outer.Length / 2));
                if (!gated(ce, cn)) continue;
                uint id = FieldGeometry.OsmId(p.Relation, p.Id, p.Part);
                all.Add(Boxed(new Raw(id, OsmFields.CropOf(p.Landuse, p.Crop, id), FieldSource.Osm, 0, p.Rings, true, 0, 0, 0, 0)));
            }
        }
        return all.OrderBy(f => f.Id).ThenBy(f => f.Source).ToList();
    }

    /// <summary>The field with its outer ring's LV95 box (a margin of a metre).</summary>
    private static Raw Boxed(Raw f)
    {
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        var r = f.Rings[0];
        for (int i = 0; i < r.Length; i += 2)
        {
            var (e, n) = f.LatLon ? SwissGeodesy.ToLv95(r[i], r[i + 1]) : (r[i], r[i + 1]);
            (minE, minN, maxE, maxN) = (Math.Min(minE, e), Math.Min(minN, n), Math.Max(maxE, e), Math.Max(maxN, n));
        }
        return f with { MinE = minE - 1, MinN = minN - 1, MaxE = maxE + 1, MaxN = maxN + 1 };
    }

    /// <summary>A ring onto the frame's plane: latitude and longitude exactly, LV95 at the ground's height.</summary>
    private static double[] OnPlane(WorkFrame frame, IGround ground, double[] ring, bool latLon)
    {
        var p = new double[ring.Length];
        for (int i = 0; i < ring.Length; i += 2)
            (p[i], p[i + 1]) = latLon ? frame.Plane.ToPlane(ring[i], ring[i + 1]) : CubeFrames.FromLv95(frame, ground, ring[i], ring[i + 1]);
        return p;
    }

    /// <summary>A plane ring as the leaf holds it: anchor-frame (x, z) of each point on the leaf's ground (0 m where it has none).</summary>
    private static float[] ToLeaf(LeafTransform t, IGround ground, double[] ring)
    {
        var f = new float[ring.Length];
        for (int i = 0; i < ring.Length; i += 2)
        {
            var (x, _, z) = t.PlaneToLocal(ring[i], ring[i + 1], ground.Sample(ring[i], ring[i + 1]) ?? 0);
            (f[i], f[i + 1]) = ((float)x, (float)z);
        }
        return f;
    }
}

/// <summary>Small pieces the row-7 frame stages share (#1507).</summary>
public static class CubeFrames
{
    /// <summary>Whether a leaf's centre lies in an LV95 box (<c>--cube-box</c>).</summary>
    public static bool InBox(CubeKey k, (double MinE, double MinN, double MaxE, double MaxN) b)
    {
        var c = k.Centre;
        var (e, n) = SwissGeodesy.ToLv95(c.LatDeg, c.LonDeg);
        return e >= b.MinE && e <= b.MaxE && n >= b.MinN && n <= b.MaxN;
    }

    /// <summary>A leaf's box on the frame's plane (its corners and edge middles), pushed out by <paramref name="slack"/> metres.</summary>
    public static (double X0, double Y0, double X1, double Y1) PlaneBox(LeafTransform t, double slack)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        int c = t.Cells, h = c / 2;
        foreach (var (i, j) in (ReadOnlySpan<(int, int)>)[(0, 0), (h, 0), (c, 0), (0, h), (c, h), (0, c), (h, c), (c, c)])
        {
            var (x, y) = t.LatticeToPlane(i, j);
            (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
        }
        return (x0 - slack, y0 - slack, x1 + slack, y1 + slack);
    }

    /// <summary>An LV95 point onto the frame's plane at its ground's height (the frame's own ground, else sea level).</summary>
    public static (double X, double Y) FromLv95(WorkFrame frame, IGround ground, double e, double n)
    {
        var (x0, y0, _) = frame.FromSource(Lv95Source.Instance, e, n, 0);
        double h = ground.Sample(x0, y0) ?? 0;
        if (h == 0) return (x0, y0);
        var (x, y, _) = frame.FromSource(Lv95Source.Instance, e, n, h);
        return (x, y);
    }
}
