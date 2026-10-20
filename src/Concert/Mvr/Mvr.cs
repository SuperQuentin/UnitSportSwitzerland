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

    private static string F(float v) => Finite(v).ToString("0.######", CultureInfo.InvariantCulture);
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
    /// frame and units, which glTF shares: Y up, metres). <paramref name="originals"/> gives an imported
    /// type's own GDTF by its GDTFSpec (#959): written back as it was, under its own spec and mode
    /// names, with every DMX break on the one fixture; ours is generated for the rest.
    /// </summary>
    public static byte[] Write(RigData rig, string title, byte[]? stageGlb = null, Func<string, byte[]?>? originals = null)
    {
        var types = rig.Types.Select(RigData.Type).ToArray();
        var kept = rig.Types.Select(t => t.GdtfSpec is { Length: > 0 } s ? originals?.Invoke(s) : null).ToArray();
        var gdtfFiles = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        (string Spec, string Mode) Spec(int t, bool ours)
        {
            if (!ours && kept[t] is { } bytes)
            {
                string spec = rig.Types[t].GdtfSpec!;
                gdtfFiles.TryAdd(SpecFile(spec), () => bytes);
                return (spec, rig.Types[t].GdtfMode ?? Gdtf.ModeName);
            }
            string file = Gdtf.FileName(types[t]);
            gdtfFiles.TryAdd(file, () => Gdtf.Write(types[t]));
            return (file, Gdtf.ModeName);
        }

        // fixture IDs: an imported one's own where it is unique, the next free number otherwise
        var given = rig.Fixtures.Where(f => f.FixtureId > 0).Select(f => f.FixtureId).ToHashSet();
        var usedIds = new HashSet<int>();
        int nextId = 0;
        int NextId()
        {
            do nextId++; while (given.Contains(nextId) || !usedIds.Add(nextId));
            return nextId;
        }
        var usedUuids = new HashSet<string>();
        string NewUuid(string? kept, string seed) =>
            Key(kept) is { } k && usedUuids.Add(k) ? Uuid(kept)! : Gdtf.Uuid(seed) is var u && usedUuids.Add(Key(u)!) ? u : Gdtf.Uuid(seed + "/" + usedUuids.Count);

        var layers = new Dictionary<int, XElement>();
        var extras = new XElement("Fixtures");
        int n = 0;
        foreach (var f in rig.Fixtures)
        {
            n++;
            if (!layers.TryGetValue(f.Group, out var children))
                layers[f.Group] = children = new XElement("ChildList");
            var up = RigData.V(f.Up);
            var home = RigData.V(f.Home);
            var pos = RigData.V(f.Pos);
            string uuid = NewUuid(f.Uuid, $"{title}/{n}/{f.Name}");
            int id = f.FixtureId > 0 && usedIds.Add(f.FixtureId) ? f.FixtureId : NextId();
            // its other breaks ride on the one fixture with its own GDTF; with ours each is a fixture of its own
            bool together = kept[f.Type] != null && (f.Breaks ?? new()).All(b => kept[b[1]] != null && rig.Types[b[1]].GdtfSpec == rig.Types[f.Type].GdtfSpec);
            var (spec, mode) = Spec(f.Type, false);
            var addresses = new XElement("Addresses", new XElement("Address", new XAttribute("break", "0"), f.Universe * DmxPatch.Channels + f.Address + 1));
            if (together && f.Breaks != null)
                foreach (var b in f.Breaks)
                    addresses.Add(new XElement("Address", new XAttribute("break", (b[0] - 1).ToString(CultureInfo.InvariantCulture)), b[2] * DmxPatch.Channels + b[3] + 1));

            // its focus point: where the beam meets at pan and tilt centre (10 m out when the distance is lost)
            string? focus = null;
            if (f.FocusDistance > 0 || f.FocusPan != 0 || f.FocusTilt != 0)
            {
                focus = NewUuid(null, uuid + "/focus");
                var point = pos + Beam(up, home, f.FocusPan, f.FocusTilt) * (f.FocusDistance > 0 ? f.FocusDistance : 10f);
                children.Add(new XElement("FocusPoint", new XAttribute("name", $"{f.Name} focus"), new XAttribute("uuid", focus),
                    new XElement("Matrix", "{1,0,0}{0,1,0}{0,0,1}" + Vec(ToMvr(point) * 1000f)),
                    new XElement("Classing", ClassUuid(f.Group)),
                    new XElement("Geometries")));
            }
            var fixture = Fixture(f.Name, uuid, Axes(up, home), pos, spec, mode, focus, f, id, addresses);
            // a fixed head's zoom its GDTF has no channel for goes back as the CustomCommand it came as
            var tr = rig.Types[f.Type];
            if (kept[f.Type] != null && tr.Name.EndsWith(ZoomTag(tr.BeamMax), StringComparison.Ordinal))
                fixture.Element("CustomId")!.AddBeforeSelf(new XElement("CustomCommands", new XElement("CustomCommand", "Zoom,f " + F(tr.BeamMax))));
            children.Add(fixture);
            // what MVR has no place for: the exact home, a screen's size and pixels
            var extra = new XElement("Fixture", new XAttribute("uuid", uuid), new XAttribute("home", Vec(home)));
            if (f.Screen != null)
                extra.Add(new XAttribute("screen", Vec(RigData.V(f.Screen))), new XAttribute("px", f.PixelsX), new XAttribute("py", f.PixelsY));
            extras.Add(extra);

            if (!together && f.Breaks != null)
                foreach (var b in f.Breaks)
                {
                    var (bSpec, bMode) = Spec(b[1], true);
                    children.Add(Fixture($"{f.Name} break {b[0]}", NewUuid(null, $"{uuid}/break{b[0]}"), Axes(up, home), pos, bSpec, bMode, null, f, NextId(),
                        new XElement("Addresses", new XElement("Address", new XAttribute("break", "0"), b[2] * DmxPatch.Channels + b[3] + 1))));
                }
        }
        var layerList = new XElement("Layers");
        foreach (var (group, children) in layers.OrderBy(kv => kv.Key))
            layerList.Add(new XElement("Layer", new XAttribute("name", ((RigGroup)group).ToString()), new XAttribute("uuid", Gdtf.Uuid($"{title}/layer/{group}")), children));
        if (stageGlb != null)
            layerList.Add(new XElement("Layer", new XAttribute("name", "Stage"), new XAttribute("uuid", Gdtf.Uuid($"{title}/layer/stage")),
                new XElement("ChildList", new XElement("SceneObject", new XAttribute("name", "Stage"), new XAttribute("uuid", Gdtf.Uuid($"{title}/stage")),
                    new XElement("Matrix", "{1,0,0}{0,1,0}{0,0,1}{0,0,0}"),
                    new XElement("Geometries", new XElement("Geometry3D", new XAttribute("fileName", "stage.glb")))))));

        // a class (visibility) and a position (logical group) per rig group, as the fixtures' Classing and Position name them
        var aux = new XElement("AUXData");
        foreach (int group in layers.Keys.OrderBy(g => g))
            aux.Add(new XElement("Class", new XAttribute("name", ((RigGroup)group).ToString()), new XAttribute("uuid", ClassUuid(group))));
        foreach (int group in layers.Keys.OrderBy(g => g))
            aux.Add(new XElement("Position", new XAttribute("name", ((RigGroup)group).ToString()), new XAttribute("uuid", PositionUuid(group))));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "no"),
            new XElement("GeneralSceneDescription", new XAttribute("verMajor", "1"), new XAttribute("verMinor", "6"),
                new XAttribute("provider", Provider), new XAttribute("providerVersion", RigData.Version),
                new XElement("UserData", new XElement("Data", new XAttribute("provider", Provider), new XAttribute("ver", RigData.Version),
                    new XElement("Frame", "stadium"), new XElement("Title", title), extras)),
                new XElement("Scene", aux, layerList)));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(zip, SceneEntry, w => doc.Save(w));
            foreach (var (file, bytes) in gdtfFiles)
            {
                var e = zip.CreateEntry(file, CompressionLevel.NoCompression);   // a GDTF is a zip already
                using var s = e.Open();
                var b = bytes();
                s.Write(b, 0, b.Length);
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

    private static string ZoomTag(float zoom) => FormattableString.Invariant($" (zoom {zoom})");

    private static string ClassUuid(int group) => Gdtf.Uuid($"UnitSport/class/{(RigGroup)group}");
    private static string PositionUuid(int group) => Gdtf.Uuid($"UnitSport/position/{(RigGroup)group}");

    /// <summary>A Fixture node, its children in the spec's order (MVR 1.6: FixtureID and FixtureIDNumeric required).</summary>
    private static XElement Fixture(string name, string uuid, Basis axes, Vector3 pos, string spec, string mode, string? focus,
        RigData.FixtureRow f, int id, XElement addresses)
    {
        var x = new XElement("Fixture", new XAttribute("name", name), new XAttribute("uuid", uuid),
            new XElement("Matrix", Vec(ToMvr(axes.X)) + Vec(ToMvr(axes.Y)) + Vec(ToMvr(axes.Z)) + Vec(ToMvr(pos) * 1000f)),
            new XElement("Classing", ClassUuid(f.Group)),
            new XElement("GDTFSpec", spec),
            new XElement("GDTFMode", mode));
        if (focus != null) x.Add(new XElement("Focus", focus));
        if (f.InvertPan) x.Add(new XElement("DMXInvertPan", "true"));
        if (f.InvertTilt) x.Add(new XElement("DMXInvertTilt", "true"));
        string fid = id.ToString(CultureInfo.InvariantCulture);
        x.Add(new XElement("Position", PositionUuid(f.Group)),
            new XElement("FixtureID", fid),
            new XElement("FixtureIDNumeric", fid),
            new XElement("UnitNumber", "0"),
            addresses,
            new XElement("CustomId", "0"));
        return x;
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
        // focus points, positions and video screens, wherever they are in the tree (#959)
        var index = Index(root);
        var gdtfs = new Dictionary<string, Gdtf.Type?>();
        var told = new Dictionary<string, int>();
        foreach (var layer in root.Element("Scene")?.Element("Layers")?.Elements("Layer") ?? Enumerable.Empty<XElement>())
        {
            string layerName = (string?)layer.Attribute("name") ?? "";
            var layerXf = ParseMatrix((string?)layer.Element("Matrix")?.Value);
            Walk(layer.Element("ChildList"), layerXf, layerName);
        }
        foreach (var (node, xf, layerName) in index.Screens) AddFixture(node, xf, layerName);
        ReadObjects(root, files, rig, notes, ours != null);   // trusses, scene objects, symbols (#958)

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

        // one of our types per GDTF, mode, break and zoom; -1 for one the game cannot show
        int TypeOf(string spec, string mode, int dmxBreak, float? zoom, bool screen)
        {
            string key = FormattableString.Invariant($"{spec}|{mode}|{dmxBreak}|{zoom}|{screen}");
            if (typeIndex.TryGetValue(key, out int t)) return t;
            Gdtf.Type? g = null;
            string? why = null;
            if (spec.Length > 0)
            {
                string file = spec + "|" + mode;
                if (!gdtfs.TryGetValue(file, out g))
                {
                    var bytes = files(spec);
                    if (bytes == null) why = $"{spec} is not in the file";
                    else g = Gdtf.Read(bytes, mode, out why);
                    gdtfs[file] = g;
                }
                else if (g == null) why = "unreadable";
            }
            var type = g != null ? Gdtf.ToFixtureType(g, out why, dmxBreak) : null;
            if (screen && type?.Kind != FixtureKind.Screen)
            {
                // a video screen whose GDTF the game cannot drive (or none): our media-server layer at its address
                if (spec.Length > 0) notes.Add($"{spec}: a video screen driven as our own media-server layer");
                type = FixtureType.Screen with { Name = g != null ? $"{(g.Manufacturer is "" ? "" : g.Manufacturer + " ")}{g.Name} (as a screen)" : FixtureType.Screen.Name };
            }
            // a CustomCommand's zoom on a head with no zoom channel: its beam (the pendants' 44 degrees)
            if (type != null && zoom is { } z && type.Offset(Attr.Zoom) < 0)
                type = type with { Name = type.Name + ZoomTag(z), BeamMin = z, BeamMax = z };
            t = typeIndex[key] = type == null ? -1 : rig.Types.Count;
            if (type != null)
            {
                var row = RigData.Row(type);
                if (g != null) { row.GdtfSpec = spec; row.GdtfMode = g.Mode; }
                rig.Types.Add(row);
            }
            else notes.Add($"{spec} ({mode}{(dmxBreak > 1 ? $", break {dmxBreak}" : "")}): {why}");
            if (type != null && dmxBreak == 1 && type.Name.Contains("(layout unknown", StringComparison.Ordinal))
                notes.Add($"{spec}: its channels are numbered placeholders (a stripped profile): hung with our own layout");
            return t;
        }

        void AddFixture(XElement x, Transform3D xf, string layerName)
        {
            string name = (string?)x.Attribute("name") ?? "Fixture";
            bool screen = x.Name.LocalName == "VideoScreen";
            string spec = (x.Element("GDTFSpec")?.Value ?? "").Trim();
            string mode = (x.Element("GDTFMode")?.Value ?? "").Trim();
            if (spec.Length == 0 && !screen) { Skip("no GDTF (a placeholder)"); return; }
            int t = TypeOf(spec, mode, 1, screen ? null : CustomZoom(x), screen);
            if (t < 0) { Skip(spec); return; }
            var type0 = rig.Types[t];
            // address 0 or none: hung but not patched yet (a plot drawn before the patch); patched after the rest below
            var addresses = Addresses(x);
            bool patched = addresses.TryGetValue(0, out var at);
            int universe = at.Universe, address = at.Address;
            if (patched && address + type0.Channels.Length > DmxPatch.Channels) { Skip("crosses a universe's end"); return; }

            // MVR is millimetres, Z up: the fixture's axes and place in ours
            var zAxis = FromMvr(xf.Basis.Z).Normalized();
            var yAxis = FromMvr(xf.Basis.Y).Normalized();
            var pos = FromMvr(xf.Origin) / 1000f;
            var up = -zAxis;
            var home = yAxis;
            // a video screen faces -Y: the audience, in a plot whose Y runs upstage
            if (screen) { up = Vector3.Up; home = -yAxis; }
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
                // someone else's media server: a 16:9 wall of 8 m facing its home; a video screen's as its matrix scales it
                row.Screen = RigData.V(screen ? ScreenSize(xf) : new Vector3(8f, 4.5f, 0f));
                row.PixelsX = 32;
                row.PixelsY = 18;
            }
            // the group: its MVR Position's or layer's name when it is one of ours, else what it is
            string? position = Key(x.Element("Position")?.Value) is { } pk ? index.Positions.GetValueOrDefault(pk) : null;
            row.Group = (int)(Enum.TryParse<RigGroup>(position, out var posGroup) ? posGroup
                : Enum.TryParse<RigGroup>(layerName, out var layerGroup) ? layerGroup : GroupOf(kind, up));
            row.Uuid = Uuid(uuid);
            row.InvertPan = Bool(x.Element("DMXInvertPan"));
            row.InvertTilt = Bool(x.Element("DMXInvertTilt"));
            if (Key(x.Element("Focus")?.Value) is { } fk && index.Focus.TryGetValue(fk, out var focus))
                Tell(Aim(row, type0, pos, up, home, FromMvr(focus) / 1000f));
            // its other DMX breaks, each at its own address when the file patches it
            if (spec.Length > 0 && gdtfs.GetValueOrDefault(spec + "|" + mode)?.Breaks is { } breaks)
                foreach (int b in breaks)
                {
                    if (b == 1) continue;
                    if (!addresses.TryGetValue(b - 1, out var ab)) { Tell($"with DMX break {b} not patched in the file: only their first break lit"); continue; }
                    int tb = TypeOf(spec, mode, b, null, false);
                    if (tb < 0) continue;
                    if (ab.Address + rig.Types[tb].Channels.Length > DmxPatch.Channels) { Tell($"with DMX break {b} across a universe's end: not patched"); continue; }
                    if ((row.Breaks ??= new()).Count >= RigData.MaxBreaks) continue;
                    row.Breaks.Add(new[] { b, tb, ab.Universe, ab.Address });
                    end = Math.Max(end, ab.Universe * DmxPatch.Channels + ab.Address + rig.Types[tb].Channels.Length);
                }
            rig.Fixtures.Add(row);
            if (patched) end = Math.Max(end, universe * DmxPatch.Channels + address + type0.Channels.Length);
            else unpatched.Add(row);
            int? id = int.TryParse(x.Element("FixtureIDNumeric")?.Value ?? x.Element("FixtureID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fid) ? fid : null;
            row.FixtureId = id is > 0 ? id.Value : 0;
            ids.Add(id);
        }

        void Tell(string? what)
        {
            if (what != null) told[what] = told.GetValueOrDefault(what) + 1;
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
        foreach (var (what, n) in told) notes.Add($"{n} {what}");
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
