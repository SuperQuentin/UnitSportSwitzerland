namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The canton territories of swissBOUNDARIES3D (<c>kantonsgebiet</c>) as outer rings, and the canton
/// at a point. Where rings overlap (an enclave drawn inside its neighbour: interior rings are not
/// read) the smaller ring wins. Shared by MapSetup's country bake (tiles) and <see cref="PoiStage"/>
/// (one canton per POI, #1224).
/// </summary>
public sealed class CantonRings
{
    /// <summary>The two-letter codes in the federal order: <c>kantonsnummer</c> 1 is ZH, 26 JU.</summary>
    public static readonly string[] Codes =
    {
        "ZH", "BE", "LU", "UR", "SZ", "OW", "NW", "GL", "ZG", "FR", "SO", "BS", "BL", "SH",
        "AR", "AI", "SG", "GR", "AG", "TG", "TI", "VD", "VS", "NE", "GE", "JU",
    };

    private readonly record struct Ring(byte Number, double[] Xy, double MinE, double MinN, double MaxE, double MaxN, double Area);

    private readonly List<Ring> _rings = new();

    /// <summary>Canton number (1-26) -> its name in the source.</summary>
    public Dictionary<int, string> Names { get; } = new();

    public int RingCount => _rings.Count;

    /// <summary>Reads the canton layer of a swissBOUNDARIES3D GeoPackage.</summary>
    public static CantonRings Read(string gpkg)
    {
        var result = new CantonRings();
        using var conn = GeoPackageReader.Open(gpkg);
        string table;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select table_name from gpkg_contents where lower(table_name) like '%kantonsgebiet%'";
            table = cmd.ExecuteScalar() as string ?? throw new InvalidDataException("no canton layer in " + gpkg);
        }
        string geom = GeoPackageReader.GeometryColumn(conn, table);
        var columns = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"pragma table_info(\"{table}\")";
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }
        string numberCol = columns.First(c => c.Equals("kantonsnummer", StringComparison.OrdinalIgnoreCase));
        string nameCol = columns.First(c => c.Equals("name", StringComparison.OrdinalIgnoreCase));

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"select \"{numberCol}\", \"{nameCol}\", \"{geom}\" from \"{table}\"";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.IsDBNull(2)) continue;
                int number = r.GetInt32(0);
                if (number < 1 || number > Codes.Length) continue;
                result.Names.TryAdd(number, r.GetString(1));
                foreach (var ring in GeoPackageReader.ParsePolygons((byte[])r[2]))
                {
                    int n = ring.Count;
                    var xy = new double[n * 2];
                    double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
                    for (int i = 0; i < n; i++)
                    {
                        double e = ring.Xyz[i * 3], nn = ring.Xyz[i * 3 + 1];
                        xy[i * 2] = e;
                        xy[i * 2 + 1] = nn;
                        minE = Math.Min(minE, e); maxE = Math.Max(maxE, e);
                        minN = Math.Min(minN, nn); maxN = Math.Max(maxN, nn);
                    }
                    result._rings.Add(new Ring((byte)number, xy, minE, minN, maxE, maxN, PoiPick.Area(xy)));
                }
            }
        }
        return result;
    }

    /// <summary>The canton number (1-26) at a point, 0 outside every canton. Thread-safe.</summary>
    public byte At(double e, double n)
    {
        byte best = 0;
        double bestArea = double.MaxValue;
        foreach (var ring in _rings)
        {
            if (e < ring.MinE || e > ring.MaxE || n < ring.MinN || n > ring.MaxN || ring.Area >= bestArea) continue;
            if (PoiPick.Inside(ring.Xy, e, n)) { best = ring.Number; bestArea = ring.Area; }
        }
        return best;
    }

    /// <summary>The two-letter code at a point, "" outside.</summary>
    public string CodeAt(double e, double n) => At(e, n) is var c and > 0 ? Codes[c - 1] : "";
}
