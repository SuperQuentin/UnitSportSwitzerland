using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// Bakes the globe's colour (#1430 H4; docs/plans/ellipsoid-handover.md §6a, tools/cube-tiles note)
/// into a coarse cube region built by <c>--cube-global</c>: for every tile of every level its
/// 12 monthly images from NASA's Blue Marble Next Generation (topography and bathymetry, 2 km), as
/// WebP, in <c>.cuba</c> bundles beside the heights, and the manifest's levels told their colour.
/// A Godot tool mode because Godot's <see cref="Image"/> decodes the JPEGs and encodes the WebP the
/// game reads (the tools have no image library), and later samples our own tile colours over
/// Switzerland.
///
/// <code>
/// &lt;godot&gt; --headless --path . -- --globealbedo &lt;region dir&gt; --bmng &lt;dir of world.topo.bathy.2004MM.3x21600x10800.jpg&gt; [--months 1-12]
/// &lt;godot&gt; --headless --path . -- --globealbedo &lt;region dir&gt; --body moon --colour &lt;equirectangular JPEG or PNG&gt;
/// </code>
///
/// <para>
/// <b>Another body</b> (#1849, <c>--body</c>): a pyramid that <c>--cube-body</c> built on that body's cube gets one
/// colour that never changes (no months), from one whole-body equirectangular map (the Moon's: the CGI Moon Kit's LROC
/// colour, Hapke-normalised so it carries no shadows of its own), on the body's cube and radius; no lakes, no colour of
/// our own.
/// </para>
///
/// <para>
/// A pixel's colour is a function of the pixel's own place (its centre's geodetic normal): the mean of
/// a k × k grid of bilinear samples over its footprint, so tiles meet without a seam of their own.
/// </para>
/// </summary>
public partial class GlobeAlbedoBake : Node
{
    public static bool Requested() => CmdArgs.Value("--globealbedo") != null;

    /// <summary>A level's colour side: ~10-40 km a pixel on the shipped levels 0-2, ~2.4 km (Blue Marble's own) on 3 and 4.</summary>
    public static int SizeFor(int level) => level == 3 ? 512 : 256;

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        if (CmdArgs.Value("--body") is { } body) { await RunBodyAsync(body); return; }
        string dir = CmdArgs.Value("--globealbedo")!, bmng = CmdArgs.Value("--bmng") ?? "D:/UnitSportSwitzerland/data/global/bmng";
        var months = (CmdArgs.Value("--months") ?? "1-12").Split('-').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        int first = months[0], last = months[^1];
        string manifestPath = System.IO.Path.Combine(dir, CubeManifest.FileName);
        var manifest = CubeManifest.FromJson(File.ReadAllText(manifestPath));
        var keys = manifest.Tiles.Select(t => CubeKey.Parse(t.Key)).ToList();
        var sw = Stopwatch.StartNew();
        GD.Print($"[globealbedo] {keys.Count} tiles, months {first}-{last}");

        // the world's own colour over its region (--ours <chunk dir>, default the D: build; "off" none):
        // worked out once a tile, then blended into every month
        string? oursDir = CmdArgs.Value("--ours") ?? "D:/UnitSportSwitzerland/terrain_chunks";
        var oursByKey = new ConcurrentDictionary<CubeKey, (Vector3[] Stored, float[] Weight)>();
        if (oursDir != "off" && Directory.Exists(oursDir))
        {
            var ours = new OurColours(oursDir);
            await Task.Run(() => Parallel.ForEach(keys, key =>
            {
                if (OursFor(ours, key, SizeFor(key.Level)) is { } o) oursByKey[key] = o;
            }));
            GD.Print($"[globealbedo] the world's own colour from {ours.Count} tiles into {oursByKey.Count} globe tiles, {sw.Elapsed.TotalSeconds:F0} s");
        }

        // month by month: one 21600 x 10800 image (700 MB) at a time
        var images = new ConcurrentDictionary<CubeKey, byte[][]>();
        foreach (var k in keys) images[k] = new byte[last - first + 1][];
        for (int m = first; m <= last; m++)
        {
            string file = System.IO.Path.Combine(bmng, $"world.topo.bathy.2004{m:D2}.3x21600x10800.jpg");
            var src = Image.LoadFromFile(file);
            if (src == null || src.IsEmpty()) { Fail($"cannot read {file}"); return; }
            if (src.GetFormat() != Image.Format.Rgb8) src.Convert(Image.Format.Rgb8);
            var rgb = src.GetData();
            int lakes = PaintLakes(rgb);
            var map = new EquirectMap(rgb, src.GetWidth(), src.GetHeight());
            src.Dispose();
            int month = m;
            await Task.Run(() => Parallel.ForEach(keys, key =>
            {
                int size = SizeFor(key.Level);
                var px = Pixels(map, key, size);
                if (oursByKey.TryGetValue(key, out var o)) Blend(px, o.Stored, o.Weight);
                // WebP from Godot on a worker: Image is a Resource, created and encoded per tile
                var img = Image.CreateFromData(size, size, false, Image.Format.Rgb8, px);
                images[key][month - first] = img.SaveWebpToBuffer(true, 0.85f);
                img.Dispose();
            }));
            GD.Print($"[globealbedo] month {m}: {lakes} lake pixels painted, {sw.Elapsed.TotalSeconds:F0} s");
        }

