using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// An MVR fixture's details beyond its place and patch (#959): its focus point (aimed at, as a pan and
/// tilt from its home), DMXInvertPan/Tilt, every DMX break's address, a CustomCommand's zoom, a
/// VideoScreen as our screen; and the parts of an export MVR 1.6 asks for (classes, positions, focus
/// points, the imported GDTFs kept). Pure.
/// </summary>
public static partial class MvrFile
{
    /// <summary>What the scene says besides its fixtures' own nodes, wherever in the layer tree it sits.</summary>
    private sealed class SceneIndex
    {
        /// <summary>Focus points by uuid key: their place (MVR mm, Z up, parents composed).</summary>
        public readonly Dictionary<string, Vector3> Focus = new();
        /// <summary>AUXData positions by uuid key: their name.</summary>
        public readonly Dictionary<string, string> Positions = new();
        /// <summary>Video screens, each with its composed transform and its layer's name.</summary>
        public readonly List<(XElement Node, Transform3D Xf, string Layer)> Screens = new();
    }

    private static SceneIndex Index(XElement root)
    {
        var index = new SceneIndex();
        var scene = root.Element("Scene");
        foreach (var p in scene?.Element("AUXData")?.Elements("Position") ?? Enumerable.Empty<XElement>())
            if (Key((string?)p.Attribute("uuid")) is { } k) index.Positions[k] = (string?)p.Attribute("name") ?? "";
        foreach (var layer in scene?.Element("Layers")?.Elements("Layer") ?? Enumerable.Empty<XElement>())
            Walk(layer.Element("ChildList"), ParseMatrix((string?)layer.Element("Matrix")?.Value), (string?)layer.Attribute("name") ?? "");
        return index;

        void Walk(XElement? list, Transform3D parent, string layer)
        {
            if (list == null) return;
            foreach (var child in list.Elements())
            {
                var xf = parent * ParseMatrix((string?)child.Element("Matrix")?.Value);
                if (child.Name.LocalName == "FocusPoint" && Key((string?)child.Attribute("uuid")) is { } k) index.Focus[k] = xf.Origin;
                else if (child.Name.LocalName == "VideoScreen") index.Screens.Add((child, xf, layer));
                Walk(child.Element("ChildList"), xf, layer);
            }
        }
    }

    /// <summary>A UUID as a key: its 32 hex digits, upper case (the spec's own example writes them spaced); null for none or nil.</summary>
    public static string? Key(string? uuid)
    {
        if (uuid == null) return null;
        var hex = string.Concat(uuid.Where(Uri.IsHexDigit)).ToUpperInvariant();
        return hex.Length == 32 && hex.Any(c => c != '0') ? hex : null;
    }

    /// <summary>A UUID in the spec's form (XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX); null for none or nil.</summary>
    public static string? Uuid(string? uuid) =>
        Key(uuid) is { } k ? $"{k[..8]}-{k[8..12]}-{k[12..16]}-{k[16..20]}-{k[20..]}" : null;

    private static bool Bool(XElement? e) => e != null && e.Value.Trim().ToLowerInvariant() is "true" or "1" or "yes";

    /// <summary>A beam's direction at a pan from its home and a tilt from its yoke axis (degrees), as the rig turns a head (ConcertRig.Direction).</summary>
    public static Vector3 Beam(Vector3 up, Vector3 home, float panDeg, float tiltDeg)
    {
        var (u, fwd, right) = Frame(up, home);
        float pan = Mathf.DegToRad(panDeg), tilt = Mathf.DegToRad(tiltDeg);
        return (u * MathF.Cos(tilt) + (fwd * MathF.Cos(pan) + right * MathF.Sin(pan)) * MathF.Sin(tilt)).Normalized();
    }

    private static (Vector3 Up, Vector3 Fwd, Vector3 Right) Frame(Vector3 up, Vector3 home)
    {
        up = up.Normalized();
        var fwd = home - up * home.Dot(up);
        fwd = fwd.LengthSquared() < 1e-6f ? up.Cross(Vector3.Right).Normalized() : fwd.Normalized();
        return (up, fwd, up.Cross(fwd));
    }

    /// <summary>The pan from home and tilt from the yoke axis (degrees) that meet a point, and how far it is (m).</summary>
    public static (float Pan, float Tilt, float Distance) PanTilt(Vector3 pos, Vector3 up, Vector3 home, Vector3 target)
    {
        var d = target - pos;
        float dist = d.Length();
        if (dist < 0.01f) return (0f, 0f, 0f);
        var dir = d / dist;
        var (u, fwd, right) = Frame(up, home);
        float tilt = Mathf.RadToDeg(MathF.Acos(Math.Clamp(dir.Dot(u), -1f, 1f)));
        float pan = tilt < 0.01f ? 0f : Mathf.RadToDeg(MathF.Atan2(dir.Dot(right), dir.Dot(fwd)));
        return (pan, tilt, dist);
    }

