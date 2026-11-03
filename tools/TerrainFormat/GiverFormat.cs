using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace UnitSport.Terrain.Format;

/// <summary>
/// One errand giver the OSM and GWR stage found (#1339, errands plan part 1). <see cref="Kind"/> is the game's
/// <c>GiverKind</c> number (Farmer 3, Senn 4, Dairy 5, Postmaster 6, Host 7): never renumber. <see cref="Heading"/> is where the
/// figure faces, radians, 0 = north, clockwise. <see cref="Source"/> names where it came from (<c>osm:n123</c>, <c>gwr:egid</c>).
/// </summary>
public sealed record GiverRecord(string Id, int Kind, double E, double N, float Heading, string Canton, float Elevation, string Name, string Source);

/// <summary>
/// <c>givers.tsv.gz</c> (#1339): the farms, alp and village dairies, post offices and Beizen of Switzerland, written by
/// <c>TerrainPreprocessor --givers</c> and embedded in the game (<c>src/Adventure/Errands/givers.tsv.gz</c>), so they are on the
/// server and every client from boot and never sent. Line 1 is a <c># givers v1</c> header with the source, line 2 the column
/// names, then one row per giver sorted by id: <c>id kind e n heading canton elevation name source</c>. Tabs and line breaks in
/// names become spaces. A rerun on the same data gives the same bytes.
/// </summary>
public static class GiverFormat
{
    public const string Header = "# givers v1";
    public const string Columns = "id\tkind\te\tn\theading\tcanton\televation\tname\tsource";

    public static void Write(Stream stream, string source, IEnumerable<GiverRecord> rows)
    {
        // no file name or time in the gzip header (.NET writes none), so the same rows give the same bytes
        using var gz = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine($"{Header} {Clean(source)}");
        w.WriteLine(Columns);
        var inv = CultureInfo.InvariantCulture;
        foreach (var r in rows.OrderBy(r => r.Id, StringComparer.Ordinal))
            w.WriteLine(string.Join('\t', r.Id, r.Kind.ToString(inv), r.E.ToString("0.0", inv), r.N.ToString("0.0", inv),
                r.Heading.ToString("0.00", inv), r.Canton, r.Elevation.ToString("0", inv), Clean(r.Name), Clean(r.Source)));
    }

    public static List<GiverRecord> Read(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        var rows = new List<GiverRecord>();
        var inv = CultureInfo.InvariantCulture;
        string? line = r.ReadLine();
        if (line == null || !line.StartsWith(Header, StringComparison.Ordinal)) throw new InvalidDataException("not a givers v1 file");
        r.ReadLine();   // the column names
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var c = line.Split('\t');
            if (c.Length < 9) throw new InvalidDataException($"givers: {c.Length} columns in \"{line}\"");
            rows.Add(new GiverRecord(c[0], int.Parse(c[1], inv), double.Parse(c[2], inv), double.Parse(c[3], inv), float.Parse(c[4], inv),
                c[5], float.Parse(c[6], inv), c[7], c[8]));
        }
        return rows;
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
