using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// MVR, My Virtual Rig (DIN SPEC 15801), for a concert's rig (#941): a zip holding
/// <c>GeneralSceneDescription.xml</c> (a layer per rig group, every fixture with its matrix, its GDTF,
/// its DMX address), one GDTF per fixture type and, optionally, the stage as a glTF scene object.
/// <para>
/// Frames. MVR is millimetres, Z up; ours is metres, Y up, u east and v south of the centre spot
/// (<see cref="ToMvr"/>: x stays, y = -v, z = up). A fixture's matrix: its GDTF Z axis is minus our
/// yoke axis (the beam leaves along -Z at pan and tilt zero), its Y axis our home, levelled across the
/// yoke. A file we wrote says so in its user data, and its fixtures land where they were, exactly
/// (home and screens kept there too); anyone else's is read in the stage's frame (origin the deck's
/// front centre on the pitch, Y upstage), mapped by the caller.
/// </para>
/// Pure: the caller reads and writes the file and builds the glTF.
/// </summary>
public static partial class MvrFile
{
    public const string SceneEntry = "GeneralSceneDescription.xml";
    private const string Provider = "UnitSport";

    /// <summary>Our frame (metres, Y up) to MVR's (metres, Z up).</summary>
    public static Vector3 ToMvr(Vector3 v) => new(v.X, -v.Z, v.Y);
    public static Vector3 FromMvr(Vector3 v) => new(v.X, v.Z, -v.Y);

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Vec(Vector3 v) => $"{{{F(v.X)},{F(v.Y)},{F(v.Z)}}}";

    /// <summary>A fixture's MVR axes from our yoke axis and home: Z = -up, Y = home levelled across the yoke.</summary>
    public static Basis Axes(Vector3 up, Vector3 home)
    {
        up = up.Normalized();
        var fwd = home - up * home.Dot(up);
        if (fwd.LengthSquared() < 1e-6f) fwd = up.Cross(MathF.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Forward).Cross(up);
        fwd = fwd.Normalized();
        var z = -up;
        var x = fwd.Cross(z).Normalized();
        return new Basis(x, fwd, z);
    }

    /// <summary>
    /// The MVR of a rig: <paramref name="stageGlb"/>, when given, becomes a scene object (glTF, in our
    /// frame and units, which glTF shares: Y up, metres).
    /// </summary>
    public static byte[] Write(RigData rig, string title, byte[]? stageGlb = null)
    {
        var types = rig.Types.Select(RigData.Type).ToArray();
        var specs = types.Select(Gdtf.FileName).ToArray();
        var layers = new Dictionary<int, XElement>();
        var extras = new XElement("Fixtures");
        int id = 0;
        foreach (var f in rig.Fixtures)
        {
            id++;
            if (!layers.TryGetValue(f.Group, out var children))
                layers[f.Group] = children = new XElement("ChildList");
            var axes = Axes(RigData.V(f.Up), RigData.V(f.Home));
            var origin = ToMvr(RigData.V(f.Pos)) * 1000f;
            string uuid = Gdtf.Uuid($"{title}/{id}/{f.Name}");
            children.Add(new XElement("Fixture", new XAttribute("name", f.Name), new XAttribute("uuid", uuid),
                new XElement("Matrix", Vec(ToMvr(axes.X)) + Vec(ToMvr(axes.Y)) + Vec(ToMvr(axes.Z)) + Vec(origin)),
                new XElement("GDTFSpec", specs[f.Type]),
                new XElement("GDTFMode", Gdtf.ModeName),
                new XElement("Addresses", new XElement("Address", new XAttribute("break", "0"), f.Universe * DmxPatch.Channels + f.Address + 1)),
                new XElement("FixtureID", id.ToString(CultureInfo.InvariantCulture)),
                new XElement("FixtureIDNumeric", id.ToString(CultureInfo.InvariantCulture)),
                new XElement("UnitNumber", "0"),
                new XElement("CustomId", "0")));
            // what MVR has no place for: the exact home, a screen's size and pixels
            var extra = new XElement("Fixture", new XAttribute("uuid", uuid), new XAttribute("home", Vec(RigData.V(f.Home))));
            if (f.Screen != null)
                extra.Add(new XAttribute("screen", Vec(RigData.V(f.Screen))), new XAttribute("px", f.PixelsX), new XAttribute("py", f.PixelsY));
            extras.Add(extra);
        }
        var layerList = new XElement("Layers");
        foreach (var (group, children) in layers.OrderBy(kv => kv.Key))
            layerList.Add(new XElement("Layer", new XAttribute("name", ((RigGroup)group).ToString()), new XAttribute("uuid", Gdtf.Uuid($"{title}/layer/{group}")), children));
        if (stageGlb != null)
            layerList.Add(new XElement("Layer", new XAttribute("name", "Stage"), new XAttribute("uuid", Gdtf.Uuid($"{title}/layer/stage")),
                new XElement("ChildList", new XElement("SceneObject", new XAttribute("name", "Stage"), new XAttribute("uuid", Gdtf.Uuid($"{title}/stage")),
                    new XElement("Matrix", "{1,0,0}{0,1,0}{0,0,1}{0,0,0}"),
                    new XElement("Geometries", new XElement("Geometry3D", new XAttribute("fileName", "stage.glb")))))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "no"),
            new XElement("GeneralSceneDescription", new XAttribute("verMajor", "1"), new XAttribute("verMinor", "6"),
                new XAttribute("provider", Provider), new XAttribute("providerVersion", RigData.Version),
                new XElement("UserData", new XElement("Data", new XAttribute("provider", Provider), new XAttribute("ver", RigData.Version),
                    new XElement("Frame", "stadium"), new XElement("Title", title), extras)),
                new XElement("Scene", new XElement("AUXData"), layerList)));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(zip, SceneEntry, w => doc.Save(w));
            var written = new HashSet<string>();
            for (int i = 0; i < types.Length; i++)
                if (written.Add(specs[i]))
                {
                    var e = zip.CreateEntry(specs[i], CompressionLevel.NoCompression);   // a GDTF is a zip already
                    using var s = e.Open();
                    var bytes = Gdtf.Write(types[i]);
                    s.Write(bytes, 0, bytes.Length);
                }
            if (stageGlb != null)
            {
                var e = zip.CreateEntry("stage.glb", CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(stageGlb, 0, stageGlb.Length);
            }
        }
        return ms.ToArray();
    }