    /// <summary>Whether a head's ranges reach a pan and tilt, either way round (pan half a turn over, tilt the other side).</summary>
    public static bool Reach(ref float pan, ref float tilt, float panRange, float tiltRange)
    {
        const float slack = 0.01f;
        if (MathF.Abs(pan) <= panRange / 2 + slack && MathF.Abs(tilt) <= tiltRange / 2 + slack) return true;
        float p2 = pan > 0 ? pan - 180f : pan + 180f, t2 = -tilt;
        if (MathF.Abs(p2) > panRange / 2 + slack || MathF.Abs(t2) > tiltRange / 2 + slack) return false;
        pan = p2;
        tilt = t2;
        return true;
    }

    /// <summary>
    /// A fixture aimed at its focus point (#959): the pan and tilt from its home that meet it, as far as
    /// the head reaches (<see cref="RigData.PatchInto"/> clamps). A head that does not move keeps its
    /// matrix. Returns the import's note for it.
    /// </summary>
    private static string? Aim(RigData.FixtureRow row, RigData.TypeRow type, Vector3 pos, Vector3 up, Vector3 home, Vector3 focus)
    {
        var (pan, tilt, dist) = PanTilt(pos, up, home, focus);
        if (dist <= 0f) return null;
        bool moves = type.PanRange > 0 || type.TiltRange > 0;
        // within 2 degrees of its beam: a fixed light hung at its point already
        if (!moves && tilt > 2f) return "do not move and their focus point is off their beam: hung as their matrix says";
        string what = !moves || Reach(ref pan, ref tilt, type.PanRange, type.TiltRange) ? "aimed at their focus point"
            : "cannot reach their focus point: leaned as far as they go";
        row.FocusPan = pan;
        row.FocusTilt = tilt;
        row.FocusDistance = MathF.Min(dist, 1000f);
        return what;
    }

    /// <summary>A CustomCommand setting the zoom's physical value, a beam angle (degrees): "Base_Zoom.Zoom.Zoom 1,f 44" or "Zoom,f 44".</summary>
    public static float? CustomZoom(XElement fixture)
    {
        foreach (var c in fixture.Element("CustomCommands")?.Elements("CustomCommand") ?? Enumerable.Empty<XElement>())
        {
            var parts = c.Value.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !parts[1].StartsWith("f ", StringComparison.Ordinal)) continue;
            // the channel function's path: Geometry_Attribute.Attribute.Function, or an attribute alone
            var path = parts[0].Split('.');
            string attr = path.Length >= 2 ? path[1] : path[0][(path[0].LastIndexOf('_') + 1)..];
            if (attr != "Zoom") continue;
            if (float.TryParse(parts[1][2..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v is >= 0.5f and <= 120f) return v;
        }
        return null;
    }

    /// <summary>
    /// A fixture's DMX addresses by MVR break (0 its first, GDTF's DMXBreak 1): absolute (1-based) or
    /// "universe.address"; address 0, none or past 64 universes left out (not patched).
    /// </summary>
    private static Dictionary<int, (int Universe, int Address)> Addresses(XElement fixture)
    {
        var all = new Dictionary<int, (int, int)>();
        foreach (var a in fixture.Element("Addresses")?.Elements("Address") ?? Enumerable.Empty<XElement>())
        {
            if (!int.TryParse((string?)a.Attribute("break") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int brk) || brk < 0) continue;
            if (ParseAddress(a.Value.Trim()) is { } at && at.Universe < RigData.MaxUniverses) all.TryAdd(brk, at);
        }
        return all;
    }

    private static (int Universe, int Address)? ParseAddress(string v)
    {
        if (v.Contains('.'))
        {
            var p = v.Split('.');
            if (p.Length != 2 || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int u)
                || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ad) || u < 1 || ad < 1 || ad > DmxPatch.Channels) return null;
            return (u - 1, ad - 1);
        }
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int abs) || abs < 1) return null;
        return ((abs - 1) / DmxPatch.Channels, (abs - 1) % DmxPatch.Channels);
    }

    /// <summary>
    /// A video screen's size (m): our 8 by 4.5 m wall scaled as its matrix scales it (X across, Z up);
    /// its geometry's own size is not read.
    /// </summary>
    private static Vector3 ScreenSize(Transform3D xf) =>
        new(Math.Clamp(8f * xf.Basis.X.Length(), 0.5f, 200f), Math.Clamp(4.5f * xf.Basis.Z.Length(), 0.5f, 200f), 0f);

    /// <summary>
    /// The GDTFs an MVR holds for some GDTFSpecs, as they are (#959): what a re-export of an imported rig
    /// writes back byte for byte. Missing ones are left out.
    /// </summary>
    public static Dictionary<string, byte[]> Gdtfs(byte[] mvr, IEnumerable<string> specs)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var ms = new MemoryStream(mvr);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var spec in specs)
                if (!found.ContainsKey(spec) && Entry(zip, spec) is { } bytes) found[spec] = bytes;
        }
        catch (Exception e) when (e is InvalidDataException or IOException) { }
        return found;
    }

    /// <summary>A GDTFSpec's file in the archive: the name with its .gdtf (MVR files often leave it out).</summary>
    private static string SpecFile(string spec) => spec.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase) ? spec : spec + ".gdtf";

    /// <summary>A float as MVR wants it: never NaN nor infinite.</summary>
    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;
}
