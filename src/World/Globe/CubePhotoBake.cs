using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// Bakes the aerial photo of every leaf of a cube region (#1507, G2 B3 row 7; docs/notes/tools/cube-fields-pistes-photos.md)
/// from the LV95 build's SWISSIMAGE tiles (<c>photo_E_N.jpg</c>, 512² a km), into each leaf's <see cref="LeafKind.Photo"/>
/// section (<see cref="LeafPhoto"/>: a JPEG over the leaf's (u, v) square, row 0 its south edge). A Godot tool mode, as
/// <see cref="GlobeAlbedoBake"/>, because Godot's <see cref="Image"/> decodes and encodes the JPEGs (the tools have no
/// image library).
///
/// <code>
/// &lt;godot&gt; --headless --path . -- --cubephotos &lt;region dir&gt; [--photos &lt;LV95 chunk dir&gt;] [--size 512] [--quality 0.85]
///   [--cube-box minE,minN,maxE,maxN]
/// </code>
///
/// <para>
/// A pixel's colour is a function of its own place: its centre on the leaf's lattice, on the leaf's ground (the mesh's
/// height there), taken to LV95 rigorously (<see cref="SwissGeodesy"/>) and sampled bilinearly from the photo tiles,
/// across their edges; so leaves meet without a seam of their own. A leaf any of whose pixels the photos do not cover
/// gets no photo (counted). The run ends with a check: the leaves' JPEGs decoded again against the samples they were
/// made from.
/// </para>
/// </summary>
public partial class CubePhotoBake : Node
{
    public static bool Requested() => CmdArgs.Value("--cubephotos") != null;

    /// <summary>The source: SWISSIMAGE 512² over each LV95 km tile.</summary>
    private const int SourceSize = 512;

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        string dir = CmdArgs.Value("--cubephotos")!, photos = CmdArgs.Value("--photos") ?? "D:/UnitSportSwitzerland/terrain_chunks";
        int size = int.Parse(CmdArgs.Value("--size") ?? "512", CultureInfo.InvariantCulture);
        float quality = float.Parse(CmdArgs.Value("--quality") ?? "0.85", CultureInfo.InvariantCulture);
        double[]? box = CmdArgs.Value("--cube-box")?.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var sw = Stopwatch.StartNew();