    private static void Put(ZipArchive zip, string name, Action<StreamWriter> write)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        write(w);
    }

    // ---- reading ------------------------------------------------------------------------------------

    /// <summary>
    /// What an MVR holds for the game: the rig (positions in the file's frame turned Y up, metres; ours
    /// already in the stadium frame), whether it is ours, and what was skipped and why.
    /// </summary>
    public sealed record Import(RigData Rig, bool OursStadiumFrame, List<string> Notes);

    /// <summary>Reads an .mvr; null with why when it is not one or holds nothing the game can light.</summary>
    public static Import? Read(byte[] mvr, string source, out string? error)
    {
        error = null;
        try
        {
            using var ms = new MemoryStream(mvr);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var sceneEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(SceneEntry, StringComparison.OrdinalIgnoreCase));
            if (sceneEntry == null) { error = $"no {SceneEntry}"; return null; }
            XDocument doc;
            using (var s = sceneEntry.Open()) doc = XDocument.Load(s);
            return FromScene(doc, name => Entry(zip, name), source, out error);
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException or IOException)
        {
            error = e.Message;
            return null;
        }
    }

    private static byte[]? Entry(ZipArchive zip, string name)
    {
        var e = zip.Entries.FirstOrDefault(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? zip.Entries.FirstOrDefault(x => x.FullName.Equals(name + ".gdtf", StringComparison.OrdinalIgnoreCase))
            ?? zip.Entries.FirstOrDefault(x => x.Name.Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));
        if (e == null || e.Length > 64 << 20) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static Import? FromScene(XDocument doc, Func<string, byte[]?> files, string source, out string? error)
    {
        error = null;
        var root = doc.Root;
        if (root?.Name.LocalName != "GeneralSceneDescription") { error = "not an MVR scene"; return null; }
        var ours = root.Element("UserData")?.Elements("Data").FirstOrDefault(d => (string?)d.Attribute("provider") == Provider);
        bool stadiumFrame = (string?)ours?.Element("Frame") == "stadium";
        var extras = ours?.Element("Fixtures")?.Elements("Fixture").ToDictionary(x => (string?)x.Attribute("uuid") ?? "", x => x)
            ?? new Dictionary<string, XElement>();

        var rig = new RigData { Source = source };
        var notes = new List<string>();
        var typeIndex = new Dictionary<string, int>();
        var skipped = new Dictionary<string, int>();
        var ids = new List<int?>();
        var unpatched = new List<RigData.FixtureRow>();
        int end = 0;
        foreach (var layer in root.Element("Scene")?.Element("Layers")?.Elements("Layer") ?? Enumerable.Empty<XElement>())
        {
            string layerName = (string?)layer.Attribute("name") ?? "";
            var layerXf = ParseMatrix((string?)layer.Element("Matrix")?.Value);
            Walk(layer.Element("ChildList"), layerXf, layerName);
        }

        void Walk(XElement? list, Transform3D parent, string layerName)
        {
            if (list == null) return;
            foreach (var child in list.Elements())
            {
                var xf = parent * ParseMatrix((string?)child.Element("Matrix")?.Value);
                if (child.Name.LocalName == "Fixture") AddFixture(child, xf, layerName);
                // fixtures hang inside trusses, groups and scene objects too
                Walk(child.Element("ChildList"), xf, layerName);
            }
        }

        void AddFixture(XElement x, Transform3D xf, string layerName)
        {
            string name = (string?)x.Attribute("name") ?? "Fixture";
            string spec = (x.Element("GDTFSpec")?.Value ?? "").Trim();
            string mode = (x.Element("GDTFMode")?.Value ?? "").Trim();
            if (spec.Length == 0) { Skip("no GDTF (a placeholder)"); return; }
            string key = spec + "|" + mode;
            if (!typeIndex.TryGetValue(key, out int t))
            {
                var bytes = files(spec);
                FixtureType? type = null;
                string? why;
                if (bytes == null) why = $"{spec} is not in the file";
                else if (Gdtf.Read(bytes, mode, out why) is { } g) type = Gdtf.ToFixtureType(g, out why);
                t = typeIndex[key] = type == null ? -1 : rig.Types.Count;
                if (type != null) rig.Types.Add(RigData.Row(type));
                else notes.Add($"{spec} ({mode}): {why}");
                if (type != null && type.Name.Contains("(layout unknown", StringComparison.Ordinal))
                    notes.Add($"{spec}: its channels are numbered placeholders (a stripped profile): hung with our own layout");
            }
            if (t < 0) { Skip(spec); return; }
            var type0 = rig.Types[t];
            // address 0 or none: hung but not patched yet (a plot drawn before the patch); patched after the rest below
            bool patched = TryAddress(x, out int universe, out int address);
            if (patched && address + type0.Channels.Length > DmxPatch.Channels) { Skip("crosses a universe's end"); return; }

            // MVR is millimetres, Z up: the fixture's axes and place in ours
            var zAxis = FromMvr(xf.Basis.Z).Normalized();
            var yAxis = FromMvr(xf.Basis.Y).Normalized();
            var pos = FromMvr(xf.Origin) / 1000f;
            var up = -zAxis;
            var home = yAxis;
            string uuid = (string?)x.Attribute("uuid") ?? "";
            var row = new RigData.FixtureRow { Name = name, Type = t, Universe = universe, Address = address, Pos = RigData.V(pos), Up = RigData.V(up) };
            if (extras.TryGetValue(uuid, out var extra))
            {
                if (ParseVec((string?)extra.Attribute("home")) is { } h) home = h;
                if (ParseVec((string?)extra.Attribute("screen")) is { } sz)
                {
                    row.Screen = RigData.V(sz);
                    row.PixelsX = (int?)extra.Attribute("px") ?? 16;
                    row.PixelsY = (int?)extra.Attribute("py") ?? 9;
                }
            }
            row.Home = RigData.V(home);
            var kind = (FixtureKind)type0.Kind;
            if (kind == FixtureKind.Screen && row.Screen == null)
            {
                // someone else's media server: a 16:9 wall of 8 m facing its home
                row.Screen = RigData.V(new Vector3(8f, 4.5f, 0f));
                row.PixelsX = 32;
                row.PixelsY = 18;
            }
            row.Group = (int)(Enum.TryParse<RigGroup>(layerName, out var layerGroup) ? layerGroup : GroupOf(kind, up));
            rig.Fixtures.Add(row);
            if (patched) end = Math.Max(end, universe * DmxPatch.Channels + address + type0.Channels.Length);
            else unpatched.Add(row);
            ids.Add(int.TryParse(x.Element("FixtureIDNumeric")?.Value ?? x.Element("FixtureID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fid) ? fid : null);
        }

        void Skip(string why) => skipped[why] = skipped.GetValueOrDefault(why) + 1;

        // the unpatched, one after another past the last address in use, never across a universe's end
        foreach (var row in unpatched)
        {
            int len = rig.Types[row.Type].Channels.Length;
            if (end % DmxPatch.Channels + len > DmxPatch.Channels) end = (end / DmxPatch.Channels + 1) * DmxPatch.Channels;
            row.Universe = end / DmxPatch.Channels;
            row.Address = end % DmxPatch.Channels;
            end += len;
        }
        if (unpatched.Count > 0) notes.Add($"{unpatched.Count} not patched in the file: given addresses from {unpatched[0].Universe + 1}.{unpatched[0].Address + 1}");

        // in fixture-ID order, as a console lists them (when every fixture has one)
        if (ids.Count > 1 && ids.All(i => i != null))
            rig.Fixtures = rig.Fixtures.Select((f, i) => (f, id: ids[i]!.Value, i)).OrderBy(t => t.id).ThenBy(t => t.i).Select(t => t.f).ToList();
        foreach (var (why, n) in skipped) notes.Add($"{n} skipped: {why}");
        if (rig.Fixtures.Count == 0) { error = "no fixture the game can show" + (notes.Count > 0 ? ": " + string.Join("; ", notes.Take(4)) : ""); return null; }
        return new Import(rig, stadiumFrame, notes);
    }

    /// <summary>Where a fixture of no known layer hangs in the desk's groups: by what it is and which way its yoke points.</summary>
    public static RigGroup GroupOf(FixtureKind kind, Vector3 up) => kind switch
    {
        FixtureKind.Spot => RigGroup.FrontTruss,
        FixtureKind.Beam => up.Y > 0.3f ? RigGroup.Floor : RigGroup.MidTruss,
        FixtureKind.Wash => up.Y > 0.3f ? RigGroup.Floor : RigGroup.BackTruss,
        FixtureKind.Blinder => RigGroup.Blinders,
        FixtureKind.Strobe => RigGroup.Strobes,
        FixtureKind.PixelBar => RigGroup.PixelBars,
        FixtureKind.Screen => RigGroup.Screens,
        FixtureKind.Hazer => RigGroup.Haze,
        FixtureKind.Flame => RigGroup.Flames,
        FixtureKind.Co2 => RigGroup.Co2,
        FixtureKind.Confetti => RigGroup.Confetti,
        FixtureKind.Gerb => RigGroup.Gerbs,
        FixtureKind.Airburst => RigGroup.Airbursts,
        FixtureKind.LowFog => RigGroup.LowFog,
        _ => RigGroup.Towers,
    };

    /// <summary>An MVR address: absolute (1-based, universe 1 address 1 = 1) or "universe.address".</summary>
    private static bool TryAddress(XElement fixture, out int universe, out int address)
    {
        universe = address = 0;
        var a = fixture.Element("Addresses")?.Elements("Address").FirstOrDefault(x => (string?)x.Attribute("break") is null or "0")
            ?? fixture.Element("Addresses")?.Elements("Address").FirstOrDefault();
        string v = a?.Value.Trim() ?? "";
        if (v.Contains('.'))
        {
            var p = v.Split('.');
            if (p.Length != 2 || !int.TryParse(p[0], out int u) || !int.TryParse(p[1], out int ad) || u < 1 || ad < 1 || ad > DmxPatch.Channels) return false;
            universe = u - 1;
            address = ad - 1;
            return true;
        }
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int abs) || abs < 1) return false;
        universe = (abs - 1) / DmxPatch.Channels;
        address = (abs - 1) % DmxPatch.Channels;
        return true;
    }

    /// <summary>"{ux,uy,uz}{vx,vy,vz}{wx,wy,wz}{ox,oy,oz}": the local axes and the origin (mm); identity when absent.</summary>
    public static Transform3D ParseMatrix(string? m)
    {
        if (string.IsNullOrWhiteSpace(m)) return Transform3D.Identity;
        var groups = m.Split('}', StringSplitOptions.RemoveEmptyEntries).Select(g => g.Trim().TrimStart('{'))
            .Select(g => g.Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : float.NaN).ToArray())
            .ToList();
        if (groups.Count < 4 || groups.Take(4).Any(g => g.Length < 3 || g.Take(3).Any(float.IsNaN))) return Transform3D.Identity;
        Vector3 V(float[] g) => new(g[0], g[1], g[2]);
        return new Transform3D(new Basis(V(groups[0]), V(groups[1]), V(groups[2])), V(groups[3]));
    }

    private static Vector3? ParseVec(string? v)
    {
        if (v == null) return null;
        var p = v.Trim('{', '}', ' ').Split(',');
        if (p.Length < 3) return null;
        var f = p.Take(3).Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ? r : float.NaN).ToArray();
        return f.Any(float.IsNaN) ? null : new Vector3(f[0], f[1], f[2]);
    }
}
