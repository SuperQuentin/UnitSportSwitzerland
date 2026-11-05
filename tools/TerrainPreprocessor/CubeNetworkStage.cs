using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// RoadGen's network stage on work frames (#1507, G2 B3 4b; docs/notes/tools/roadgen-units.md): the LV95 build's
/// <c>--rewrite</c> (<see cref="TileRewriter"/>, unchanged) over a cube region, frame by frame in the driver's order,
/// through the frame instance of its seams: <see cref="FrameUnits"/> (each leaf a tile, a plane-aligned square round
/// it), <see cref="FrameGround"/> over the region's heights, and <see cref="FrameTileLoop"/>. It reads the raw kinds
/// the road stage left in the temporary <c>.cubl</c> (<c>--cube-roads</c>: <see cref="LeafKind.Roads"/>,
/// <see cref="LeafKind.RoadKeys"/>, <see cref="LeafKind.Parking"/>), the LV95 build's buildings (until stage 3 writes
/// them per leaf) and the region's OSM, SITG, tram stop and place files, moved onto each frame's plane, and writes
/// the network's <see cref="LeafKind.Roads"/> into the region's <c>.cubl</c>, each record in its leaf's anchor frame
/// (<see cref="LeafRecords"/>).
///
/// <code>
/// --cube-network --cube-out REGION [--cube-temp DIR] --chunks LV95_CHUNKS [--osm DIR] [--cube-box minE,minN,maxE,maxN]
///   [--block N] [--halo N]
/// </code>
/// </summary>
public static class CubeNetworkStage
{
    /// <summary>Leaves a block holds along each side (8 × ~610 m: about the LV95 build's 6 km blocks).</summary>
    public const int DefaultBlock = 8;

    /// <summary>Rings of leaves read round a block for context: 2 × ~610 m, about the LV95 build's 1 km tile.</summary>
    public const int DefaultHalo = 2;

    /// <summary>How far round a frame the driver lists margin leaves (the halo of its edge blocks, with room).</summary>
    public const double MarginM = 1600;

    public static bool Wants(IReadOnlyList<string> args) => args.Contains("--cube-network");

