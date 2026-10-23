using System.Globalization;

namespace UnitSport.Terrain.Format;

/// <summary>Who an endpoint makes and takes (#1134): cars, riders, walkers or trams.</summary>
public enum EndpointMode : byte { Car = 0, Bike = 1, Walker = 2, Tram = 3 }

/// <summary>
/// A traffic endpoint a world comes with (#1134, the test region's <see cref="FileName"/> beside its tiles): as the game's
/// <c>TrafficEndpoint</c> (#971), a point (LV95) with a heading in (radians, east 0, north π/2), making <see cref="PerMinute"/>
/// movers of its <see cref="Mode"/> and/or taking those leaving past it, its movers shared out to destinations by weight; and
/// which junction and arm it serves (the probe's report).
/// </summary>
public sealed record RegionEndpoint(int Id, EndpointMode Mode, double E, double N, float Heading, bool Generates, bool Destination,
    float PerMinute, (int To, float Weight)[] Shares, string Junction, string Arm);

/// <summary>The world's endpoints file (#1134): one tab-separated row per endpoint, invariant culture.</summary>
public static class TrafficEndpointsFile
{
    public const string FileName = "traffic_endpoints.tsv";
    private const string Header = "id\tmode\te\tn\theading\tgenerates\tdestination\tper_minute\tshares\tjunction\tarm";

    public static void Write(string path, IEnumerable<RegionEndpoint> endpoints)
    {
        var inv = CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path) { NewLine = "\n" };
        w.WriteLine(Header);
        foreach (var x in endpoints)
            w.WriteLine(string.Join('\t', x.Id.ToString(inv), x.Mode.ToString().ToLowerInvariant(), x.E.ToString("F2", inv), x.N.ToString("F2", inv),
                x.Heading.ToString("F4", inv), x.Generates ? "1" : "0", x.Destination ? "1" : "0", x.PerMinute.ToString("0.###", inv),
                x.Shares.Length == 0 ? "-" : string.Join(',', x.Shares.Select(s => $"{s.To.ToString(inv)}:{s.Weight.ToString("0.###", inv)}")),
                x.Junction, x.Arm));
    }

    /// <summary>The file's endpoints; none where it is missing. A row that does not parse is skipped.</summary>
    public static List<RegionEndpoint> Read(string path)
    {
        var list = new List<RegionEndpoint>();
        if (!File.Exists(path)) return list;
        var inv = CultureInfo.InvariantCulture;
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 11 || !int.TryParse(f[0], NumberStyles.Integer, inv, out int id)
                || !Enum.TryParse(f[1], ignoreCase: true, out EndpointMode mode)
                || !double.TryParse(f[2], NumberStyles.Float, inv, out double e) || !double.TryParse(f[3], NumberStyles.Float, inv, out double n)
                || !float.TryParse(f[4], NumberStyles.Float, inv, out float heading) || !float.TryParse(f[7], NumberStyles.Float, inv, out float rate))
                continue;
            var shares = f[8] == "-" ? [] : f[8].Split(',').Select(s => s.Split(':'))
                .Where(p => p.Length == 2 && int.TryParse(p[0], NumberStyles.Integer, inv, out _) && float.TryParse(p[1], NumberStyles.Float, inv, out _))
                .Select(p => (int.Parse(p[0], inv), float.Parse(p[1], inv))).ToArray();
            list.Add(new RegionEndpoint(id, mode, e, n, heading, f[5] == "1", f[6] == "1", rate, shares, f[9], f[10]));
        }
        return list;
    }
}
