using System.Globalization;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// <c>/goto &lt;body&gt; &lt;lat&gt; &lt;lon&gt; [km]</c> (#1849, #1850; docs/plans/planets.md section 12): the fly camera to a place
/// on any body, the anchor relabelled to that body and the world moved there (<c>ClientWorld.GoToBody</c>). This is the
/// line's reading: the body by name (any case), planetocentric latitude and east longitude in degrees (−180..360), and
/// the height over the body's reference in km (by default 30 over a body with ground, over the cloud tops of a giant,
/// a fifth of the Sun's radius off it). Offline; online it waits for play on other bodies (C3b).
/// </summary>
public static class BodyGoto
{
    public const string Usage = "Usage: /goto <body> <lat> <lon> [km], e.g. /goto moon 26.13 3.63 (the Apollo 15 site)";

    /// <summary>The height a camera arrives at over a body with no height given, metres over its reference.</summary>
    public static double DefaultHeightM(CelestialBody body) =>
        body.Landable ? 30_000 : body.Id == BodyId.Sun ? 0.2 * body.Shape.A : body.Air != null ? 500_000 : 100_000;

    /// <summary>Reads the line's arguments; false with the reason to show.</summary>
    public static bool TryParse(string args, out CelestialBody body, out double lat, out double lon, out double heightM, out string error)
    {
        body = CelestialBody.Earth;
        lat = lon = heightM = 0;
        error = Usage;
        var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4) return false;
        CelestialBody? found = null;
        foreach (var b in CelestialBody.All)
            if (string.Equals(b.Name, parts[0], System.StringComparison.OrdinalIgnoreCase)) found = b;
        if (found == null)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var b in CelestialBody.All) names.Add(b.Name.ToLowerInvariant());
            error = $"No body called {parts[0]}: " + string.Join(", ", names);
            return false;
        }
        body = found;
        const NumberStyles F = NumberStyles.Float;
        var inv = CultureInfo.InvariantCulture;
        if (!double.TryParse(parts[1], F, inv, out lat) || !double.TryParse(parts[2], F, inv, out lon)) return false;
        if (lat is < -90 or > 90) { error = "A latitude is −90 to 90°."; return false; }
        if (lon is < -180 or > 360) { error = "A longitude is −180 to 360° east."; return false; }
        if (lon > 180) lon -= 360;
        if (parts.Length == 4)
        {
            if (!double.TryParse(parts[3], F, inv, out double km)) return false;
            if (km is < 0 or > 1_000_000) { error = "A height is 0 to 1,000,000 km."; return false; }
            heightM = km * 1000;
        }
        else heightM = DefaultHeightM(body);
        // never under a body's floor (the giants' deep cloud, the Sun's glare)
        if (!body.Landable) heightM = System.Math.Max(heightM, body.FloorM);
        error = "";
        return true;
    }
}