    public static int Run(IReadOnlyList<string> args)
    {
        string? region = null, temp = null, chunks = null, osm = null;
        (double MinE, double MinN, double MaxE, double MaxN)? box = null;
        int block = DefaultBlock, halo = DefaultHalo;
        bool lv95Ground = false, planOnly = false, smooth = true;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--cube-network": break;
                case "--cube-out": region = args[++i]; break;
                case "--cube-temp": temp = args[++i]; break;
                case "--chunks": chunks = args[++i]; break;
                case "--osm": osm = args[++i]; break;
                case "--block": block = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--halo": halo = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--jobs": i++; break;   // one frame at a time
                // a diagnostic (#1507): drape on the LV95 build's .terr moved onto the plane instead of the leaf mesh
                case "--lv95-ground": lv95Ground = true; break;
                // the frames a box makes, with their leaves' LV95 extent, and nothing built (where to put a box across a frame edge)
                case "--plan-only": planOnly = true; break;
                case "--no-smooth": smooth = false; break;   // junctions only, centrelines left alone (as RoadGen --rewrite --no-smooth)
                case "--cube-box":
                    var v = args[++i].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length != 4) { Console.Error.WriteLine("--cube-box minE,minN,maxE,maxN (LV95 m)"); return 2; }
                    box = (v[0], v[1], v[2], v[3]);
                    break;
                default: Console.Error.WriteLine($"--cube-network: unknown option {args[i]}"); return 2;
            }
        }
        if (region == null || chunks == null) { Console.Error.WriteLine("--cube-network needs --cube-out REGION (heights, raw roads in its temp) and --chunks LV95_CHUNKS (buildings, places)"); return 2; }
        temp ??= region.TrimEnd('/', '\\') + "_temp";
        osm ??= temp;
        string rawDir = CubeRoadStage.RawDir(temp);
        if (!Directory.Exists(rawDir)) { Console.Error.WriteLine($"--cube-network: no raw roads in {rawDir} (run --cube-roads first)"); return 2; }

        var sw = Stopwatch.StartNew();
        var heights = new RegionHeights(region);
        var raw = new RegionHeights(rawDir);
        var wanted = heights.Leaves.Where(k => box is not { } b || InBox(k, b)).ToList();
        if (wanted.Count == 0) { Console.Error.WriteLine($"--cube-network: no leaf with heights in {region}" + (box != null ? " inside the box" : "")); return 2; }
        var driver = new FrameDriver(wanted, MarginM, SwissSeaLevel.Instance, heights.FrameLevel);

        if (planOnly)
        {
            foreach (var plan in driver.Frames)
            {
                var c = plan.Core.Select(k => SwissGeodesy.ToLv95(k.Centre.LatDeg, k.Centre.LonDeg)).ToList();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  frame {plan.Cell}: {plan.Core.Count} leaves, centres E {c.Min(p => p.E):F0}-{c.Max(p => p.E):F0}, N {c.Min(p => p.N):F0}-{c.Max(p => p.N):F0}"));
            }
            return 0;
        }
        string Beside(string name) => Path.Combine(osm, name);
        var options = new TileRewriter.Options(
            Halo: halo, Smooth: smooth, OsmOverlay: Beside("osm_overlay.tsv"), TramStops: Beside("tram_stops.tsv"),
            SitgSigns: Beside("sitg_signs.tsv"), Places: Path.Combine(chunks, PlaceIndex.FileName), KeepPlans: false);
        Console.WriteLine($"cube network: {wanted.Count} leaves in {driver.Frames.Count} frames, blocks of {block}x{block} leaves, halo {halo}");
        var data = RegionData.Load(options, Console.WriteLine);
        var gantries = data.LoadGantries(options, Console.WriteLine);

        var writer = new LeafWriter(region, driver.FrameLevel, [LeafKind.Roads]);
        var totals = new FrameTileLoop.Counts();
        foreach (var plan in driver.Frames)
        {
            var fs = Stopwatch.StartNew();
            var loop = new FrameTileLoop(plan, driver.LeafLevel, heights, raw, chunks, data, gantries, options, block, totals, lv95Ground);
            var stats = TileRewriter.Run(loop, options, _ => { });
            writer.WriteFrame(plan.Cell, loop.Sections);
            heights.Reload(plan.Cell);
            Console.WriteLine($"  frame {plan.Cell.Describe()}: {plan.Core.Count} leaves, {loop.Sections.Count(e => e.Value.Count > 0)} with roads, "
                + $"{stats.Junctions:N0} junctions, {loop.Frame.Signals} signals, {fs.Elapsed.TotalSeconds:F1} s, "
                + $"working set {Process.GetCurrentProcess().WorkingSet64 / 1e9:F1} GB; {sw.Elapsed.TotalSeconds:F0} s");
        }

        var manifestPath = Path.Combine(region, CubeManifest.FileName);
        var sources = File.Exists(manifestPath) ? CubeManifest.FromJson(File.ReadAllText(manifestPath)).Sources : new List<string>();
        const string source = "road network (RoadGen's network stage on work frames, #1507)";
        if (!sources.Contains(source)) sources.Add(source);
        LeafWriter.WriteManifest(region, driver.LeafLevel, driver.FrameLevel, sources);
        Console.WriteLine($"cube network: {totals.Tiles:N0} leaves with roads, {totals.Segments:N0} segments, {totals.Junctions:N0} junction records, "
            + $"{totals.Signals:N0} signals, {totals.Approaches:N0} approaches, {totals.Paint:N0} paint; {sw.Elapsed.TotalSeconds:F0} s, "
            + $"peak {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB");
        return 0;
    }

    private static bool InBox(CubeKey k, (double MinE, double MinN, double MaxE, double MaxN) b)
    {
        var c = k.Centre;
        var (e, n) = SwissGeodesy.ToLv95(c.LatDeg, c.LonDeg);
        return e >= b.MinE && e <= b.MaxE && n >= b.MinN && n <= b.MaxN;
    }
}

