using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// GDTF, the General Device Type Format (DIN SPEC 15800), for our fixture types (#941): a zip holding
/// <c>description.xml</c> with the attributes, the geometries (base, yoke, head, beams or cells) and
/// one DMX mode laying out every channel, 16-bit pan, tilt and dimmer as coarse and fine. Written from
/// a <see cref="FixtureType"/>; read back from ours or anyone's, a mode's channels mapped onto our
/// attributes and a kind told from what the head can do. Pure.
/// </summary>
public static class Gdtf
{
    public const string ModeName = "Default";

    /// <summary>Our attribute's GDTF name, and the feature it belongs to (Feature group.Feature).</summary>
    private static (string Name, string Feature) Name(Attr a) => a switch
    {
        Attr.Pan => ("Pan", "Position.PanTilt"),
        Attr.Tilt => ("Tilt", "Position.PanTilt"),
        Attr.PanTiltSpeed => ("PositionMSpeed", "Control.Control"),
        Attr.Dimmer => ("Dimmer", "Dimmer.Dimmer"),
        Attr.Shutter => ("Shutter1", "Beam.Beam"),
        Attr.Red => ("ColorAdd_R", "Color.RGB"),
        Attr.Green => ("ColorAdd_G", "Color.RGB"),
        Attr.Blue => ("ColorAdd_B", "Color.RGB"),
        Attr.White => ("ColorAdd_W", "Color.RGB"),
        Attr.ColorWheel => ("Color1", "Color.Color"),
        Attr.Cyan => ("ColorSub_C", "Color.RGB"),
        Attr.Magenta => ("ColorSub_M", "Color.RGB"),
        Attr.Yellow => ("ColorSub_Y", "Color.RGB"),
        Attr.Cto => ("CTO", "Color.Color"),
        Attr.Gobo => ("Gobo1", "Gobo.Gobo"),
        Attr.GoboRotation => ("Gobo1PosRotate", "Gobo.Gobo"),
        Attr.Prism => ("Prism1", "Beam.Beam"),
        Attr.Frost => ("Frost1", "Beam.Beam"),
        Attr.Focus => ("Focus1", "Focus.Focus"),
        Attr.Zoom => ("Zoom", "Focus.Focus"),
        Attr.Iris => ("Iris", "Beam.Beam"),
        Attr.Control => ("Control1", "Control.Control"),
        Attr.Duration => ("StrobeDuration", "Beam.Beam"),
        Attr.Rate => ("StrobeRate", "Beam.Beam"),
        Attr.Effect => ("Effects1", "Beam.Beam"),
        Attr.Clip => ("MediaContent1", "Video.Video"),
        Attr.ClipSpeed => ("PlayMode", "Video.Video"),
        Attr.Red2 => ("VideoEffect1Parameter1", "Video.Video"),
        Attr.Green2 => ("VideoEffect1Parameter2", "Video.Video"),
        Attr.Blue2 => ("VideoEffect1Parameter3", "Video.Video"),
        Attr.Pump => ("Fog1", "Fog.Fog"),
        Attr.Fan => ("FogFan1", "Fog.Fog"),
        Attr.Arm => ("SFXArm1", "Control.Control"),
        Attr.Fire => ("SFXFire1", "Control.Control"),
        _ => ("NoFeature", "Control.Control"),
    };

