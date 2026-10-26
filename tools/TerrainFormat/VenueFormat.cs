using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace UnitSport.Terrain.Format;

/// <summary>
/// What an entertainment venue is (#1232), from its OpenStreetMap tag. Stored as a number in
/// <c>venues.tsv.gz</c>: never renumber, new kinds go on the end.
/// </summary>
public enum VenueType : byte
{
    None = 0,
    /// <summary><c>amenity=restaurant</c>.</summary>
    Restaurant = 1,
    /// <summary><c>amenity=cafe</c>: a café, a tea room, a Konditorei.</summary>
    Cafe = 2,
    /// <summary><c>amenity=fast_food</c>: kebab, burgers, a take-away.</summary>
    FastFood = 3,
    /// <summary><c>amenity=bar</c>.</summary>
    Bar = 4,
    /// <summary><c>amenity=pub</c>.</summary>
    Pub = 5,
    /// <summary><c>amenity=nightclub</c>.</summary>
    Nightclub = 6,
    /// <summary><c>amenity=biergarten</c>.</summary>
    Biergarten = 7,
    /// <summary><c>amenity=cinema</c>.</summary>
    Cinema = 8,
    /// <summary><c>leisure=bowling_alley</c>.</summary>
    Bowling = 9,
    /// <summary><c>leisure=amusement_arcade</c>.</summary>
    Arcade = 10,
}

/// <summary>What OSM says a venue has beyond its kind. Stored as a number: never renumber.</summary>
[Flags]
public enum VenueFlags : ushort
{
    None = 0,
    /// <summary><c>outdoor_seating</c> other than <c>no</c>: a terrace in front.</summary>
    Outdoor = 1,
    /// <summary><c>outdoor_seating=no</c>: mapped as having none.</summary>
    NoOutdoor = 2,
    /// <summary><c>sport</c> or <c>billiards</c> naming pool or snooker.</summary>
    Billiards = 4,
    /// <summary><c>sport=table_soccer</c> or a table-football tag.</summary>
    TableSoccer = 8,
    /// <summary><c>sport=darts</c> or <c>darts=yes</c>.</summary>
    Darts = 16,
    /// <summary><c>pinball</c> or <c>amusement_arcade=pinball</c>.</summary>
    Pinball = 32,
    /// <summary><c>live_music=yes</c> or a stage.</summary>
    LiveMusic = 64,
    /// <summary><c>karaoke=yes</c>.</summary>
    Karaoke = 128,
    /// <summary>A bowling centre's ninepins (<c>sport=9pin</c>, Kegelbahn) rather than ten.</summary>
    NinePin = 256,
}

/// <summary>How a row's point was placed against the built tiles when the list was written.</summary>
public enum VenueFix : byte
{
    /// <summary>No built tile there: the OSM point as it is.</summary>
    Raw = 0,
    /// <summary>The OSM point already lies on a solid's footprint.</summary>
    Inside = 1,
    /// <summary>Moved onto the nearest solid a few metres away (a point mapped on the pavement).</summary>
    Snapped = 2,
    /// <summary>Built tiles there, and no solid near: a beer garden in a park, a kiosk with no solid.</summary>
    Open = 3,
}

/// <summary>One venue: LV95 metres to the decimetre.</summary>
public sealed record VenueRecord(string Osm, VenueType Type, double E, double N, VenueFix Fix, VenueFlags Flags,
    int Count, string Cuisine, string Name);

/// <summary>
/// <c>venues.tsv.gz</c> (#1232): every venue OSM knows in Switzerland, written by
/// <c>TerrainPreprocessor --venues</c> and embedded in the game. Line 1 is a <c># venues v1</c>
/// header with the source, line 2 the column names, then one row per venue sorted by E, N and OSM id.
/// Columns: <c>osm kind e n fix flags count cuisine name</c>; <c>count</c> is OSM's lanes or screens
/// when mapped, else 0. Tabs and line breaks in names are written as spaces.
/// </summary>
public static class VenueFormat
{
    public const string Header = "# venues v1";
    public const string Columns = "osm\tkind\te\tn\tfix\tflags\tcount\tcuisine\tname";

    public static void Write(Stream stream, string source, IEnumerable<VenueRecord> rows)
    {
        // no file name or time in the gzip header (.NET writes none), so the same rows give the same bytes
        using var gz = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
        using var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine($"{Header} {Clean(source)}");
        w.WriteLine(Columns);
        var inv = CultureInfo.InvariantCulture;
        foreach (var r in rows)
            w.WriteLine(string.Join('\t', r.Osm, ((int)r.Type).ToString(inv), r.E.ToString("0.0", inv), r.N.ToString("0.0", inv),
                ((int)r.Fix).ToString(inv), ((int)r.Flags).ToString(inv), r.Count.ToString(inv), Clean(r.Cuisine), Clean(r.Name)));
    }

    public static List<VenueRecord> Read(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        var rows = new List<VenueRecord>();
        var inv = CultureInfo.InvariantCulture;
        string? line = r.ReadLine();
        if (line == null || !line.StartsWith(Header, StringComparison.Ordinal))
            throw new InvalidDataException("not a venues v1 file");
        r.ReadLine();   // the column names
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var c = line.Split('\t');
            if (c.Length < 9) throw new InvalidDataException($"venues: {c.Length} columns in \"{line}\"");
            rows.Add(new VenueRecord(c[0], (VenueType)int.Parse(c[1], inv), double.Parse(c[2], inv), double.Parse(c[3], inv),
                (VenueFix)int.Parse(c[4], inv), (VenueFlags)int.Parse(c[5], inv), int.Parse(c[6], inv), c[7], c[8]));
        }
        return rows;
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