/// <summary>
/// One work frame's tile loop for the network stage (#1507): its core leaves in blocks of
/// <c>block</c> × <c>block</c> leaves (by their index on their face), the frame's inputs on its plane, and the finished
/// tiles turned into their leaves' anchor frames as <see cref="LeafKind.Roads"/> sections (<see cref="Sections"/>).
/// </summary>
public sealed class FrameTileLoop : ITileLoop
{
    /// <summary>What a run wrote, summed over frames.</summary>
    public sealed class Counts
    {
        public long Tiles, Segments, Junctions, Signals, Approaches, Paint;
    }

    private readonly FrameUnits _units;
    private readonly Counts _totals;

    public FramePlan Plan { get; }
    public IReadOnlyList<HashSet<TileId>> Blocks { get; }
    public HashSet<TileId> Wanted { get; }
    public TileRewriter.Inputs Inputs { get; }

    /// <summary>The core leaves' sections, every one listed (empty: no roads), for <see cref="LeafWriter.WriteFrame"/>.</summary>
    public Dictionary<CubeKey, List<LeafSection>> Sections { get; } = new();

    /// <summary>This frame's counts.</summary>
    public Counts Frame { get; } = new();

    public FrameTileLoop(FramePlan plan, int leafLevel, RegionHeights heights, RegionHeights raw, string lv95Chunks,
        RegionData data, GantryContext? gantries, TileRewriter.Options options, int block, Counts totals, bool lv95Ground = false)
    {
        Plan = plan;
        _totals = totals;
        _units = new FrameUnits(plan.Frame, plan.Transform, leafLevel);
        var units = _units;
        var frame = plan.Frame;
        IGround ground = lv95Ground ? new Lv95OnPlane(frame, lv95Chunks) : new FrameGround(frame, heights.Read, leafLevel);

        foreach (var leaf in plan.Core) Sections[leaf] = new List<LeafSection>();
        Wanted = plan.Core.Select(units.Tile).ToHashSet();
        Blocks = plan.Core
            .GroupBy(k => (k.Face, k.X / block, k.Y / block))
            .OrderBy(g => g.Key.Face).ThenBy(g => g.Key.Item3).ThenBy(g => g.Key.Item2)
            .Select(g => g.Select(units.Tile).ToHashSet()).ToList();

        // an LV95 point onto the plane at its ground's height (the region's LV95 files took latitudes and longitudes on
        // the built ground, GeoInput); a bearing by a 10 m step along it; a plane point back to LV95 (the canton lattice)
        (double X, double Y) FromLv95(double e, double n)
        {
            var (x0, y0, _) = frame.FromSource(Lv95Source.Instance, e, n, 0);
            double h = ground.Sample(x0, y0) ?? 0;
            var (x, y, _) = frame.FromSource(Lv95Source.Instance, e, n, h);
            return (x, y);
        }
        double Bearing(double e, double n, double deg)
        {
            double r = deg * Math.PI / 180;
            var (x0, y0) = FromLv95(e, n);
            var (x1, y1) = FromLv95(e + 10 * Math.Sin(r), n + 10 * Math.Cos(r));
            return (Math.Atan2(x1 - x0, y1 - y0) * 180 / Math.PI + 360) % 360;
        }
        (double E, double N) ToLv95(double x, double y)
        {
            var (lat, lon) = frame.Plane.FromPlane(x, y);
            return SwissGeodesy.ToLv95(lat, lon);
        }

        var buildings = new FrameBuildings(lv95Chunks, frame, units);
        Inputs = data.On(units, FromLv95, Bearing, ToLv95,
            roads: id => RawRoads(raw, units, id),
            ground: _ => ground,
            buildings: buildings.For,
            parking: id => RawLots(raw, units, id),
            gantries: gantries);
    }

