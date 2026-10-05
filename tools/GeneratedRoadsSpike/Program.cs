using System.Diagnostics;
using System.Globalization;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

// Spike for #559: can RoadGen's network stage run per generated tile?
//   dotnet run --project tools/GeneratedRoadsSpike -c Release -- --out test_output/roadgen_spike [--size 6] [--villages 3]
//       [--halo 1] [--no-keys: raw roads without line keys] [--no-smooth] [--dump: segments of a differing tile]
// 1. generates a size x size block round each of the biggest villages near the anchor (+2 tiles of
//    context), writing raw roads, full grids and buildings like the preprocessor's temp output
// 2. reference: TileRewriter over the whole block at once
// 3. single: TileRewriter one tile at a time with a one-tile halo (what a runtime source would do)
// 4. compares the two byte for byte, and counts what the stage added.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string outDir = Arg("--out") ?? Path.Combine("test_output", "roadgen_spike");
int size = int.Parse(Arg("--size") ?? "6");
int villages = int.Parse(Arg("--villages") ?? "3");
int halo = int.Parse(Arg("--halo") ?? "1");
bool smooth = !args.Contains("--no-smooth");
bool noKeys = args.Contains("--no-keys");
const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N

var world = new ProceduralWorld(AnchorE, AnchorN);
var picks = world.VillageCentres()
    .Where(v => Math.Abs(v.E - AnchorE) < 20000 && Math.Abs(v.N - AnchorN) < 20000)
    .OrderByDescending(v => v.Buildings).Take(villages).ToList();
Console.WriteLine($"villages: {string.Join(", ", picks.Select(v => $"{v.Name} ({v.Buildings} buildings) at {v.E:F0},{v.N:F0}"))}");

int totalTiles = 0, identical = 0;
double singleMs = 0, generateMs = 0;
var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
foreach (var v in picks)
{
    var centre = TileId.FromLv95(v.E, v.N);
    var region = new List<TileId>();
    for (int de = 0; de < size; de++)
    for (int dn = 0; dn < size; dn++)
        region.Add(new TileId(centre.E - size / 2 + de, centre.N - size / 2 + dn));
    var context = Grow(region, halo + 1);

    string root = Path.Combine(outDir, $"v{centre.E}_{centre.N}");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    string raw = Path.Combine(root, "raw"), input = Path.Combine(root, "input");
    Directory.CreateDirectory(raw);
    Directory.CreateDirectory(input);

    var sw = Stopwatch.StartNew();
    int rawSegments = 0;
    foreach (var id in context)
    {
        using (var s = File.Create(Path.Combine(input, ChunkFormat.ChunkFileName(id))))
            ChunkCodec.Encode(world.BuildGrid(id, 1), s);
        if (world.BuildBuildings(id) is { } b)
            using (var s = File.Create(Path.Combine(input, BuildingFormat.FileName(id))))
                BuildingCodec.Encode(b, s);
        var (r, keys) = world.BuildRoadsKeyed(id);
        if (r is not null)
        {
            RawRoads.Write(raw, r, noKeys ? null : keys.Select(k => (RawRoads.Key?)new RawRoads.Key(k.Line, 0, k.FromM)).ToList());
            if (region.Contains(id)) rawSegments += r.Segments.Count;
        }
    }
    generateMs += sw.Elapsed.TotalMilliseconds;
    Console.WriteLine($"\n{root}: {context.Count} tiles generated in {sw.Elapsed.TotalSeconds:F1} s, {rawSegments} raw segments in the block");

    string refDir = Copy(input, Path.Combine(root, "ref"));
    string oneDir = Copy(input, Path.Combine(root, "single"));
    var targets = region.Where(id => File.Exists(RawRoads.RoadPath(raw, id))).ToList();

    sw.Restart();
    var refStats = TileRewriter.Run(refDir, targets,
        new TileRewriter.Options(BlockSize: 100000, Halo: 1, Smooth: smooth, RawDir: raw, AuditHeights: false), _ => { });
    Console.WriteLine($"  reference (one block): {sw.Elapsed.TotalMilliseconds:F0} ms, {refStats.Junctions} junctions, {Grow(targets, 1).Count} context tiles");

    sw.Restart();
    TileRewriter.Run(oneDir, targets,
        new TileRewriter.Options(BlockSize: 1, Halo: halo, Smooth: smooth, RawDir: raw, AuditHeights: false), _ => { });
    double ms = sw.Elapsed.TotalMilliseconds;
    singleMs += ms;
    Console.WriteLine($"  single (1 tile + halo 1): {ms:F0} ms for {targets.Count} tiles, {ms / Math.Max(1, targets.Count):F0} ms a tile");

    // what the file-based stage spends just reading its inputs for one tile + halo 1
    sw.Restart();
    foreach (var id in targets)
        foreach (var n in Grow([id], 1))
        {
            string g = Path.Combine(oneDir, ChunkFormat.ChunkFileName(n)), bl = Path.Combine(oneDir, BuildingFormat.FileName(n));
            if (File.Exists(g)) using (var s = File.OpenRead(g)) ChunkCodec.Decode(s);
            if (File.Exists(bl)) using (var s = File.OpenRead(bl)) BuildingCodec.Decode(s);
        }
    Console.WriteLine($"  of which reading grids + buildings: ~{sw.Elapsed.TotalMilliseconds / Math.Max(1, targets.Count):F0} ms a tile");

    foreach (var id in targets)
    {
        totalTiles++;
        string name = RoadFormat.FileName(id);
        byte[] a = File.ReadAllBytes(Path.Combine(refDir, name)), b = File.ReadAllBytes(Path.Combine(oneDir, name));
        var ta = Decode(a);
        Count(counts, ta);
        if (a.AsSpan().SequenceEqual(b)) { identical++; continue; }
        Console.WriteLine($"  DIFF {id.E},{id.N}: {Diff(ta, Decode(b))}");
        if (args.Contains("--dump")) { Dump.Segments("ref", ta); Dump.Segments("single", Decode(b)); }
    }
}

