namespace UnitSport.Tools.RoadGen.Layout;

using System.Globalization;
using UnitSport.Terrain.Format;

/// <summary>A Geneva lane sign (#1110, SITG <c>OTC_SV_SIGNAL</c>, SSV 4.41 main roads / 4.42 minor): its point, gantry, number, the way its readers drive (deg), its text.</summary>
public sealed record SitgSign(double E, double N, string Support, string Osr, double Orientation, string Text)
{
    public bool Motorway => SignText.Parse(Text).Motorway;
}

/// <summary>A Geneva gantry of lane signs: its signs' middle, the way their readers drive, its signs.</summary>
public sealed record SitgGantry(string Support, double E, double N, double Bearing, List<SitgSign> Signs);

/// <summary>
/// Canton Geneva's lane signs over the carriageway (#1110, <c>docs/notes/tools/direction-signs.md</c>), read from the
/// preprocessor's <c>sitg_signs.tsv</c> (<c>--sitg-signs</c>): the gantries (signs grouped by their support) and which one stands
/// over a lit approach. SITG's own head layer marks only 48 heads as on a gantry ("portique", the photos' Route de Lausanne
/// gantry is not among them): a gantry of lane signs at the lights is taken as one with heads in its band, as Geneva builds them
/// (inferred from the user's photos). Pure once read.
/// </summary>
public sealed class SitgGantries
{
    /// <summary>A gantry serves an approach whose stop line is at most this far past it, along the way (m), or <see cref="Behind"/> before it.</summary>
    public const double Upstream = 60, Behind = 12;
    /// <summary>... at most this far to the side of the line's middle (m), its readers driving within this of the approach's way (deg).</summary>
    public const double Side = 15, Turn = 35;

    public List<SitgGantry> Gantries { get; } = new();

    /// <summary>The lane signs of <paramref name="path"/> grouped by support; null without the file.</summary>
    public static SitgGantries? Load(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        var signs = new List<SitgSign>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.StartsWith("kind\t", StringComparison.Ordinal)) continue;
            var f = line.Split('\t');
            if (f.Length < 12 || f[0] != "sign" || f[5] is not ("4.41" or "4.42")) continue;
            if (!double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double orientation)) continue;
            signs.Add(new SitgSign(double.Parse(f[1], CultureInfo.InvariantCulture), double.Parse(f[2], CultureInfo.InvariantCulture),
                f[4], f[5], orientation, f[11]));
        }
        var result = new SitgGantries();
        foreach (var g in signs.GroupBy(s => (s.Support, Math.Round(s.Orientation / 30))).OrderBy(g => g.Key.Support, StringComparer.Ordinal))
        {
            var list = g.ToList();
            result.Gantries.Add(new SitgGantry(g.Key.Support, list.Average(s => s.E), list.Average(s => s.N), list[0].Orientation, list));
        }
        return result;
    }

    /// <summary>Angle between two bearings (deg, 0-180).</summary>
    public static double TurnBetween(double a, double b) { double d = Math.Abs(((a - b) % 360 + 540) % 360 - 180); return d; }

    /// <summary>
    /// The gantry over an approach whose stop line's middle is (<paramref name="e"/>, <paramref name="n"/>), its traffic driving
    /// <paramref name="bearing"/>: within <see cref="Upstream"/> m before the line (or <see cref="Behind"/> past it),
    /// <see cref="Side"/> m to the side, facing the same way; the nearest along the way. Null where none.
    /// </summary>
    public SitgGantry? At(double e, double n, double bearing)
    {
        double rad = bearing * Math.PI / 180, de = Math.Sin(rad), dn = Math.Cos(rad);
        SitgGantry? best = null;
        double bestAlong = double.MaxValue;
        foreach (var g in Gantries)
        {
            if (TurnBetween(g.Bearing, bearing) > Turn) continue;
            double along = (g.E - e) * de + (g.N - n) * dn, side = (g.E - e) * dn - (g.N - n) * de;
            if (along < -Upstream || along > Behind || Math.Abs(side) > Side) continue;
            if (Math.Abs(along) < bestAlong) (best, bestAlong) = (g, Math.Abs(along));
        }
        return best;
    }

    /// <summary>
    /// A gantry's signs left to right as its readers see them (the points SITG draws them at stand side by side in that order),
    /// lanes signed alike one sign (Geneva lists a sign per lane: "Lausanne Versoix" twice over two lanes).
    /// </summary>
    public static List<SitgSign> LeftToRight(SitgGantry g)
    {
        double rad = g.Bearing * Math.PI / 180, re = Math.Cos(rad), rn = -Math.Sin(rad);
        var sorted = g.Signs.OrderBy(s => s.E * re + s.N * rn).ToList();
        var merged = new List<SitgSign>();
        foreach (var s in sorted)
            if (merged.Count == 0 || merged[^1].Text != s.Text) merged.Add(s);
        return merged;
    }

    /// <summary>
    /// Which sign a run of lanes takes (#1110), left to right: one each where the counts agree, else the sign nearest the run's
    /// share of the width (a run left without one of its own takes the nearest).
    /// </summary>
    public static int[] Assign(int runs, int signs)
    {
        var at = new int[runs];
        for (int i = 0; i < runs; i++)
            at[i] = runs == signs ? i : signs == 1 ? 0 : (int)Math.Round((runs == 1 ? 0.5 : i / (double)(runs - 1)) * (signs - 1));
        return at;
    }
}
