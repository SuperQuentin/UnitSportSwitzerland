using System.Diagnostics;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Biomes;

namespace UnitSport.World;

/// <summary>
/// Bakes the fill's photo statistics (#1809, <see cref="PhotoStats"/>) from Blue Marble NG's 12 months, the source of the
/// shipped globe colour (<see cref="GlobeAlbedoBake"/>): per cell of a latitude/longitude grid the sRGB of its greenest
/// month, its snowy months and whether it is water all year. A Godot tool mode because Godot's <see cref="Image"/>
/// decodes the JPEGs. The result is committed (<c>src/Terrain/Biomes/photo_stats.bin.gz</c>), so peers never bake it.
///
/// <code>
/// &lt;godot&gt; --headless --path . -- --biomephotobake &lt;out .bin.gz&gt; [--bmng DIR] [--cell 0.2]
/// </code>
/// </summary>
public partial class BiomePhotoBake : Node
{
    public static bool Requested() => CmdArgs.Value("--biomephotobake") != null;

    /// <summary>A snowy month: bright (luma over this, sRGB) ...</summary>
    public const float SnowLuma = 0.62f;

    /// <summary>... and not warm: red at most this over blue (bright sand is warm, snow and ice are not).</summary>
    public const float SnowWarmth = 0.06f;

    /// <summary>
    /// Open water in a month's mean colour (sRGB): bluer than green (the bathymetry), or black (Blue Marble's inland lakes).
    /// Not <see cref="GlobeGround.Classify"/>'s "dark is water": the Amazon, the Congo and Borneo are as dark as that.
    /// </summary>
    public static bool Water(float r, float g, float b) => (b - g > 0.02f && b > r && (r + g + b) / 3 < 0.7f) || Math.Max(r, Math.Max(g, b)) < 0.05f;

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        string output = CmdArgs.Value("--biomephotobake")!, bmng = CmdArgs.Value("--bmng") ?? "D:/UnitSportSwitzerland/data/global/bmng";
        double cellDeg = double.Parse(CmdArgs.Value("--cell") ?? "0.2", CultureInfo.InvariantCulture);
        int cols = (int)Math.Round(360 / cellDeg), rows = cols / 2, n = cols * rows;
        var best = new float[n];
        Array.Fill(best, float.NegativeInfinity);
        var rgb = new byte[n * 3];
        var snow = new byte[n];
        var water = new byte[n];
        var sw = Stopwatch.StartNew();
        for (int m = 1; m <= 12; m++)
        {
            string file = System.IO.Path.Combine(bmng, $"world.topo.bathy.2004{m:D2}.3x21600x10800.jpg");
            var src = Image.LoadFromFile(file);
            if (src == null || src.IsEmpty()) { Fail($"cannot read {file}"); return; }
            if (src.GetFormat() != Image.Format.Rgb8) src.Convert(Image.Format.Rgb8);
            int w = src.GetWidth(), h = src.GetHeight();
            var px = src.GetData();
            src.Dispose();
            await Task.Run(() => Parallel.For(0, rows, y =>
            {
                int py0 = y * h / rows, py1 = Math.Max(py0 + 1, (y + 1) * h / rows);
                for (int x = 0; x < cols; x++)
                {
                    int px0 = x * w / cols, px1 = Math.Max(px0 + 1, (x + 1) * w / cols);
                    long r = 0, g = 0, b = 0;
                    for (int py = py0; py < py1; py++)
                        for (int p = px0; p < px1; p++)
                        {
                            int o = (py * w + p) * 3;
                            r += px[o];
                            g += px[o + 1];
                            b += px[o + 2];
                        }
                    float count = (py1 - py0) * (px1 - px0) * 255f;
                    float fr = r / count, fg = g / count, fb = b / count;
                    int k = y * cols + x;
                    float green = fg - (fr + fb) / 2;
                    if (green > best[k])
                    {
                        best[k] = green;
                        rgb[k * 3] = (byte)Math.Clamp(fr * 255 + 0.5f, 0, 255);
                        rgb[k * 3 + 1] = (byte)Math.Clamp(fg * 255 + 0.5f, 0, 255);
                        rgb[k * 3 + 2] = (byte)Math.Clamp(fb * 255 + 0.5f, 0, 255);
                    }
                    if ((fr + fg + fb) / 3 > SnowLuma && fr - fb < SnowWarmth) snow[k]++;
                    if (Water(fr, fg, fb)) water[k]++;
                }
            }));
            GD.Print($"[biomephotobake] month {m}, {sw.Elapsed.TotalSeconds:F0} s");
        }
        var cells = new byte[n * PhotoStats.Stride];
        int seaCells = 0;
        for (int k = 0; k < n; k++)
        {
            bool sea = water[k] >= PhotoStats.SeaMonths;
            int o = k * PhotoStats.Stride;
            if (sea)
            {
                // the sea's colour says nothing the fill reads: one value, so the grid compresses
                seaCells++;
                cells[o + 3] = (byte)(PhotoStats.SeaFlag | Math.Min((int)snow[k], 12));
                continue;
            }
            cells[o] = rgb[k * 3];
            cells[o + 1] = rgb[k * 3 + 1];
            cells[o + 2] = rgb[k * 3 + 2];
            cells[o + 3] = (byte)Math.Min((int)snow[k], 12);
        }
        var stats = new PhotoStats(cols, rows, cells);
        using (var fs = File.Create(output)) stats.Write(fs);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[biomephotobake] RESULT: ok — {cols} x {rows} cells of {cellDeg}°, {seaCells * 100.0 / n:F0} % sea, {new FileInfo(output).Length / 1e6:F2} MB, {sw.Elapsed.TotalSeconds:F0} s"));
        GetTree().Quit();
    }

    private void Fail(string why)
    {
        GD.PrintErr($"[biomephotobake] RESULT: FAILED — {why}");
        GetTree().Quit(1);
    }
}