    /// <summary>A leaf's raw roads and keys from the temporary <c>.cubl</c>, in the stage's tile-local frame (a fresh copy).</summary>
    private static (RoadTile? Tile, RawRoads.Key?[]? Keys) RawRoads(RegionHeights raw, FrameUnits units, TileId id)
    {
        var leaf = units.Leaf(id);
        if (raw.ReadKind(leaf, LeafKind.Roads) is not { } sec) return (null, null);
        var d = LeafRoads.Decode(sec);
        var tile = new RoadTile
        {
            Id = id, Segments = d.Segments, Junctions = d.Junctions, Version = d.Version, Flags = d.Flags,
            Paint = d.Paint, PointProps = d.PointProps, LinearProps = d.LinearProps, AreaProps = d.AreaProps,
            Signals = d.Signals, Approaches = d.Approaches, Transitions = d.Transitions, Parking = d.Parking,
            JunctionPaths = d.JunctionPaths, CatenarySupports = d.CatenarySupports, CatenaryWires = d.CatenaryWires,
        };
        LeafRecords.FromLeaf(tile, units, id);
        RawRoads.Key?[]? keys = raw.ReadKind(leaf, LeafKind.RoadKeys) is { } ks
            ? LeafRoadKeys.Decode(ks).Select(k => k is { } a ? new RawRoads.Key(a.Uuid, a.Part, a.FromM) : (RawRoads.Key?)null).ToArray()
            : null;
        return (tile, keys);
    }

    /// <summary>A leaf's car park rings on the plane.</summary>
    private static List<RawParking.Lot> RawLots(RegionHeights raw, FrameUnits units, TileId id)
    {
        var leaf = units.Leaf(id);
        if (raw.ReadKind(leaf, LeafKind.Parking) is not { } sec) return [];
        var t = units.TransformOf(id);
        var lots = new List<RawParking.Lot>();
        foreach (var lot in LeafParking.Decode(sec))
        {
            var ring = new double[lot.Count * 2];
            for (int i = 0; i < lot.Count; i++)
            {
                var (x, y, _) = t.LocalToPlane(lot.Points[i * 3], lot.Points[i * 3 + 1], lot.Points[i * 3 + 2]);
                (ring[i * 2], ring[i * 2 + 1]) = (x, y);
            }
            lots.Add(new RawParking.Lot(lot.Cover, lot.Source, ring));
        }
        return lots;
    }

    public void Write(RoadTile tile, byte[] bytes)
    {
        foreach (var c in (ReadOnlySpan<Counts>)[Frame, _totals])
        {
            c.Tiles++;
            c.Segments += tile.Segments.Count;
            c.Junctions += tile.Junctions.Count;
            c.Signals += tile.Signals.Count;
            c.Approaches += tile.Approaches.Count;
            c.Paint += tile.Paint.Count;
        }
        LeafRecords.ToLeaf(tile, _units);
        Sections[_units.Leaf(tile.Id)] = [LeafRoads.Encode(tile)];
    }

    public void Finish() { }
}

/// <summary>
/// The LV95 build's buildings (<c>.bldg</c>) as the network stage reads them on a work frame (#1507), until stage 3
/// writes them per leaf: every triangle moved onto the frame's plane rigorously (<see cref="WorkFrame.FromSource"/>),
/// each LV95 tile once (the last <see cref="Cached"/> kept: a block reads a few km round itself); a leaf's tile is every
/// building whose plane box reaches its <see cref="FrameUnits.Box"/> square, in that square's tile-local frame (the walls
/// the street planner stops at, the footprints poles keep off). Plane positions are kept as floats: a millimetre at
/// 15 km from the frame's centre, against the walls' 0.5 m raster.
/// </summary>
public sealed class FrameBuildings(string lv95Chunks, WorkFrame frame, FrameUnits units)
{
    public const int Cached = 100;

