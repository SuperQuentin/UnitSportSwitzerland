using System.IO.Compression;
using System.Xml;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Streams the farm land-use objects (<c>LNF_Nutzung</c>, MGDM 153.1 "LWB Nutzungsflächen") out of a
/// geodienste.ch INTERLIS 2.3 XTF (#494), straight from the canton's zip: no extraction, no GDAL.
/// A canton is 100-650 MB of XML, so it is read with <see cref="XmlReader"/> one object at a time.
/// Model v3.0 and v2.0 look the same where it matters: the <c>Nutzungsart</c> reference is the LNF
/// code (<c>&lt;Reference REF="611"/&gt;</c>), geometry is a MultiPolygon of surfaces with straight
/// segments only (<c>SURFACE/BOUNDARY/POLYLINE/COORD/C1,C2</c>, LV95).
/// </summary>
public static class LwbReader
{
    /// <summary>One land-use object: its identifier, LNF code and polygon parts (rings of interleaved E,N; first ring outer).</summary>
    public sealed record Item(string Identifier, int Code, List<List<double[]>> Polygons);

    /// <summary>The canton's <c>*_b_153_1.xtf</c> (land use, not <c>a_153_6</c> summering areas) inside a geodienste.ch zip.</summary>
    public static ZipArchiveEntry? FindEntry(ZipArchive zip) =>
        zip.Entries.FirstOrDefault(e => e.Name.EndsWith("_b_153_1.xtf", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads every object of a zip (or of a bare .xtf file); returns how many.</summary>
    public static int ReadZip(string path, Action<Item> each)
    {
        if (path.EndsWith(".xtf", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(path);
            return Read(fs, each);
        }
        using var zip = ZipFile.OpenRead(path);
        var entry = FindEntry(zip) ?? throw new InvalidDataException($"{path}: no *_b_153_1.xtf inside");
        using var s = entry.Open();
        return Read(s, each);
    }

    public static int Read(Stream xtf, Action<Item> each)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true,
            CloseInput = false, XmlResolver = null,
        };
        using var r = XmlReader.Create(xtf, settings);
        int count = 0;
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || !r.Name.EndsWith(".LNF_Nutzung", StringComparison.Ordinal)) continue;
            using var sub = r.ReadSubtree();
            var item = ReadObject(sub);
            if (item != null) { each(item); count++; }
        }
        return count;
    }

    private static Item? ReadObject(XmlReader r)
    {
        string id = "";
        int code = -1;
        bool inArt = false;
        var polygons = new List<List<double[]>>();
        List<double[]>? rings = null;
        var ring = new List<double>();
        double e = 0;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                string name = r.Name;
                if (name == "Identifikator") id = Text(r);
                else if (name == "Nutzungsart") inArt = true;
                else if (name == "Reference" && inArt && code < 0) { int.TryParse(r.GetAttribute("REF"), out code); }
                else if (name.EndsWith(".PolygonStructure", StringComparison.Ordinal)) rings = [];
                else if (name == "BOUNDARY") ring.Clear();
                else if (name == "C1") e = ParseD(Text(r));
                else if (name == "C2") { double n = ParseD(Text(r)); ring.Add(e); ring.Add(n); }
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                string name = r.Name;
                if (name == "Nutzungsart") inArt = false;
                else if (name == "BOUNDARY") { if (rings != null && ring.Count >= 6) rings.Add(Open(ring)); ring.Clear(); }
                else if (name.EndsWith(".PolygonStructure", StringComparison.Ordinal))
                {
                    if (rings is { Count: > 0 }) polygons.Add(rings);
                    rings = null;
                }
            }
        }
        return code < 0 || polygons.Count == 0 ? null : new Item(id, code, polygons);
    }

    /// <summary>The ring without its closing duplicate vertex.</summary>
    private static double[] Open(List<double> ring)
    {
        int n = ring.Count;
        if (n >= 8 && ring[0] == ring[n - 2] && ring[1] == ring[n - 1]) n -= 2;
        return ring.GetRange(0, n).ToArray();
    }

    /// <summary>The text of a leaf element, leaving the reader on that text (its end tag is next).</summary>
    private static string Text(XmlReader r)
    {
        if (r.IsEmptyElement) return "";
        r.Read();
        return r.NodeType == XmlNodeType.Text ? r.Value : "";
    }

    private static double ParseD(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
}