Console.WriteLine($"\nRESULT tiles={totalTiles} identical={identical} " +
                  $"single_ms_per_tile={singleMs / Math.Max(1, totalTiles):F0}");
Console.WriteLine("added on the generated network (reference):");
foreach (var (k, n) in counts) Console.WriteLine($"  {k,-28} {n}");
return identical == totalTiles ? 0 : 1;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static List<TileId> Grow(List<TileId> tiles, int by)
{
    var set = new HashSet<TileId>();
    foreach (var t in tiles)
        for (int de = -by; de <= by; de++)
        for (int dn = -by; dn <= by; dn++)
            set.Add(new TileId(t.E + de, t.N + dn));
    return set.ToList();
}

static string Copy(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    return to;
}

static RoadTile Decode(byte[] bytes)
{
    using var s = new MemoryStream(bytes);
    return RoadCodec.Decode(s);
}

static void Count(SortedDictionary<string, int> c, RoadTile t)
{
    void Add(string k, int n = 1) => c[k] = c.GetValueOrDefault(k) + n;
    Add("segments", t.Segments.Count);
    Add("junction polygons", t.Junctions.Count);
    foreach (var p in t.Paint) Add($"paint {p.Type}");
    foreach (var p in t.PointProps) Add($"point {p.Type}");
    foreach (var p in t.LinearProps) Add($"linear {p.Type}");
    foreach (var p in t.AreaProps) Add($"area {p.Type}");
    Add("signals", t.Signals.Count);
    Add("approaches", t.Approaches.Count);
}

static string Diff(RoadTile a, RoadTile b)
{
    var parts = new List<string>();
    void Cmp(string what, int x, int y) { if (x != y) parts.Add($"{what} {x}->{y}"); }
    Cmp("segments", a.Segments.Count, b.Segments.Count);
    Cmp("junctions", a.Junctions.Count, b.Junctions.Count);
    Cmp("paint", a.Paint.Count, b.Paint.Count);
    Cmp("point", a.PointProps.Count, b.PointProps.Count);
    Cmp("linear", a.LinearProps.Count, b.LinearProps.Count);
    Cmp("area", a.AreaProps.Count, b.AreaProps.Count);
    Cmp("signals", a.Signals.Count, b.Signals.Count);
    Cmp("approaches", a.Approaches.Count, b.Approaches.Count);
    if (parts.Count == 0 && a.Segments.Count == b.Segments.Count)
    {
        double worst = 0; int pointsDiff = 0;
        for (int i = 0; i < a.Segments.Count; i++)
        {
            var (p, q) = (a.Segments[i].Points, b.Segments[i].Points);
            if (p.Length != q.Length) { pointsDiff++; continue; }
            for (int j = 0; j < p.Length; j++) worst = Math.Max(worst, Math.Abs(p[j] - q[j]));
        }
        parts.Add($"same counts; {pointsDiff} segments with other point counts, worst coordinate delta {worst:F3} m");
    }
    return string.Join(", ", parts);
}

static class Dump
{
    public static void Segments(string label, RoadTile t)
    {
        Console.WriteLine($"    {label}:");
        foreach (var s in t.Segments.OrderBy(s => s.Points[0]).ThenBy(s => s.Points[2]))
        {
            var p = s.Points; int n = p.Length / 3;
            Console.WriteLine($"      {s.Class,-10} w{s.Width:F1} n{n,4} ({p[0]:F1},{p[2]:F1}) -> ({p[(n - 1) * 3]:F1},{p[(n - 1) * 3 + 2]:F1}) {s.Flags} {s.Attributes}");
        }
    }
}