    /// <summary>A building on the plane: x, height above sea level, y per vertex, and its plane box.</summary>
    private sealed record Moved(Building Source, float[] Plane, double MinX, double MinY, double MaxX, double MaxY);

    private readonly Dictionary<TileId, List<Moved>> _lv95 = new();

    public BuildingTile? For(TileId id)
    {
        double west = units.West(id), north = units.North(id), east = west + FrameUnits.Box, south = north - FrameUnits.Box;
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        foreach (var (x, y) in (ReadOnlySpan<(double, double)>)[(west, north), (east, north), (west, south), (east, south)])
        {
            var (lat, lon) = frame.Plane.FromPlane(x, y);
            var (e, n) = SwissGeodesy.ToLv95(lat, lon);
            (minE, minN, maxE, maxN) = (Math.Min(minE, e), Math.Min(minN, n), Math.Max(maxE, e), Math.Max(maxN, n));
        }
        var list = new List<Building>();
        for (int te = (int)Math.Floor((minE - 50) / 1000); te <= (int)Math.Floor((maxE + 50) / 1000); te++)
            for (int tn = (int)Math.Floor((minN - 50) / 1000); tn <= (int)Math.Floor((maxN + 50) / 1000); tn++)
                foreach (var m in Tile(new TileId(te, tn)))
                {
                    if (m.MaxX < west || m.MinX > east || m.MaxY < south || m.MinY > north) continue;
                    var tris = new float[m.Plane.Length];
                    for (int k = 0; k + 2 < tris.Length; k += 3)
                        (tris[k], tris[k + 1], tris[k + 2]) = ((float)(m.Plane[k] - west), m.Plane[k + 1], (float)(north - m.Plane[k + 2]));
                    var b = m.Source;
                    list.Add(new Building { Kind = b.Kind, Egid = b.Egid, YearBuilt = b.YearBuilt, Floors = b.Floors, MinY = b.MinY, MaxY = b.MaxY, Triangles = tris });
                }
        return list.Count == 0 ? null : new BuildingTile { Id = id, Buildings = list };
    }

    private List<Moved> Tile(TileId lv95)
    {
        if (_lv95.TryGetValue(lv95, out var list)) return list;
        if (_lv95.Count >= Cached) _lv95.Clear();
        list = new List<Moved>();
        if (Facades.Read(lv95Chunks, lv95) is { } tile)
            foreach (var b in tile.Buildings)
            {
                var t = b.Triangles;
                var plane = new float[t.Length];
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    var (x, y, h) = frame.FromSource(Lv95Source.Instance, lv95.MinE + t[k], lv95.MaxN - t[k + 2], t[k + 1]);
                    (plane[k], plane[k + 1], plane[k + 2]) = ((float)x, (float)h, (float)y);
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                }
                list.Add(new Moved(b, plane, minX, minY, maxX, maxY));
            }
        _lv95[lv95] = list;
        return list;
    }
}

/// <summary>
/// A diagnostic ground (#1507, <c>--lv95-ground</c>): the LV95 build's <c>.terr</c> at a plane point taken to LV95 (its
/// latitude and longitude), so a frame run's differences from the LV95 build can be split into the ground's share
/// (the leaf mesh against the 1 m grid) and the rest. Heights above sea level in both.
/// </summary>
public sealed class Lv95OnPlane(WorkFrame frame, string lv95Chunks) : IGround
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TileId, ChunkGrid?> _grids = new();

    public double? Sample(double x, double y)
    {
        var (lat, lon) = frame.Plane.FromPlane(x, y);
        var (e, n) = SwissGeodesy.ToLv95(lat, lon);
        var id = TileId.FromLv95(e, n);
        var grid = _grids.GetOrAdd(id, k => TileRewriter.LoadGrid(lv95Chunks, k));
        return grid?.SampleHeight(e, n);
    }
}
