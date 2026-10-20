using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// A GDTF fixture's own body (#961), from its geometry tree: three rigid parts in the fixture's GDTF
/// frame (metres, Z = minus our yoke axis, Y = our home levelled; the beam leaves along -Z at pan and
/// tilt zero). The base is everything outside the pan axis, the yoke the pan axis' subtree outside the
/// tilt axis, the head the tilt axis' subtree; each with the pivot it turns about, and the point the
/// beam leaves the head. Each node's <c>Model</c> is its 3D file (glTF preferred over 3DS, the file's
/// box scaled to the model's Length, Width, Height about its origin, as GDTF says) or its primitive
/// (a box, a cylinder, a sphere, or boxes and cylinders shaped like a base, a yoke, a head, a par);
/// the parts simplified to <see cref="MaxPartTriangles"/> each (a body is small on screen). Pure.
/// </summary>
public sealed record GdtfBody(MvrMesh? Base, MvrMesh? Yoke, MvrMesh? Head, Vector3 YokePivot, Vector3 HeadPivot, Vector3 BeamAt)
{
    public const int MaxPartTriangles = 600;
    /// <summary>Geometry nodes walked at most (a pixel bar's references), and their triangles before simplifying.</summary>
    private const int MaxNodes = 4096, MaxTriangles = 400_000;
    /// <summary>A model thinner than this (m) is a lens disc or an emitter's plane: the flare draws it.</summary>
    private const float Thin = 0.003f;
    /// <summary>A primitive's colour: fixtures are black.</summary>
    public static readonly Color Black = new(0.13f, 0.13f, 0.14f);

    /// <summary>
    /// A yoke's and a head's transforms at a pan and tilt (radians), from the fixture's frame (its GDTF
    /// axes in ours, at its place): pan turns about the frame's Z through the yoke's pivot, tilt about
    /// the panned X through the head's. Ours pans toward the right of home, against GDTF's Z, so the
    /// head's -Z is the rig's beam direction. Parts are in the fixture's frame at rest, so the base's
    /// transform is the frame itself.
    /// </summary>
    public static (Transform3D Yoke, Transform3D Head) Pose(Transform3D frame, Vector3 yokePivot, Vector3 headPivot, float pan, float tilt)
    {
        var yoke = frame * new Transform3D(new Basis(new Vector3(0, 0, 1), -pan), yokePivot) * new Transform3D(Basis.Identity, -yokePivot);
        var head = yoke * new Transform3D(new Basis(new Vector3(1, 0, 0), tilt), headPivot) * new Transform3D(Basis.Identity, -headPivot);
        return (yoke, head);
    }