        // the source tiles, decoded once (each 768 KB as RGB)
        var source = new Dictionary<TileId, byte[]>();
        foreach (var path in Directory.EnumerateFiles(photos, "photo_*.jpg"))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(path).Split('_');
            var img = Image.LoadFromFile(path);
            if (img == null || img.IsEmpty() || img.GetWidth() != SourceSize || img.GetHeight() != SourceSize) continue;
            if (img.GetFormat() != Image.Format.Rgb8) img.Convert(Image.Format.Rgb8);
            source[new TileId(int.Parse(stem[1], CultureInfo.InvariantCulture), int.Parse(stem[2], CultureInfo.InvariantCulture))] = img.GetData();
            img.Dispose();
        }
        GD.Print($"[cubephotos] {source.Count} photo tiles from {photos}");
        if (source.Count == 0) { Fail($"no photo_E_N.jpg in {photos}"); return; }

        int written = 0, partial = 0, leaves = 0;
        var rgbOf = new ConcurrentDictionary<CubeKey, byte[]>();   // for --mosaic only
        long bytes = 0;
        double diffSum = 0;
        long diffN = 0;
        foreach (var bundlePath in Directory.EnumerateFiles(dir, "*.cubl").Order(StringComparer.Ordinal))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(bundlePath).Split('_');
            var frame = new CubeKey(int.Parse(stem[0][1..], CultureInfo.InvariantCulture), int.Parse(stem[1][1..], CultureInfo.InvariantCulture),
                int.Parse(stem[2], CultureInfo.InvariantCulture), int.Parse(stem[3], CultureInfo.InvariantCulture));
            var tiles = new List<CubeTile>();
            var kept = new Dictionary<CubeKey, List<LeafSection>>();   // the leaves a box run leaves alone keep their photo
            foreach (var (key, entry) in CubeBundle.ReadAllEntries(bundlePath))
            {
                if (CubeLeafCodec.ReadSection(entry, LeafKind.Heights) is { } h && InBox(key, box)) tiles.Add(LeafHeights.Decode(h));
                else if (CubeLeafCodec.ReadSection(entry, LeafKind.Photo) is { } old) kept[key] = [old];
            }
            if (tiles.Count == 0) continue;
            leaves += tiles.Count;
            var sections = new ConcurrentDictionary<CubeKey, List<LeafSection>>();
            var made = new ConcurrentBag<(byte[] Jpeg, byte[] Rgb)>();
            await Task.Run(() => Parallel.ForEach(tiles, tile =>
            {
                var rgb = Pixels(tile, size, source);
                if (rgb == null) { Interlocked.Increment(ref partial); sections[tile.Key] = []; return; }
                var img = Image.CreateFromData(size, size, false, Image.Format.Rgb8, rgb);
                var jpeg = img.SaveJpgToBuffer(quality);
                img.Dispose();
                sections[tile.Key] = [LeafPhoto.Encode(new CubePhoto { Format = CubeAlbedoFormat.Jpeg, Width = size, Height = size, Image = jpeg })];
                made.Add((jpeg, rgb));
                if (CmdArgs.Value("--mosaic") != null) rgbOf[tile.Key] = rgb;
            }));
            foreach (var (k, old) in kept) sections.TryAdd(k, old);
            CubeBundle.UpdateLeaves(bundlePath, frame, new Dictionary<CubeKey, List<LeafSection>>(sections), [LeafKind.Photo]);
            foreach (var (jpeg, rgb) in made)
            {
                written++;
                bytes += jpeg.Length;
                // the check: the JPEG decoded again against its samples (mean absolute difference per channel)
                if (diffN < 50_000_000)
                {
                    var back = new Image();
                    back.LoadJpgFromBuffer(jpeg);
                    if (back.GetFormat() != Image.Format.Rgb8) back.Convert(Image.Format.Rgb8);
                    var d = back.GetData();
                    for (int k = 0; k < d.Length; k += 7) { diffSum += Math.Abs(d[k] - rgb[k]); diffN++; }
                    back.Dispose();
                }
            }
            GD.Print($"[cubephotos] frame {frame}: {tiles.Count} leaves, {made.Count} photos, {sw.Elapsed.TotalSeconds:F0} s");
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[cubephotos] {written} of {leaves} leaves with a photo ({partial} not fully covered by the source, none written), {bytes / 1048576.0:F1} MB, "
            + $"{(written > 0 ? bytes / written / 1024 : 0)} KB a leaf; JPEG against its samples: mean {(diffN > 0 ? diffSum / diffN : 0):F2} of 255; {sw.Elapsed.TotalSeconds:F0} s"));
        if (CmdArgs.Value("--mosaic") is { } mosaic) Mosaic(mosaic, rgbOf, size);
        GD.Print("[cubephotos] RESULT: PASS");
        GetTree().Quit();
    }

    /// <summary>
    /// <c>--mosaic PNG</c>: the first leaf whose eight neighbours all have a photo, the 3 × 3 block side by side by
    /// their place on the face (north up), so a seam between leaves would show.
    /// </summary>
    private static void Mosaic(string path, ConcurrentDictionary<CubeKey, byte[]> rgbOf, int size)
    {
        foreach (var key in rgbOf.Keys.OrderBy(k => k.Y).ThenBy(k => k.X))
        {
            var block = new byte[3 * size * 3 * size * 3];
            bool full = true;
            for (int dy = -1; dy <= 1 && full; dy++)
                for (int dx = -1; dx <= 1 && full; dx++)
                {
                    var k = new CubeKey(key.Face, key.Level, key.X + dx, key.Y + dy);
                    if (!rgbOf.TryGetValue(k, out var rgb)) { full = false; break; }
                    for (int py = 0; py < size; py++)
                    {
                        // block row 0 is the north edge: a leaf's row 0 is its south edge
                        int by = (1 - dy) * size + (size - 1 - py), bx = (dx + 1) * size;
                        Array.Copy(rgb, py * size * 3, block, (by * 3 * size + bx) * 3, size * 3);
                    }
                }
            if (!full) continue;
            Image.CreateFromData(3 * size, 3 * size, false, Image.Format.Rgb8, block).SavePng(path);
            GD.Print($"[cubephotos] mosaic round {key} in {path}");
            return;
        }
        GD.Print("[cubephotos] mosaic: no leaf with all eight neighbours photographed");
    }

    private static bool InBox(CubeKey key, double[]? box)
    {
        if (box == null) return true;
        var c = key.Centre;
        var (e, n) = SwissGeodesy.ToLv95(c.LatDeg, c.LonDeg);
        return e >= box[0] && e <= box[2] && n >= box[1] && n <= box[3];
    }

    /// <summary>A leaf's pixels, RGB, row 0 its south edge; null when the source misses any of them.</summary>
    private static byte[]? Pixels(CubeTile tile, int size, Dictionary<TileId, byte[]> source)
    {
        var key = tile.Key;
        int cells = tile.Side - 1;
        var rgb = new byte[size * size * 3];
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                // the pixel's centre as a fractional lattice point, its ground height bilinear between the vertices
                double fi = (px + 0.5) * cells / size, fj = (py + 0.5) * cells / size;
                double h = Bilinear(tile, fi, fj);
                double u = key.U0 + key.Size * fi / cells, v = key.V0 + key.Size * fj / cells;
                var p = CubeSphere.ToEcef(key.Face, u, v, double.IsNaN(h) ? 0 : h);
                var (e, n, _) = SwissGeodesy.FromEcef(p);
                if (!Sample(source, e, n, rgb, (py * size + px) * 3)) return null;
            }
        return rgb;
    }

    private static double Bilinear(CubeTile tile, double fi, double fj)
    {
        int n = tile.Side, i = Math.Min((int)fi, n - 2), j = Math.Min((int)fj, n - 2);
        double a = fi - i, b = fj - j;
        var hs = tile.Heights;
        double h00 = hs[j * n + i], h10 = hs[j * n + i + 1], h01 = hs[(j + 1) * n + i], h11 = hs[(j + 1) * n + i + 1];
        return (h00 * (1 - a) + h10 * a) * (1 - b) + (h01 * (1 - a) + h11 * a) * b;
    }

    /// <summary>The photo's colour at an LV95 point, bilinear between the source pixels' centres across tile edges; false off the source.</summary>
    private static bool Sample(Dictionary<TileId, byte[]> source, double e, double n, byte[] into, int at)
    {
        // global pixel indices: column east from E 0, row north from N 0; pixel (x, y)'s centre at ((x + 0.5), (y + 0.5)) × 1000/512 m
        double gx = e / 1000 * SourceSize - 0.5, gy = n / 1000 * SourceSize - 0.5;
        long x0 = (long)Math.Floor(gx), y0 = (long)Math.Floor(gy);
        double a = gx - x0, b = gy - y0;
        Span<double> sum = stackalloc double[3];
        for (int k = 0; k < 4; k++)
        {
            long x = x0 + (k & 1), y = y0 + (k >> 1);
            double w = ((k & 1) == 0 ? 1 - a : a) * ((k >> 1) == 0 ? 1 - b : b);
            long te = (long)Math.Floor(x / (double)SourceSize), tn = (long)Math.Floor(y / (double)SourceSize);
            if (!source.TryGetValue(new TileId((int)te, (int)tn), out var img)) return false;
            long col = x - te * SourceSize, rowFromSouth = y - tn * SourceSize;
            int o = (int)((SourceSize - 1 - rowFromSouth) * SourceSize + col) * 3;   // image row 0 is the tile's north edge
            sum[0] += w * img[o]; sum[1] += w * img[o + 1]; sum[2] += w * img[o + 2];
        }
        into[at] = (byte)Math.Clamp(Math.Round(sum[0]), 0, 255);
        into[at + 1] = (byte)Math.Clamp(Math.Round(sum[1]), 0, 255);
        into[at + 2] = (byte)Math.Clamp(Math.Round(sum[2]), 0, 255);
        return true;
    }

    private void Fail(string why)
    {
        GD.PrintErr($"[cubephotos] {why}");
        GD.Print("[cubephotos] RESULT: FAIL");
        GetTree().Quit(1);
    }
}