        long bytes = 0;
        foreach (var bundle in keys.GroupBy(k => k.AncestorAt(CubeKey.PyramidBundleLevel(k.Level))))
        {
            var entries = bundle.OrderBy(k => k.Level).ThenBy(k => k.Y).ThenBy(k => k.X).Select(k => (k, CubeAlbedoCodec.Encode(new CubeAlbedo
            {
                Key = k,
                Size = SizeFor(k.Level),
                Months = images[k],
            }))).ToList();
            CubeBundle.Write(System.IO.Path.Combine(dir, CubeBundle.AlbedoFileName(bundle.Key)), bundle.Key, entries, CubeBundle.AlbedoMagic);
            bytes += entries.Sum(e => (long)e.Item2.Length);
        }
        foreach (var lv in manifest.Levels)
        {
            lv.Albedo = SizeFor(lv.Level);
            lv.AlbedoBundleLevel = CubeKey.PyramidBundleLevel(lv.Level);
        }
        manifest.Sources.RemoveAll(s => s.StartsWith("NASA", StringComparison.Ordinal) || s.StartsWith("the world's", StringComparison.Ordinal));
        if (!oursByKey.IsEmpty) manifest.Sources.Add($"the world's own ground colour (CoverPalette) over its region, feathered {OurColours.FeatherM / 1000:F0} km into it, stored for the Cartoon globe");
        manifest.Sources.Add("NASA Earth Observatory, Blue Marble Next Generation with topography and bathymetry, 2004, monthly, 21600 x 10800 (credit NASA Earth Observatory)");
        File.WriteAllText(manifestPath, manifest.ToJson());
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[globealbedo] RESULT: ok — {keys.Count} tiles x {last - first + 1} months, {bytes / 1e6:F1} MB, {sw.Elapsed.TotalSeconds:F0} s"));
        GetTree().Quit();
    }

    /// <summary>Another body's colour (#1849): one image a tile from <c>--colour</c>, on the body's cube.</summary>
    private async Task RunBodyAsync(string body)
    {
        string dir = CmdArgs.Value("--globealbedo")!, file = CmdArgs.Value("--colour") ?? "";
        var cube = body.ToLowerInvariant() switch { "moon" => CubeShape.Moon, "mars" => CubeShape.Mars, _ => null };
        if (cube == null) { Fail($"no body called {body} (moon, mars)"); return; }
        string manifestPath = System.IO.Path.Combine(dir, CubeManifest.FileName);
        var manifest = CubeManifest.FromJson(File.ReadAllText(manifestPath));
        if (!string.Equals(manifest.Body, body, StringComparison.OrdinalIgnoreCase)) { Fail($"{dir} is the pyramid of {manifest.Body ?? "Earth"}, not {body}"); return; }
        var keys = manifest.Tiles.Select(t => CubeKey.Parse(t.Key)).ToList();
        var sw = Stopwatch.StartNew();
        var src = Image.LoadFromFile(file);
        if (src == null || src.IsEmpty()) { Fail($"cannot read {file}"); return; }
        if (src.GetFormat() != Image.Format.Rgb8) src.Convert(Image.Format.Rgb8);
        var map = new EquirectMap(src.GetData(), src.GetWidth(), src.GetHeight());
        GD.Print($"[globealbedo] {body}: {keys.Count} tiles from {System.IO.Path.GetFileName(file)} {src.GetWidth()} x {src.GetHeight()}");
        src.Dispose();
        var images = new ConcurrentDictionary<CubeKey, byte[]>();
        await Task.Run(() => Parallel.ForEach(keys, key =>
        {
            int size = SizeFor(key.Level);
            var img = Image.CreateFromData(size, size, false, Image.Format.Rgb8, Pixels(map, key, size, cube));
            images[key] = img.SaveWebpToBuffer(true, 0.85f);
            img.Dispose();
        }));
        long bytes = 0;
        foreach (var bundle in keys.GroupBy(k => k.AncestorAt(CubeKey.PyramidBundleLevel(k.Level))))
        {
            var entries = bundle.OrderBy(k => k.Level).ThenBy(k => k.Y).ThenBy(k => k.X)
                .Select(k => (k, CubeAlbedoCodec.Encode(new CubeAlbedo { Key = k, Size = SizeFor(k.Level), Months = [images[k]] }))).ToList();
            CubeBundle.Write(System.IO.Path.Combine(dir, CubeBundle.AlbedoFileName(bundle.Key)), bundle.Key, entries, CubeBundle.AlbedoMagic);
            bytes += entries.Sum(e => (long)e.Item2.Length);
        }
        foreach (var lv in manifest.Levels)
        {
            lv.Albedo = SizeFor(lv.Level);
            lv.AlbedoBundleLevel = CubeKey.PyramidBundleLevel(lv.Level);
        }
        manifest.Sources.RemoveAll(s => s.StartsWith("colour:", StringComparison.Ordinal));
        manifest.Sources.Add($"colour: {System.IO.Path.GetFileName(file)} (NASA, public domain)");
        File.WriteAllText(manifestPath, manifest.ToJson());
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[globealbedo] RESULT: ok — {body}, {keys.Count} tiles, {bytes / 1e6:F1} MB, {sw.Elapsed.TotalSeconds:F0} s"));
        GetTree().Quit();
    }

    private void Fail(string why)
    {
        GD.PrintErr($"[globealbedo] RESULT: FAILED — {why}");
        GetTree().Quit(1);
    }

    /// <summary>The colour Blue Marble's black inland water is painted, sRGB: a deep lake's blue, darker than the shelf seas.</summary>
    public static readonly (byte R, byte G, byte B) Lake = (22, 46, 92);

    /// <summary>
    /// Blue Marble draws inland water black (Ladoga, Onega, the Great Lakes, Geneva's): every pixel with
    /// all three channels under 12 becomes <see cref="Lake"/>. The sea is never that dark, nor land by
    /// day (the darkest forests are ~20). Returns how many were painted.
    /// </summary>
    public static int PaintLakes(byte[] rgb)
    {
        int painted = 0;
        object gate = new();
        Parallel.For(0, rgb.Length / 3 / 65536 + 1, chunk =>
        {
            int count = 0, from = chunk * 65536, to = Math.Min(from + 65536, rgb.Length / 3);
            for (int p = from; p < to; p++)
            {
                int o = p * 3;
                if (rgb[o] < 12 && rgb[o + 1] < 12 && rgb[o + 2] < 12)
                {
                    rgb[o] = Lake.R; rgb[o + 1] = Lake.G; rgb[o + 2] = Lake.B;
                    count++;
                }
            }
            lock (gate) painted += count;
        });
        return painted;
    }

    /// <summary>
    /// The world's colour for a globe tile over its region: per pixel what to store (for the Cartoon
    /// shader, <see cref="OurColours.ForCartoonShader"/>) and how much it counts; null when the tile is
    /// nowhere near the region.
    /// </summary>
    public static (Vector3[] Stored, float[] Weight)? OursFor(OurColours ours, CubeKey key, int size)
    {
        // near Switzerland at all (the tile's angular radius plus 3°), then its LV95 box against the data's
        var c = key.Centre;
        double radius = 90.0 / key.Side * 0.8 + 3;
        if (Math.Abs(c.LatDeg - 46.8) > radius + 2 || Math.Abs(c.LonDeg - 8.2) * Math.Cos(46.8 * Math.PI / 180) > radius + 3) return null;
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        foreach (var (u, v) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0), (0.5, 0.0), (0.0, 0.5), (1.0, 0.5), (0.5, 1.0), (0.5, 0.5) })
        {
            var (e, n, _) = SwissGeodesy.FromEcef(CubeSphere.ToEcef(key.Face, key.U0 + u * key.Size, key.V0 + v * key.Size, 0));
            minE = Math.Min(minE, e); maxE = Math.Max(maxE, e); minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
        }
        if (!ours.Touches(minE, minN, maxE, maxN)) return null;

        var stored = new Vector3[size * size];
        var weight = new float[size * size];
        double step = key.Size / size, half = 0.5 * (Math.PI / 2 * 6_371_000 / key.Side / size);
        bool any = false;
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                var (e, n, _) = SwissGeodesy.FromEcef(CubeSphere.ToEcef(key.Face, key.U0 + (px + 0.5) * step, key.V0 + (py + 0.5) * step, 0));
                if (!ours.Touches(e - half, n - half, e + half, n + half)) continue;
                var (colour, w) = ours.At(e, n, half);
                if (w <= 0) continue;
                stored[py * size + px] = OurColours.ForCartoonShader(colour);
                weight[py * size + px] = w;
                any = true;
            }
        return any ? (stored, weight) : null;
    }

    /// <summary>The month's sRGB pixels with the world's colour mixed in, in linear light, by its weight.</summary>
    public static void Blend(byte[] px, Vector3[] stored, float[] weight)
    {
        for (int p = 0; p < weight.Length; p++)
        {
            float w = weight[p];
            if (w <= 0) continue;
            int o = p * 3;
            for (int c = 0; c < 3; c++)
            {
                float photo = Mathf.Pow(px[o + c] / 255f, 2.2f);
                float mixed = Mathf.Lerp(photo, stored[p][c], w);
                px[o + c] = (byte)Math.Clamp(Math.Round(Mathf.Pow(mixed, 1 / 2.2f) * 255), 0, 255);
            }
        }
    }

    /// <summary>
    /// A tile's size² RGB pixels, row-major from its south-west corner (row 0 along its south edge). On the Earth's cube by
    /// default; on another body's (#1849) a pixel's latitude is planetocentric, as its maps are gridded.
    /// </summary>
    public static byte[] Pixels(EquirectMap map, CubeKey key, int size, CubeShape? cube = null)
    {
        var px = new byte[size * size * 3];
        double step = key.Size / size;
        double radius = cube?.Shape.A ?? 6_371_000;
        // half a pixel at a face centre, metres: the footprint's half side
        double half = 0.5 * (Math.PI / 2 * radius / key.Side / size);
        for (int py = 0; py < size; py++)
            for (int pxi = 0; pxi < size; pxi++)
            {
                var n = cube == null
                    ? CubeSphere.Normal(key.Face, key.U0 + (pxi + 0.5) * step, key.V0 + (py + 0.5) * step)
                    : cube.ToEcef(key.Face, key.U0 + (pxi + 0.5) * step, key.V0 + (py + 0.5) * step, 0);
                double lat = Math.Atan2(n.Z, Math.Sqrt(n.X * n.X + n.Y * n.Y)) * 180 / Math.PI, lon = Math.Atan2(n.Y, n.X) * 180 / Math.PI;
                var (r, g, b) = map.Footprint(lat, lon, half, radius);
                int o = (py * size + pxi) * 3;
                px[o] = r; px[o + 1] = g; px[o + 2] = b;
            }
        return px;
    }
}