    /// <summary>A GDTF archive's files by their path inside it (64 MB each at most).</summary>
    public static Func<string, byte[]?> Files(ZipArchive zip) => name =>
    {
        var e = zip.Entries.FirstOrDefault(x => x.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (e == null || e.Length > 64 << 20) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    };

    /// <summary>
    /// The body of a read GDTF mode: its mode's geometry, the axes its pan and tilt channels name. Null
    /// when nothing is drawable (Capture's stripped profiles: a Base of no model), or for our own types,
    /// which keep the rig's own look.
    /// </summary>
    public static GdtfBody? Of(XDocument doc, Gdtf.Type type, Func<string, byte[]?> files)
    {
        if (type.Manufacturer == "UnitSport") return null;
        var ft = doc.Root?.Element("FixtureType");
        if (ft == null) return null;
        string? modeGeometry = (string?)ft.Element("DMXModes")?.Elements("DMXMode").FirstOrDefault(m => (string?)m.Attribute("Name") == type.Mode)?.Attribute("Geometry");
        string? pan = type.Channels.FirstOrDefault(c => Gdtf.Parse(c.Attribute) == Attr.Pan)?.Geometry;
        string? tilt = type.Channels.FirstOrDefault(c => Gdtf.Parse(c.Attribute) == Attr.Tilt)?.Geometry;
        return Read(ft, modeGeometry, pan, tilt, files);
    }

    /// <summary>
    /// The body of a FixtureType element. <paramref name="panGeometry"/> and <paramref name="tiltGeometry"/>:
    /// the geometries the pan and tilt channels act on (null: it does not pan or tilt; a name not found:
    /// the first axis, and the first inside that one). Never throws on bad data: null.
    /// </summary>
    public static GdtfBody? Read(XElement fixtureType, string? modeGeometry, string? panGeometry, string? tiltGeometry, Func<string, byte[]?> files)
    {
        try { return Build(fixtureType, modeGeometry, panGeometry, tiltGeometry, files); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidOperationException or InvalidDataException
            or IOException or OverflowException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    private sealed class Part
    {
        public readonly List<Vector3> Positions = new();
        public readonly List<int> Indices = new();
        public readonly Dictionary<Color, int> Colours = new();
    }

    private static GdtfBody? Build(XElement ft, string? modeGeometry, string? panGeometry, string? tiltGeometry, Func<string, byte[]?> files)
    {
        var geometries = ft.Element("Geometries");
        if (geometries == null) return null;
        var tops = geometries.Elements().ToList();
        var roots = modeGeometry != null && tops.FirstOrDefault(g => (string?)g.Attribute("Name") == modeGeometry) is { } m ? new List<XElement> { m } : tops;
        var models = new Dictionary<string, XElement>();
        foreach (var x in ft.Element("Models")?.Elements("Model") ?? Enumerable.Empty<XElement>())
            if ((string?)x.Attribute("Name") is { } n) models.TryAdd(n, x);

        // the axes: by the channels' geometry names, else the first axis and the first inside it
        XElement? Named(string? name) => name == null ? null
            : roots.SelectMany(r => r.DescendantsAndSelf()).FirstOrDefault(e => (string?)e.Attribute("Name") == name && e.Name.LocalName != "Break");
        XElement? panAxis = null, tiltAxis = null;
        if (panGeometry != null) panAxis = Named(panGeometry) ?? roots.SelectMany(r => r.DescendantsAndSelf("Axis")).FirstOrDefault();
        if (tiltGeometry != null)
            tiltAxis = Named(tiltGeometry) ?? (panAxis?.Descendants("Axis").FirstOrDefault() ?? roots.SelectMany(r => r.DescendantsAndSelf("Axis")).FirstOrDefault(a => a != panAxis));
        if (tiltAxis == panAxis) tiltAxis = null;

        var parts = new[] { new Part(), new Part(), new Part() };
        var meshes = new Dictionary<string, MvrMesh?>();
        Vector3 yokePivot = Vector3.Zero, headPivot = Vector3.Zero;
        Vector3? beam = null, headBeam = null;
        bool sawYoke = false, sawHead = false;
        int nodes = 0, triangles = 0;
        foreach (var r in roots) Walk(r, Transform3D.Identity, 0, 0, null);

        void Walk(XElement g, Transform3D parent, int part, int depth, string? modelOverride)
        {
            if (depth > 16 || ++nodes > MaxNodes) return;
            string kind = g.Name.LocalName;
            if (kind is "Break") return;
            var xf = parent * Matrix((string?)g.Attribute("Position"));
            if (g == panAxis) { part = 1; yokePivot = xf.Origin; sawYoke = true; }
            if (g == tiltAxis) { part = 2; headPivot = xf.Origin; sawHead = true; }
            if (kind == "Beam")
            {
                beam ??= xf.Origin;
                if (part == 2) headBeam ??= xf.Origin;
            }
            if (kind == "GeometryReference")
            {
                // another top-level geometry again (a bar's pixels), here; its model may be swapped
                string? target = (string?)g.Attribute("Geometry");
                if (tops.FirstOrDefault(t => (string?)t.Attribute("Name") == target) is { } refd && refd != g)
                    Walk(refd, xf * Matrix((string?)refd.Attribute("Position")).AffineInverse(), part, depth + 1, (string?)g.Attribute("Model"));
                return;
            }
            string? model = modelOverride ?? (string?)g.Attribute("Model");
            if (model != null && models.TryGetValue(model, out var mx) && MeshOf(model, mx) is { } mesh && triangles + mesh.Triangles <= MaxTriangles)
            {
                triangles += mesh.Triangles;
                var p = parts[part];
                int at = p.Positions.Count;
                foreach (var v in mesh.Positions) p.Positions.Add(xf * v);
                foreach (int i in mesh.Indices) p.Indices.Add(at + i);
                p.Colours[mesh.Color] = p.Colours.GetValueOrDefault(mesh.Color) + mesh.Triangles;
            }
            foreach (var c in g.Elements()) Walk(c, xf, part, depth + 1, null);
        }

        MvrMesh? MeshOf(string name, XElement model)
        {
            if (meshes.TryGetValue(name, out var cached)) return cached;
            return meshes[name] = ModelMesh(model, files);
        }

        MvrMesh? Merge(Part p)
        {
            if (p.Indices.Count == 0) return null;
            var colour = p.Colours.OrderByDescending(kv => kv.Value).First().Key;
            return MvrFile.Decimate(new MvrMesh(p.Positions.ToArray(), p.Indices.ToArray(), colour), MaxPartTriangles);
        }

        var body = new GdtfBody(Merge(parts[0]), Merge(parts[1]), Merge(parts[2]), yokePivot, sawHead ? headPivot : yokePivot,
            headBeam ?? beam ?? (sawHead ? headPivot : sawYoke ? yokePivot : Vector3.Zero));
        if (body.Base == null && body.Yoke == null && body.Head == null) return null;
        // a head that only tilts: its pivot is the pan's too (no pan turns it)
        if (!sawYoke && sawHead) body = body with { YokePivot = headPivot };
        return body;
    }

    /// <summary>A GDTF matrix: four rows, the translation in the fourth column (metres); identity when it is not one.</summary>
    public static Transform3D Matrix(string? m)
    {
        if (string.IsNullOrWhiteSpace(m)) return Transform3D.Identity;
        var rows = m.Split('}', StringSplitOptions.RemoveEmptyEntries).Select(g => g.Trim().TrimStart('{'))
            .Select(g => g.Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) ? f : float.NaN).ToArray())
            .ToList();
        if (rows.Count < 3 || rows.Take(3).Any(r => r.Length < 4 || r.Take(4).Any(float.IsNaN))) return Transform3D.Identity;
        var basis = new Basis(new Vector3(rows[0][0], rows[1][0], rows[2][0]), new Vector3(rows[0][1], rows[1][1], rows[2][1]), new Vector3(rows[0][2], rows[1][2], rows[2][2]));
        var origin = new Vector3(rows[0][3], rows[1][3], rows[2][3]);
        float det = MathF.Abs(basis.Determinant());
        if (origin.Length() > 50f || det < 1e-6f || det > 1e6f) return Transform3D.Identity;
        return new Transform3D(basis, origin);
    }

    /// <summary>A Model's mesh in its own frame: its file scaled to its dimensions, else its primitive; null when it draws nothing.</summary>
    public static MvrMesh? ModelMesh(XElement model, Func<string, byte[]?> files)
    {
        float F(string n) => float.TryParse((string?)model.Attribute(n), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? Math.Clamp(v, 0f, 10f) : 0f;
        var size = new Vector3(F("Length"), F("Width"), F("Height"));
        bool sized = size.X > 0 && size.Y > 0 && size.Z > 0;
        if (sized && Math.Min(size.X, Math.Min(size.Y, size.Z)) < Thin) return null;
        string file = ((string?)model.Attribute("File") ?? "").Trim();
        if (file.Length > 0 && FileMesh(file, files) is { } mesh)
        {
            var lo = Vector3.Inf;
            var hi = -Vector3.Inf;
            foreach (int i in mesh.Indices) { lo = lo.Min(mesh.Positions[i]); hi = hi.Max(mesh.Positions[i]); }
            var have = hi - lo;
            // no size given: the file as it is, unless it is no fixture's
            if (!sized && Math.Max(have.X, Math.Max(have.Y, have.Z)) > 10f) return null;
            float S(float want, float got) => want > 0 && got > 1e-6f ? want / got : 1f;
            var scale = new Vector3(S(size.X, have.X), S(size.Y, have.Y), S(size.Z, have.Z));
            var pos = new Vector3[mesh.Positions.Length];
            for (int i = 0; i < pos.Length; i++) pos[i] = mesh.Positions[i] * scale;
            return mesh with { Positions = pos };
        }
        if (!sized) return null;
        return Primitive((string?)model.Attribute("PrimitiveType") ?? "Undefined", size);
    }

    /// <summary>A model file in the archive: models/gltf first (glb, gltf), then models/3ds, by its name without extension.</summary>
    private static MvrMesh? FileMesh(string file, Func<string, byte[]?> files)
    {
        string stem = Path.HasExtension(file) && Path.GetExtension(file).ToLowerInvariant() is ".glb" or ".gltf" or ".3ds" ? Path.ChangeExtension(file, null)! : file;
        foreach (var (path, name) in new[] { ($"models/gltf/{stem}.glb", ".glb"), ($"models/gltf/{stem}.gltf", ".gltf"), ($"models/3ds/{stem}.3ds", ".3ds") })
            if (files(path) is { } bytes && MvrGeometry.Read(name, bytes, uri => files("models/gltf/" + uri), out _) is { } mesh) return mesh;
        return null;
    }

    /// <summary>
    /// A primitive of a model's size (X length, Y width, Z height), low-poly. A base and a par hang
    /// below their suspension point (Z from -height to 0), the rest are centred on it, as BlenderDMX's
    /// primitive models are; a yoke is a bar and two arms, open below.
    /// </summary>
    public static MvrMesh? Primitive(string type, Vector3 s)
    {
        var b = new Builder();
        var h = s / 2;
        switch (type)
        {
            case "Cube": case "Scanner": case "Scanner1_1": case "Head": case "Pigtail":
                b.Box(-h, h);
                break;
            case "Base": case "Base1_1":
                b.Box(new Vector3(-h.X, -h.Y, -s.Z), new Vector3(h.X, h.Y, 0));
                break;
            case "Cylinder":
                b.Cylinder(h.X, h.Y, -h.Z, h.Z, 10);
                break;
            case "Conventional": case "Conventional1_1":
                b.Cylinder(h.X, h.Y, -s.Z, 0, 10);
                break;
            case "Sphere":
                b.Sphere(h, 8, 6);   // an even ring count: one on the equator
                break;
            case "Yoke":
                float bar = s.Z * 0.18f, arm = s.X * 0.16f;
                b.Box(new Vector3(-h.X, -h.Y, h.Z - bar), h);
                b.Box(new Vector3(-h.X, -h.Y, -h.Z), new Vector3(-h.X + arm, h.Y, h.Z - bar));
                b.Box(new Vector3(h.X - arm, -h.Y, -h.Z), new Vector3(h.X, h.Y, h.Z - bar));
                break;
            default:
                return null;
        }
        return new MvrMesh(b.Positions.ToArray(), b.Indices.ToArray(), Black);
    }

    /// <summary>Triangles wound counter-clockwise seen from outside (right-handed), as the 3D files are.</summary>
    private sealed class Builder
    {
        public readonly List<Vector3> Positions = new();
        public readonly List<int> Indices = new();

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = Positions.Count;
            Positions.Add(a); Positions.Add(b); Positions.Add(c); Positions.Add(d);
            Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
            Indices.Add(i); Indices.Add(i + 2); Indices.Add(i + 3);
        }

        public void Box(Vector3 lo, Vector3 hi)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? lo.X : hi.X, y == 0 ? lo.Y : hi.Y, z == 0 ? lo.Z : hi.Z);
            Quad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));   // +X
            Quad(P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0));   // -X
            Quad(P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0));   // +Y
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));   // -Y
            Quad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1));   // +Z
            Quad(P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0));   // -Z
        }

        public void Cylinder(float rx, float ry, float z0, float z1, int sides)
        {
            int i0 = Positions.Count;
            for (int k = 0; k < sides; k++)
            {
                float a = k * MathF.Tau / sides;
                Positions.Add(new Vector3(rx * MathF.Cos(a), ry * MathF.Sin(a), z0));
                Positions.Add(new Vector3(rx * MathF.Cos(a), ry * MathF.Sin(a), z1));
            }
            for (int k = 0; k < sides; k++)
            {
                int a0 = i0 + k * 2, a1 = i0 + (k + 1) % sides * 2;
                Indices.Add(a0); Indices.Add(a1); Indices.Add(a1 + 1);
                Indices.Add(a0); Indices.Add(a1 + 1); Indices.Add(a0 + 1);
            }
            // the caps, fans from the first rim vertex
            for (int k = 1; k + 1 < sides; k++)
            {
                Indices.Add(i0); Indices.Add(i0 + (k + 1) * 2); Indices.Add(i0 + k * 2);
                Indices.Add(i0 + 1); Indices.Add(i0 + k * 2 + 1); Indices.Add(i0 + (k + 1) * 2 + 1);
            }
        }

        public void Sphere(Vector3 r, int segments, int rings)
        {
            int i0 = Positions.Count;
            for (int j = 0; j <= rings; j++)
            {
                float v = MathF.PI * j / rings;
                for (int k = 0; k < segments; k++)
                {
                    float u = MathF.Tau * k / segments;
                    Positions.Add(new Vector3(r.X * MathF.Sin(v) * MathF.Cos(u), r.Y * MathF.Sin(v) * MathF.Sin(u), r.Z * MathF.Cos(v)));
                }
            }
            for (int j = 0; j < rings; j++)
                for (int k = 0; k < segments; k++)
                {
                    int a = i0 + j * segments + k, b = i0 + j * segments + (k + 1) % segments;
                    int c = a + segments, d = b + segments;
                    // the poles' rings are one point each: their collapsed triangles are dropped when quantised
                    Indices.Add(a); Indices.Add(c); Indices.Add(d);
                    Indices.Add(a); Indices.Add(d); Indices.Add(b);
                }
        }
    }
}