    /// <summary>A GDTF attribute's name (anyone's) to ours; Other for what the game does not drive.</summary>
    public static Attr Parse(string gdtf)
    {
        string n = gdtf.Trim();
        switch (n)
        {
            case "Pan": return Attr.Pan;
            case "Tilt": return Attr.Tilt;
            case "PositionMSpeed": case "PanTiltSpeed": case "PanMSpeed": return Attr.PanTiltSpeed;
            case "Dimmer": return Attr.Dimmer;
            case "ColorAdd_R": case "ColorRGB_Red": return Attr.Red;
            case "ColorAdd_G": case "ColorRGB_Green": return Attr.Green;
            case "ColorAdd_B": case "ColorRGB_Blue": return Attr.Blue;
            case "ColorAdd_W": case "ColorAdd_WW": case "ColorAdd_CW": return Attr.White;
            case "ColorSub_C": return Attr.Cyan;
            case "ColorSub_M": return Attr.Magenta;
            case "ColorSub_Y": return Attr.Yellow;
            case "CTO": case "CTC": case "CTB": return Attr.Cto;
            case "Zoom": return Attr.Zoom;
            case "Iris": return Attr.Iris;
            case "StrobeDuration": return Attr.Duration;
            case "StrobeRate": case "StrobeFrequency": return Attr.Rate;
            case "MediaContent1": return Attr.Clip;
            case "PlayMode": return Attr.ClipSpeed;
            case "VideoEffect1Parameter1": return Attr.Red2;
            case "VideoEffect1Parameter2": return Attr.Green2;
            case "VideoEffect1Parameter3": return Attr.Blue2;
            case "SFXArm1": return Attr.Arm;
            case "SFXFire1": return Attr.Fire;
        }
        if (n.StartsWith("Shutter", StringComparison.Ordinal) && !n.Contains("Strobe")) return Attr.Shutter;
        if (n is "Color1" || n.StartsWith("Color1", StringComparison.Ordinal) && !n.Contains("Pos") && !n.Contains("Spin")) return Attr.ColorWheel;
        if (n.StartsWith("Gobo", StringComparison.Ordinal))
            return n.Contains("Pos") || n.Contains("Rotat") || n.Contains("Spin") ? Attr.GoboRotation : n.Length <= 5 ? Attr.Gobo : Attr.Other;
        if (n.StartsWith("Prism", StringComparison.Ordinal)) return n.Length <= 6 ? Attr.Prism : Attr.Other;
        if (n.StartsWith("Frost", StringComparison.Ordinal)) return Attr.Frost;
        if (n.StartsWith("Focus", StringComparison.Ordinal)) return Attr.Focus;
        if (n.StartsWith("Control", StringComparison.Ordinal)) return Attr.Control;
        if (n.StartsWith("Effects", StringComparison.Ordinal) && n.Length <= 8) return Attr.Effect;
        if (n.StartsWith("Fog", StringComparison.Ordinal) || n.StartsWith("Haze", StringComparison.Ordinal))
            return n.Contains("Fan") ? Attr.Fan : Attr.Pump;
        if (n.StartsWith("Fan", StringComparison.Ordinal)) return Attr.Fan;
        return Attr.Other;
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static readonly XNamespace None = XNamespace.None;
    private const string Identity = "{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}";
    private static string Offset(float z) => $"{{1,0,0,0}}{{0,1,0,0}}{{0,0,1,{F(z)}}}{{0,0,0,1}}";

    /// <summary>A file name for a type, as an MVR's GDTFSpec names it.</summary>
    public static string FileName(FixtureType t) => "UnitSport@" + string.Concat(t.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".gdtf";

    /// <summary>A stable id from the type's name (a GDTF wants a UUID; the same type, the same id).</summary>
    public static string Uuid(string seed)
    {
        var h = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(seed));
        h[6] = (byte)(h[6] & 0x0f | 0x30);
        h[8] = (byte)(h[8] & 0x3f | 0x80);
        return new Guid(h).ToString().ToUpperInvariant();
    }

    /// <summary>The GDTF of one of our types: a zip holding its description.xml.</summary>
    public static byte[] Write(FixtureType t)
    {
        var doc = Describe(t);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("description.xml", CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            doc.Save(w);
        }
        return ms.ToArray();
    }

    /// <summary>The geometry a channel acts on: the yoke pans, the head tilts, a cell lights, the rest is the beam (or the body).</summary>
    private static string GeometryOf(FixtureType t, Attr a, int cell)
    {
        bool moving = t.PanRange > 0 || t.TiltRange > 0;
        if (a == Attr.Cell) return $"Cell{cell + 1}";
        if (moving && a is Attr.Pan or Attr.PanFine) return "Yoke";
        if (moving && a is Attr.Tilt or Attr.TiltFine) return "Head";
        return HasBeam(t) ? "Beam" : "Base";
    }

    private static bool HasBeam(FixtureType t) => t.Kind is FixtureKind.Spot or FixtureKind.Beam or FixtureKind.Wash or FixtureKind.Strobe;

    public static XDocument Describe(FixtureType t)
    {
        bool moving = t.PanRange > 0 || t.TiltRange > 0;
        // the attributes this type uses, each once (a fine channel is its coarse's second byte)
        var used = t.Channels.Where(c => c is not (Attr.PanFine or Attr.TiltFine or Attr.DimmerFine or Attr.Other))
            .Select(c => c == Attr.Cell ? (t.Kind == FixtureKind.PixelBar ? Attr.Red : Attr.Dimmer) : c).Distinct().ToList();
        if (t.Kind == FixtureKind.PixelBar) { used.Remove(Attr.Red); used.AddRange(new[] { Attr.Red, Attr.Green, Attr.Blue }); used = used.Distinct().ToList(); }
        var features = used.Select(a => Name(a).Feature).Distinct().ToList();

        var attrDefs = new XElement("AttributeDefinitions",
            new XElement("ActivationGroups", new XElement("ActivationGroup", new XAttribute("Name", "PanTilt")), new XElement("ActivationGroup", new XAttribute("Name", "ColorRGB"))),
            new XElement("FeatureGroups", features.GroupBy(f => f.Split('.')[0]).Select(g =>
                new XElement("FeatureGroup", new XAttribute("Name", g.Key), new XAttribute("Pretty", g.Key),
                    g.Select(f => new XElement("Feature", new XAttribute("Name", f.Split('.')[1])))))),
            new XElement("Attributes", used.Select(a =>
            {
                var (name, feature) = Name(a);
                var x = new XElement("Attribute", new XAttribute("Name", name), new XAttribute("Pretty", name), new XAttribute("Feature", feature));
                if (a is Attr.Pan or Attr.Tilt) { x.Add(new XAttribute("ActivationGroup", "PanTilt"), new XAttribute("PhysicalUnit", "Angle")); }
                if (a is Attr.Red or Attr.Green or Attr.Blue or Attr.White) x.Add(new XAttribute("ActivationGroup", "ColorRGB"));
                if (a == Attr.Zoom) x.Add(new XAttribute("PhysicalUnit", "Angle"));
                return x;
            })));

        // models and geometries: a moving head's base, yoke, head and beam; a bar's or a blinder's cells
        var models = new XElement("Models",
            Model("Base", 0.4f, 0.4f, 0.2f, moving ? "Base" : "Cube"));
        XElement beam = new("Beam", new XAttribute("Name", "Beam"), new XAttribute("Model", moving ? "Head" : "Base"), new XAttribute("Position", Offset(-0.1f)),
            new XAttribute("LampType", "LED"), new XAttribute("PowerConsumption", "600"), new XAttribute("LuminousFlux", "20000"),
            new XAttribute("ColorTemperature", "6500"), new XAttribute("BeamAngle", F((t.BeamMin + t.BeamMax) / 2)),
            new XAttribute("FieldAngle", F(t.BeamMax)), new XAttribute("BeamRadius", "0.06"),
            new XAttribute("BeamType", t.Kind switch { FixtureKind.Beam => "Beam", FixtureKind.Spot => "Spot", _ => "Wash" }));
        XElement root;
        if (moving)
        {
            models.Add(Model("Yoke", 0.35f, 0.1f, 0.35f, "Yoke"), Model("Head", 0.3f, 0.3f, 0.4f, "Head"));
            root = new XElement("Geometry", new XAttribute("Name", "Base"), new XAttribute("Model", "Base"), new XAttribute("Position", Identity),
                new XElement("Axis", new XAttribute("Name", "Yoke"), new XAttribute("Model", "Yoke"), new XAttribute("Position", Offset(-0.15f)),
                    new XElement("Axis", new XAttribute("Name", "Head"), new XAttribute("Model", "Head"), new XAttribute("Position", Offset(-0.2f)), beam)));
        }
        else
        {
            root = new XElement("Geometry", new XAttribute("Name", "Base"), new XAttribute("Model", "Base"), new XAttribute("Position", Identity));
            int cells = t.Kind is FixtureKind.PixelBar or FixtureKind.Blinder ? t.Cells : 0;
            if (cells > 0)
            {
                models.Add(Model("Cell", 0.1f, 0.1f, 0.05f, "Cube"));
                for (int c = 0; c < cells; c++)
                    root.Add(new XElement("Beam", new XAttribute("Name", $"Cell{c + 1}"), new XAttribute("Model", "Cell"),
                        new XAttribute("Position", $"{{1,0,0,{F((c - (cells - 1) / 2f) * 0.25f)}}}{{0,1,0,0}}{{0,0,1,-0.05}}{{0,0,0,1}}"),
                        new XAttribute("LampType", t.Kind == FixtureKind.Blinder ? "Tungsten" : "LED"), new XAttribute("BeamAngle", F(t.BeamMax)),
                        new XAttribute("FieldAngle", F(t.BeamMax)), new XAttribute("BeamRadius", "0.05"), new XAttribute("BeamType", "Wash")));
            }
            else if (HasBeam(t)) root.Add(beam);
        }

        // the mode: one DMXChannel per coarse channel, a fine one folded into its offset list
        var channels = new XElement("DMXChannels");
        for (int i = 0, cell = 0; i < t.Channels.Length; i++)
        {
            var a = t.Channels[i];
            if (a is Attr.PanFine or Attr.TiltFine or Attr.DimmerFine) continue;
            Attr fine = a switch { Attr.Pan => Attr.PanFine, Attr.Tilt => Attr.TiltFine, Attr.Dimmer => Attr.DimmerFine, _ => Attr.Other };
            int fineAt = fine != Attr.Other ? t.Offset(fine) : -1;
            string offset = fineAt >= 0 ? $"{i + 1},{fineAt + 1}" : $"{i + 1}";
            int bytes = fineAt >= 0 ? 2 : 1;
            Attr named = a == Attr.Cell
                ? t.Kind == FixtureKind.PixelBar ? (cell % 3) switch { 0 => Attr.Red, 1 => Attr.Green, _ => Attr.Blue } : Attr.Dimmer
                : a;
            string geometry = GeometryOf(t, a, t.Kind == FixtureKind.PixelBar ? cell / 3 : cell);
            if (a == Attr.Cell) cell++;
            string name = a == Attr.Other ? "NoFeature" : Name(named).Name;
            // ours pans the other way from GDTF's right-handed Z rotation: the range is written reversed
            (float from, float to) = named switch
            {
                Attr.Pan => (t.PanRange / 2, -t.PanRange / 2),
                Attr.Tilt => (-t.TiltRange / 2, t.TiltRange / 2),
                Attr.Zoom => (t.BeamMin, t.BeamMax),
                _ => (0f, 1f),
            };
            string def = named is Attr.Pan or Attr.Tilt ? $"{(bytes == 2 ? 32768 : 128)}/{bytes}" : $"0/{bytes}";
            channels.Add(new XElement("DMXChannel", new XAttribute("DMXBreak", "1"), new XAttribute("Offset", offset),
                new XAttribute("Highlight", "None"), new XAttribute("Geometry", geometry),
                new XElement("LogicalChannel", new XAttribute("Attribute", name), new XAttribute("Snap", "No"), new XAttribute("Master", "None"),
                    new XAttribute("MibFade", "0"), new XAttribute("DMXChangeTimeLimit", "0"),
                    new XElement("ChannelFunction", new XAttribute("Name", $"{name} 1"), new XAttribute("Attribute", name), new XAttribute("OriginalAttribute", ""),
                        new XAttribute("DMXFrom", $"0/{bytes}"), new XAttribute("Default", def),
                        new XAttribute("PhysicalFrom", F(from)), new XAttribute("PhysicalTo", F(to)),
                        new XAttribute("RealFade", "0"), new XAttribute("RealAcceleration", "0")))));
        }

        var fixture = new XElement("FixtureType",
            new XAttribute("Name", t.Name), new XAttribute("ShortName", t.Name), new XAttribute("LongName", t.Name),
            new XAttribute("Manufacturer", "UnitSport"), new XAttribute("Description", Signature(t)),
            new XAttribute("FixtureTypeID", Uuid("UnitSport/" + t.Name)), new XAttribute("Thumbnail", ""), new XAttribute("RefFT", ""),
            new XAttribute("CanHaveChildren", "Yes"),
            attrDefs,
            new XElement("Wheels"),
            new XElement("PhysicalDescriptions"),
            models,
            new XElement("Geometries", root),
            new XElement("DMXModes", new XElement("DMXMode", new XAttribute("Name", ModeName), new XAttribute("Geometry", "Base"),
                channels, new XElement("Relations"), new XElement("FTMacros"))),
            new XElement("Revisions", new XElement("Revision", new XAttribute("Text", "Exported by UnitSport Switzerland"), new XAttribute("UserID", "0"))),
            new XElement("FTPresets"),
            new XElement("Protocols"));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "no"), new XElement("GDTF", new XAttribute("DataVersion", "1.2"), fixture));
    }

    /// <summary>
    /// What a GDTF's channels cannot say for ours, in its description: the kind (five effects share
    /// arm and fire), the ranges, exactly. Read back by <see cref="ToFixtureType"/> for UnitSport files.
    /// </summary>
    private static string Signature(FixtureType t) => FormattableString.Invariant(
        $"UnitSport kind={t.Kind} pan={t.PanRange} tilt={t.TiltRange} beam={t.BeamMin}..{t.BeamMax}; {t.Footprint} channels, UnitSport Switzerland concert rig");

    private static bool FromSignature(string description, out FixtureKind kind, out float pan, out float tilt, out float b0, out float b1)
    {
        kind = default; pan = tilt = b0 = b1 = 0f;
        var m = System.Text.RegularExpressions.Regex.Match(description,
            @"^UnitSport kind=(\w+) pan=([\d.]+) tilt=([\d.]+) beam=([\d.]+)\.\.([\d.]+)");
        return m.Success && Enum.TryParse(m.Groups[1].Value, out kind)
            && float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pan)
            && float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tilt)
            && float.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out b0)
            && float.TryParse(m.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out b1);
    }

    private static XElement Model(string name, float l, float w, float h, string primitive) =>
        new("Model", new XAttribute("Name", name), new XAttribute("Length", F(l)), new XAttribute("Width", F(w)),
            new XAttribute("Height", F(h)), new XAttribute("PrimitiveType", primitive));

    // ---- reading ------------------------------------------------------------------------------------

    /// <summary>
    /// One DMX channel of a mode as read: its first byte (0-based, from its break's address), how many
    /// bytes, its attribute, its geometry, its physical range, its DMX break (1 the fixture's first
    /// address) and its last byte (1-based).
    /// </summary>
    public sealed record Channel(int Offset, int Bytes, string Attribute, string Geometry, float PhysicalFrom, float PhysicalTo,
        int Break = 1, int Last = 0);

    /// <summary>
    /// A GDTF as read: its names, a mode's channels (every break's), the first beam's angle, the first
    /// break's footprint and the breaks the mode uses (null: the first only).
    /// </summary>
    public sealed record Type(string Name, string Manufacturer, string Mode, List<Channel> Channels, float BeamAngle, int Footprint, string[] Modes,
        string Description = "", int[]? Breaks = null);

    /// <summary>Reads a .gdtf (a zip) and one of its modes (the first when not named or not found); null with why when it is not one.</summary>
    public static Type? Read(byte[] gdtf, string? mode, out string? error)
    {
        error = null;
        try
        {
            using var ms = new MemoryStream(gdtf);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("description.xml", StringComparison.OrdinalIgnoreCase));
            if (entry == null) { error = "no description.xml"; return null; }
            if (entry.Length > 32 << 20) { error = "description.xml over 32 MB"; return null; }
            using var s = entry.Open();
            return FromXml(XDocument.Load(s), mode, out error);
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            error = e.Message;
            return null;
        }
    }

    public static Type? FromXml(XDocument doc, string? mode, out string? error)
    {
        error = null;
        var ft = doc.Root?.Element("FixtureType");
        if (ft == null) { error = "no FixtureType"; return null; }
        var modes = ft.Element("DMXModes")?.Elements("DMXMode").ToList() ?? new();
        if (modes.Count == 0) { error = "no DMX mode"; return null; }
        var m = modes.FirstOrDefault(x => (string?)x.Attribute("Name") == mode) ?? modes[0];
        var channels = new List<Channel>();
        // a geometry reference repeats a template geometry's channels at its own offsets (a pixel bar's
        // cells, a ring's segments): each reference's Break says where, for the template's break or for
        // the channels marked "Overwrite" (its last Break)
        var refs = new Dictionary<string, List<XElement>>();
        foreach (var r in ft.Element("Geometries")?.Descendants("GeometryReference") ?? Enumerable.Empty<XElement>())
        {
            string g = (string?)r.Attribute("Geometry") ?? "";
            if (!refs.TryGetValue(g, out var list)) refs[g] = list = new List<XElement>();
            list.Add(r);
        }
        foreach (var ch in m.Element("DMXChannels")?.Elements("DMXChannel") ?? Enumerable.Empty<XElement>())
        {
            string brText = (string?)ch.Attribute("DMXBreak") ?? "1";
            bool overwrite = brText == "Overwrite";
            int br = overwrite ? 1 : Math.Max(1, Int(brText, 1));
            string offs = (string?)ch.Attribute("Offset") ?? "";
            var parts = offs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;   // a virtual channel
            var bytes = new int[parts.Length];
            bool ok = true;
            for (int k = 0; k < parts.Length; k++)
                ok &= int.TryParse(parts[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes[k]) && bytes[k] >= 1;
            if (!ok) continue;
            var lc = ch.Element("LogicalChannel");
            var cf = lc?.Element("ChannelFunction");
            string attr = (string?)lc?.Attribute("Attribute") ?? (string?)cf?.Attribute("Attribute") ?? "NoFeature";
            float pf = Float((string?)cf?.Attribute("PhysicalFrom"), 0f), pt = Float((string?)cf?.Attribute("PhysicalTo"), 1f);
            // a channel's other functions widen its physical range (a zoom's, a pan's)
            if (lc != null)
                foreach (var f in lc.Elements("ChannelFunction").Skip(1))
                    if ((string?)f.Attribute("Attribute") == attr)
                    {
                        float a = Float((string?)f.Attribute("PhysicalFrom"), pf), b = Float((string?)f.Attribute("PhysicalTo"), pt);
                        if (pf <= pt) { pf = Math.Min(pf, Math.Min(a, b)); pt = Math.Max(pt, Math.Max(a, b)); }
                    }
            string geometry = (string?)ch.Attribute("Geometry") ?? "";
            if (refs.TryGetValue(geometry, out var instances))
                foreach (var r in instances)
                {
                    var breaks = r.Elements("Break").ToList();
                    var b = overwrite ? breaks.LastOrDefault() : breaks.FirstOrDefault(x => Int((string?)x.Attribute("DMXBreak"), 1) == br);
                    int rb = b != null ? Math.Max(1, Int((string?)b.Attribute("DMXBreak"), 1)) : br;
                    int shift = (b != null ? Math.Max(1, Int((string?)b.Attribute("DMXOffset"), 1)) : 1) - 1;
                    channels.Add(new Channel(bytes[0] - 1 + shift, bytes.Length, attr, (string?)r.Attribute("Name") ?? geometry, pf, pt, rb, bytes.Max() + shift));
                }
            else channels.Add(new Channel(bytes[0] - 1, bytes.Length, attr, geometry, pf, pt, br, bytes.Max()));
        }
        var breakList = channels.Select(c => c.Break).Distinct().OrderBy(b => b).ToArray();
        int footprint = channels.Where(c => c.Break == 1).Select(c => c.Last).DefaultIfEmpty(0).Max();
        float beamAngle = ft.Descendants("Beam").Select(b => Float((string?)b.Attribute("BeamAngle"), 0f)).FirstOrDefault(a => a > 0f);
        return new Type((string?)ft.Attribute("Name") ?? "Fixture", (string?)ft.Attribute("Manufacturer") ?? "", (string?)m.Attribute("Name") ?? "",
            channels, beamAngle, footprint, modes.Select(x => (string?)x.Attribute("Name") ?? "").ToArray(),
            (string?)ft.Attribute("Description") ?? "", breakList.Length > 1 || breakList is [not 1] ? breakList : null);
    }

    private static int Int(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static float Float(string? s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : fallback;

    /// <summary>Placeholder channels: every one named a stem and a number, the same stem (Capture's demo show: Dimmer1..Dimmer268).</summary>
    private static readonly System.Text.RegularExpressions.Regex Numbered = new(@"^([A-Za-z]+?)\d+$");

    /// <summary>
    /// A GDTF whose channels say nothing (#941): a file whose profiles were stripped to numbered
    /// placeholders, as Capture's exported demo shows are, the real layout lost. The fixture is still
    /// hung where the file puts it, as our own type of the kind its name says (a wash, a beam, a spot; a
    /// static wash for anything else), our channels at its address; the import notes it. Null when the channels are real and only unknown to us.
    /// </summary>
    private static FixtureType? Placeholder(Type g)
    {
        if (g.Channels.Count == 0) return null;
        string? stem = null;
        foreach (var c in g.Channels)
        {
            var m = Numbered.Match(c.Attribute.Trim());
            if (!m.Success || stem != null && m.Groups[1].Value != stem) return null;
            stem = m.Groups[1].Value;
        }
        string n = g.Name.ToLowerInvariant();
        bool moving = n.Contains("spot") || n.Contains("wash") || n.Contains("beam") || n.Contains("profile") || n.Contains("hybrid");
        var ours = n.Contains("wash") ? FixtureType.Wash : n.Contains("beam") && !n.Contains("wash") ? FixtureType.Beam : n.Contains("spot") || moving ? FixtureType.Spot : null;
        var layout = ours?.Channels ?? new[] { Attr.Dimmer, Attr.Red, Attr.Green, Attr.Blue, Attr.White };
        // our channels only: a stripped profile lists every mode's channels at once (268 for a spot the file
        // patches 32 apart), so its footprint says nothing either
        var attrs = layout.ToArray();
        string name = $"{(g.Manufacturer is "" ? "" : g.Manufacturer + " ")}{g.Name} (layout unknown: as {(ours?.Name ?? "a static RGBW wash")})";
        return ours != null
            ? new FixtureType(name, ours.Kind, attrs, ours.PanRange, ours.TiltRange, ours.BeamMin, ours.BeamMax)
            : new FixtureType(name, FixtureKind.Wash, attrs, 0f, 0f, 25f, 25f);
    }

    /// <summary>
    /// One of our fixture types from a read GDTF mode: each channel at its offset (a 16-bit channel's
    /// second byte its fine, or Other), and a kind from what it can do. Null, with why, for a device
    /// the game cannot show (no light, no effect it knows).
    /// </summary>
    public static FixtureType? ToFixtureType(Type g, out string? why, int dmxBreak = 1)
    {
        why = null;
        // one break at a time: each is its own address, patched as its own fixture (#959)
        if (g.Channels.Any(c => c.Break != dmxBreak))
        {
            var mine = g.Channels.Where(c => c.Break == dmxBreak).ToList();
            g = g with { Channels = mine, Footprint = mine.Select(c => Math.Max(c.Last, c.Offset + c.Bytes)).DefaultIfEmpty(0).Max() };
        }
        if (g.Footprint == 0 || g.Footprint > DmxPatch.Channels) { why = $"{g.Footprint} channels"; return null; }
        var attrs = Enumerable.Repeat(Attr.Other, g.Footprint).ToArray();
        var parsed = g.Channels.Select(c => (c, a: Parse(c.Attribute))).ToList();
        bool has(Attr a) => parsed.Any(p => p.a == a);
        int count(Attr a) => parsed.Count(p => p.a == a);
        bool panTilt = has(Attr.Pan) || has(Attr.Tilt);
        bool rgb = has(Attr.Red) && has(Attr.Green) && has(Attr.Blue);

        // what it is
        FixtureKind kind;
        float beam = g.BeamAngle > 0 ? g.BeamAngle : 20f;
        var zoom = g.Channels.FirstOrDefault(c => Parse(c.Attribute) == Attr.Zoom && c.PhysicalTo > c.PhysicalFrom && c.PhysicalFrom > 0);
        float bMin = zoom?.PhysicalFrom ?? beam, bMax = zoom?.PhysicalTo ?? beam;
        if (panTilt)
        {
            kind = rgb && !has(Attr.Gobo) ? FixtureKind.Wash
                : bMax <= 8f || has(Attr.Prism) && !has(Attr.Cyan) && bMax <= 15f ? FixtureKind.Beam
                : FixtureKind.Spot;
        }
        else if (count(Attr.Red) >= 2 && count(Attr.Green) >= 2 && count(Attr.Blue) >= 2) kind = FixtureKind.PixelBar;
        else if (count(Attr.Dimmer) >= 2 && !rgb) kind = FixtureKind.Blinder;
        else if (has(Attr.Rate) || has(Attr.Duration) || g.Name.Contains("strobe", StringComparison.OrdinalIgnoreCase)) kind = FixtureKind.Strobe;
        else if (has(Attr.Clip)) kind = FixtureKind.Screen;
        else if (has(Attr.Arm) && has(Attr.Fire)) kind = FixtureKind.Flame;
        else if (has(Attr.Pump)) kind = FixtureKind.Hazer;
        else if (rgb || has(Attr.Dimmer)) kind = FixtureKind.Wash;   // a static par or a bar of one cell: a wash that never moves
        else if (Placeholder(g) is { } stand) return stand;
        else { why = "no light or effect the game knows (" + string.Join(", ", g.Channels.Select(c => c.Attribute).Distinct().Take(6)) + ")"; return null; }

        // where each channel goes
        foreach (var (c, a) in parsed)
        {
            if (c.Offset >= attrs.Length) continue;
            var put = a;
            // a pixel bar's colours and a blinder's dimmers on their cells are its cells
            if (kind == FixtureKind.PixelBar && a is Attr.Red or Attr.Green or Attr.Blue) put = Attr.Cell;
            if (kind == FixtureKind.Blinder && a == Attr.Dimmer && c.Geometry.Length > 0 && count(Attr.Dimmer) >= 2) put = Attr.Cell;
            // only the first of anything drives it (a second colour wheel stays still)
            if (put != Attr.Cell && put != Attr.Other && attrs.Contains(put)) put = Attr.Other;
            attrs[c.Offset] = put;
            if (c.Bytes >= 2 && c.Offset + 1 < attrs.Length)
                attrs[c.Offset + 1] = put switch { Attr.Pan => Attr.PanFine, Attr.Tilt => Attr.TiltFine, Attr.Dimmer => Attr.DimmerFine, _ => Attr.Other };
        }
        // a pixel bar's cells in R, G, B order per cell, as the desk writes them
        if (kind == FixtureKind.PixelBar)
        {
            var cells = parsed.Where(p => p.a is Attr.Red or Attr.Green or Attr.Blue).ToList();
            bool ordered = true;
            for (int i = 0; i + 2 < cells.Count; i += 3)
                ordered &= cells[i].a == Attr.Red && cells[i + 1].a == Attr.Green && cells[i + 2].a == Attr.Blue;
            if (!ordered || cells.Count % 3 != 0) { why = "a pixel bar whose cells are not R, G, B in order"; return null; }
        }
        var pan = g.Channels.FirstOrDefault(c => Parse(c.Attribute) == Attr.Pan);
        var tilt = g.Channels.FirstOrDefault(c => Parse(c.Attribute) == Attr.Tilt);
        float panRange = pan != null ? MathF.Abs(pan.PhysicalTo - pan.PhysicalFrom) : 0f;
        float tiltRange = tilt != null ? MathF.Abs(tilt.PhysicalTo - tilt.PhysicalFrom) : 0f;
        if (pan != null && panRange < 1f) panRange = 540f;
        if (tilt != null && tiltRange < 1f) tiltRange = 270f;
        // ours says exactly what it is
        if (g.Manufacturer == "UnitSport" && FromSignature(g.Description, out var k, out float p0, out float t0, out float bb0, out float bb1))
        {
            kind = k;
            panRange = p0;
            tiltRange = t0;
            bMin = bb0;
            bMax = bb1;
        }
        string name = g.Manufacturer is "" or "UnitSport" ? g.Name : $"{g.Manufacturer} {g.Name}";
        if (g.Mode is not ("" or ModeName)) name += $" ({g.Mode})";
        if (dmxBreak != 1) name += FormattableString.Invariant($" (break {dmxBreak})");
        return new FixtureType(name, kind, attrs, Math.Min(panRange, 720f), Math.Min(tiltRange, 360f),
            Math.Clamp(Math.Min(bMin, bMax), 0.5f, 120f), Math.Clamp(Math.Max(bMin, bMax), 0.5f, 120f));
    }
}