/// <summary>An equirectangular RGB8 map of the whole Earth (row 0 at 90° N, column 0 at 180° W), sampled bilinearly with longitude wrapping.</summary>
public sealed class EquirectMap(byte[] rgb, int width, int height)
{
    public int Width => width;
    public int Height => height;

    public (double R, double G, double B) At(double lat, double lon)
    {
        double fx = (lon + 180) / 360 * width - 0.5, fy = Math.Clamp((90 - lat) / 180 * height - 0.5, 0, height - 1);
        int x0 = (int)Math.Floor(fx), y0 = Math.Min((int)fy, height - 2);
        double tx = fx - x0, ty = fy - y0;
        int xa = ((x0 % width) + width) % width, xb = (xa + 1) % width;
        long r0 = (long)y0 * width, r1 = r0 + width;
        double C(long row, int x, int c) => rgb[(row + x) * 3 + c];
        double Mix(int c) => (C(r0, xa, c) * (1 - tx) + C(r0, xb, c) * tx) * (1 - ty) + (C(r1, xa, c) * (1 - tx) + C(r1, xb, c) * tx) * ty;
        return (Mix(0), Mix(1), Mix(2));
    }

    /// <summary>The mean colour over a square footprint <paramref name="halfM"/> metres each way: k × k bilinear samples, k 1 to 6, on a body of radius <paramref name="R"/>.</summary>
    public (byte R, byte G, byte B) Footprint(double lat, double lon, double halfM, double R = 6_371_000)
    {
        double pixelM = 360.0 / width * Math.PI / 180 * R;
        int k = Math.Clamp((int)Math.Ceiling(2 * halfM / pixelM), 1, 6);
        double cos = Math.Max(Math.Cos(lat * Math.PI / 180), 0.01), sr = 0, sg = 0, sb = 0;
        for (int b = 0; b < k; b++)
        {
            double dy = -halfM + (b + 0.5) * 2 * halfM / k;
            double la = Math.Clamp(lat + dy / R * 180 / Math.PI, -90, 90);
            for (int a = 0; a < k; a++)
            {
                double dx = -halfM + (a + 0.5) * 2 * halfM / k;
                var (r, g, bl) = At(la, lon + dx / (R * cos) * 180 / Math.PI);
                sr += r; sg += g; sb += bl;
            }
        }
        double kk = k * k;
        return ((byte)Math.Round(sr / kk), (byte)Math.Round(sg / kk), (byte)Math.Round(sb / kk));
    }
}
