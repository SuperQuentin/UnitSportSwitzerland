using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Godot;
using UnitSport.Concert;
using UnitSport.Concert.Mvr;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>GDTF bodies (#961): the geometry tree into base, yoke and head, model files scaled, primitives, the pose, the wire and its caps.</summary>
public class GdtfBodyTests
{
    private readonly ITestOutputHelper _out;
    public GdtfBodyTests(ITestOutputHelper output) => _out = output;

    /// <summary>BlenderDMX's moving head (Beskyd): primitive base, yoke and head, two axes, a beam.</summary>
    private const string Models = """
        <Model Name="Base" Length="0.3" Width="0.3" Height="0.13" PrimitiveType="Base1_1"/>
        <Model Name="Yoke" Length="0.353" Width="0.116" Height="0.3" PrimitiveType="Yoke"/>
        <Model Name="Head" Length="0.224" Width="0.186" Height="0.244" PrimitiveType="Head"/>
        <Model Name="Beam" Length="0.16" Width="0.16" Height="0.001" PrimitiveType="Cylinder"/>
        """;
    private const string Tree = """
        <Geometry Name="Base" Model="Base" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0}{0,0,0,1}">
          <Axis Name="Yoke" Model="Yoke" Position="{1,0,0,0}{0,1,0,0}{0,0,1,-0.28}{0,0,0,1}">
            <Axis Name="Head" Model="Head" Position="{1.000000,0.000000,0.000000,0.000000}{0,1,0,0}{0,0,1,-0.100000}{0,0,0,1}">
              <Beam Name="Beam" Model="Beam" Position="{1,0,0,0}{0,1,0,0}{0,0,1,-0.13}{0,0,0,1}"/>
            </Axis>
          </Axis>
        </Geometry>
        """;

    private static XElement Ft(string models, string tree, string channels = "") => XElement.Parse(
        $"<FixtureType Name=\"Test\" Manufacturer=\"Someone\"><Models>{models}</Models><Geometries>{tree}</Geometries>" +
        $"<DMXModes><DMXMode Name=\"M\" Geometry=\"Base\"><DMXChannels>{channels}</DMXChannels></DMXMode></DMXModes></FixtureType>");

    private static Aabb Box(MvrMesh m)
    {
        var b = new Aabb(m.Positions[m.Indices[0]], Vector3.Zero);
        foreach (int i in m.Indices) b = b.Expand(m.Positions[i]);
        return b;
    }

    private static void Near(Vector3 want, Vector3 got, float tol = 0.0005f) =>
        Assert.True(want.DistanceTo(got) < tol, $"want {want}, got {got}");

    [Fact]
    public void A_moving_head_is_a_base_a_yoke_and_a_head_about_their_axes()
    {
        var body = GdtfBody.Read(Ft(Models, Tree), "Base", "Yoke", "Head", _ => null);
        Assert.NotNull(body);
        Assert.NotNull(body!.Base);
        Assert.NotNull(body.Yoke);
        Assert.NotNull(body.Head);
        Near(new Vector3(0, 0, -0.28f), body.YokePivot);
        Near(new Vector3(0, 0, -0.38f), body.HeadPivot);
        Near(new Vector3(0, 0, -0.51f), body.BeamAt);
        // the base hangs below its suspension point; the yoke and head are centred on their pivots
        var b = Box(body.Base!);
        Near(new Vector3(-0.15f, -0.15f, -0.13f), b.Position);
        Near(new Vector3(0.3f, 0.3f, 0.13f), b.Size);
        Near(new Vector3(0, 0, -0.28f), Box(body.Yoke!).GetCenter());
        Near(new Vector3(0, 0, -0.38f), Box(body.Head!).GetCenter());
        // the lens disc (1 mm) is the flare's: not drawn
        Assert.Equal(12, body.Head!.Triangles);
    }

    [Fact]
    public void Axes_unnamed_by_the_channels_are_the_first_axis_and_the_one_inside_it()
    {
        var body = GdtfBody.Read(Ft(Models, Tree), null, "Missing", "AlsoMissing", _ => null)!;
        Near(new Vector3(0, 0, -0.28f), body.YokePivot);
        Near(new Vector3(0, 0, -0.38f), body.HeadPivot);
        // no pan or tilt channel: one rigid body, whatever its axes
        var rigid = GdtfBody.Read(Ft(Models, Tree), null, null, null, _ => null)!;
        Assert.Null(rigid.Yoke);
        Assert.Null(rigid.Head);
        Assert.Equal(12 + 36 + 12, rigid.Base!.Triangles);
    }

    [Fact]
    public void The_pose_turns_the_head_where_the_rig_points_its_beam()
    {
        var up = new Vector3(0.2f, -1f, 0.1f).Normalized();
        var home = new Vector3(0.1f, 0f, 1f);
        var frame = new Transform3D(MvrFile.Axes(up, home), new Vector3(3, 12, -4));
        var fwd = (home - up * home.Dot(up)).Normalized();
        var right = up.Cross(fwd);
        foreach (var (panDeg, tiltDeg) in new[] { (0f, 0f), (30f, 50f), (-120f, 80f), (200f, -30f) })
        {
            float pan = Mathf.DegToRad(panDeg), tilt = Mathf.DegToRad(tiltDeg);
            var (yoke, head) = GdtfBody.Pose(frame, new Vector3(0, 0, -0.28f), new Vector3(0, 0, -0.38f), pan, tilt);
            // ConcertRig.Direction's formula
            var dir = (up * MathF.Cos(tilt) + (fwd * MathF.Cos(pan) + right * MathF.Sin(pan)) * MathF.Sin(tilt)).Normalized();
            Near(dir, (head.Basis * new Vector3(0, 0, -1)).Normalized(), 1e-4f);
            // each part turns about its own pivot, which stays put
            Near(frame * new Vector3(0, 0, -0.28f), yoke * new Vector3(0, 0, -0.28f), 1e-4f);
            Near(yoke * new Vector3(0, 0, -0.38f), head * new Vector3(0, 0, -0.38f), 1e-4f);
        }
    }

    [Fact]
    public void A_model_file_is_scaled_to_its_dimensions_and_gltf_wins_over_3ds()
    {
        // a 3DS box from its corner, 1 x 2 x 0.5 m, as a 0.5 x 0.5 x 0.25 m model: scaled about its origin
        const string models = """<Model Name="Body" File="body" Length="0.5" Width="0.5" Height="0.25" PrimitiveType="Undefined"/>""";
        const string tree = """<Geometry Name="Base" Model="Body" Position="{1,0,0,0}{0,1,0,0}{0,0,1,0.1}{0,0,0,1}"/>""";
        var files = new Dictionary<string, byte[]> { ["models/3ds/body.3ds"] = MvrSceneTests.Box3ds(1000, 2000, 500, red: true) };
        var body = GdtfBody.Read(Ft(models, tree), null, null, null, n => files.GetValueOrDefault(n))!;
        var b = Box(body.Base!);
        Near(new Vector3(0, 0, 0.1f), b.Position);
        Near(new Vector3(0.5f, 0.5f, 0.25f), b.Size);
        Assert.Equal(Color.Color8(255, 0, 0), body.Base!.Color);
        files["models/gltf/body.glb"] = MvrSceneTests.Glb(MvrSceneTests.Triangle(), "{}", null);
        Assert.Equal(1, GdtfBody.Read(Ft(models, tree), null, null, null, n => files.GetValueOrDefault(n))!.Base!.Triangles);
    }

    [Fact]
    public void Primitives_are_low_poly_and_of_their_size()
    {
        var size = new Vector3(0.26f, 0.2f, 0.4f);
        foreach (var p in new[] { "Cube", "Cylinder", "Sphere", "Base", "Base1_1", "Yoke", "Head", "Scanner", "Scanner1_1", "Conventional", "Conventional1_1", "Pigtail" })
        {
            var m = GdtfBody.Primitive(p, size);
            Assert.NotNull(m);
            Assert.InRange(m!.Triangles, 12, 120);
            var box = Box(m);
            Assert.True(box.Size.DistanceTo(size) < 0.01f, $"{p}: {box.Size}");
        }
        Assert.Null(GdtfBody.Primitive("Undefined", size));
    }

    [Fact]
    public void A_heavy_part_is_simplified_and_nothing_drawable_is_no_body()
    {
        var many = string.Concat(Enumerable.Range(0, 300).Select(i => FormattableString.Invariant(
            $"<Geometry Name=\"C{i}\" Model=\"Cell\" Position=\"{{1,0,0,{(i % 20) * 0.03f:0.00}}}{{0,1,0,{(i / 20) * 0.03f:0.00}}}{{0,0,1,0}}{{0,0,0,1}}\"/>")));
        var body = GdtfBody.Read(Ft("""<Model Name="Cell" Length="0.02" Width="0.02" Height="0.02" PrimitiveType="Sphere"/>""",
            $"<Geometry Name=\"Base\">{many}</Geometry>"), null, null, null, _ => null)!;
        Assert.InRange(body.Base!.Triangles, 1, GdtfBody.MaxPartTriangles);
        // Capture's stripped profiles: a Base of no model
        Assert.Null(GdtfBody.Read(Ft("""<Model Name="Base"/>""", """<Geometry Model="Base" Name="Base"/>"""), null, null, null, _ => null));
        // junk: never a throw
        Assert.Null(GdtfBody.Read(Ft("""<Model Name="B" Length="x" PrimitiveType="Cube"/>""", """<Geometry Model="B" Position="{a}"/>"""), null, null, null, _ => null));
    }

    [Fact]
    public void A_gdtf_read_carries_its_body_but_ours_keeps_the_rigs_look()
    {
        var desc = $"""
            <GDTF DataVersion="1.2"><FixtureType Name="Beskyd" Manufacturer="BlenderDMX"><Models>{Models}</Models><Geometries>{Tree}</Geometries>
            <DMXModes><DMXMode Name="Default" Geometry="Base"><DMXChannels>
              <DMXChannel DMXBreak="1" Offset="1" Geometry="Yoke"><LogicalChannel Attribute="Pan"><ChannelFunction PhysicalFrom="-270" PhysicalTo="270"/></LogicalChannel></DMXChannel>
              <DMXChannel DMXBreak="1" Offset="2" Geometry="Head"><LogicalChannel Attribute="Tilt"><ChannelFunction PhysicalFrom="-135" PhysicalTo="135"/></LogicalChannel></DMXChannel>
              <DMXChannel DMXBreak="1" Offset="3" Geometry="Beam"><LogicalChannel Attribute="Dimmer"/></DMXChannel>
            </DMXChannels></DMXMode></DMXModes></FixtureType></GDTF>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("description.xml").Open(), new UTF8Encoding(false)))
            w.Write(desc);
        var g = Gdtf.Read(ms.ToArray(), null, out string? error);
        Assert.True(g != null, error);
        Assert.NotNull(g!.Body?.Head);
        Near(new Vector3(0, 0, -0.38f), g.Body!.HeadPivot);
        var ours = Gdtf.Read(Gdtf.Write(FixtureType.Spot), null, out error);
        Assert.True(ours != null, error);
        Assert.Null(ours!.Body);
    }

    // ---- on the wire --------------------------------------------------------------------------------

    private static RigData Rig()
    {
        var rig = new RigData { Source = "t" };
        rig.Types.Add(RigData.Row(FixtureType.Spot));
        rig.Fixtures.Add(new RigData.FixtureRow { Name = "a", Type = 0, Pos = new float[] { 0, 10, 0 }, Up = new float[] { 0, -1, 0 }, Home = new float[] { 0, 0, 1 } });
        return rig;
    }

    [Fact]
    public void Bodies_travel_shared_and_checked()
    {
        var body = GdtfBody.Read(Ft(Models, Tree), null, "Yoke", "Head", _ => null)!;
        var rig = Rig();
        Assert.True(rig.AddBody(rig.Types[0], body));
        var second = RigData.Row(FixtureType.Wash);
        rig.Types.Add(rig.WithBody(second, body));
        Assert.Equal(3, rig.Bodies.Count);   // the same parts, shared
        Assert.Equal(rig.Types[0].Body!.Head, second.Body!.Head);
        Assert.Null(rig.Check());

        var back = RigData.Unpack(rig.Pack(), out string? error);
        Assert.True(back != null, error);
        Assert.Equal(3, back!.Bodies.Count);
        Near(new Vector3(0, 0, -0.38f), RigData.V(back.Types[0].Body!.HeadPivot));
        var head = RigData.Positions(back.Bodies[back.Types[0].Body!.Head]);
        Assert.Contains(head, p => p.DistanceTo(new Vector3(0.112f, 0.093f, -0.258f)) < 0.001f);

        back.Types[0].Body!.Yoke = 7;
        Assert.Contains("no mesh", back.Check());
        back.Types[0].Body!.Yoke = 1;
        back.Types[0].Body!.BeamAt = new float[] { 0, 0, -50 };
        Assert.Contains("pivot", back.Check());
        back.Types[0].Body!.BeamAt = new float[] { 0, 0, -0.5f };
        back.Bodies[0].Indices = back.Bodies[0].Indices.Concat(Enumerable.Repeat((byte)0, 6 * RigData.MaxBodyTriangles)).ToArray();
        Assert.Contains("triangles", back.Check());
    }

    [Fact]
    public void The_body_budget_leaves_a_type_on_the_generic_head()
    {
        var rig = Rig();
        int added = 0;
        for (int i = 0; i < 400; i++)
        {
            // a different body each time: none shared
            var body = GdtfBody.Read(Ft(Models.Replace("0.353", (0.3f + i * 0.0005f).ToString(System.Globalization.CultureInfo.InvariantCulture)), Tree), null, "Yoke", "Head", _ => null)!;
            if (rig.AddBody(RigData.Row(FixtureType.Spot), body)) added++;
        }
        Assert.InRange(added, 1, 399);
        Assert.True(rig.Bodies.Sum(b => b.Bytes) <= RigData.MaxBodyBytes);
        Assert.True(rig.Bodies.Count <= RigData.MaxBodyMeshes);
        Assert.Null(rig.Check());
    }

    /// <summary>Real files, when <c>MVR_SAMPLES</c> (MVRs) or <c>GDTF_SAMPLES</c> (GDTFs) name their folders: what bodies they make.</summary>
    [Fact]
    public void Real_files_when_present()
    {
        if (System.Environment.GetEnvironmentVariable("MVR_SAMPLES") is { } mvrs && Directory.Exists(mvrs))
            foreach (var file in Directory.GetFiles(mvrs, "*.mvr"))
            {
                var import = MvrFile.Read(File.ReadAllBytes(file), Path.GetFileName(file), out string? error);
                if (import == null) { _out.WriteLine($"{Path.GetFileName(file)}: {error}"); continue; }
                var rig = import.Rig;
                Assert.Null(rig.Check());
                _out.WriteLine($"{Path.GetFileName(file)}: {rig.Types.Count(t => t.Body != null)} of {rig.Types.Count} types with bodies, " +
                    $"{rig.Bodies.Count} body meshes, {rig.Bodies.Sum(b => b.TriangleCount)} triangles, {rig.Bodies.Sum(b => b.Bytes)} bytes, {rig.Pack().Length} packed");
                foreach (var t in rig.Types.Where(t => t.Body != null))
                    _out.WriteLine($"  {t.Name}: " + string.Join(", ", new[] { ("base", t.Body!.Base), ("yoke", t.Body.Yoke), ("head", t.Body.Head) }
                        .Select(p => $"{p.Item1} {(p.Item2 < 0 ? "-" : rig.Bodies[p.Item2].TriangleCount + " tris " + RigData.ColorOf(rig.Bodies[p.Item2]).ToHtml(false))}"))
                        + $"; pivots {RigData.V(t.Body.YokePivot)} {RigData.V(t.Body.HeadPivot)}, beam {RigData.V(t.Body.BeamAt)}");
            }
        if (System.Environment.GetEnvironmentVariable("GDTF_SAMPLES") is { } gdtfs && Directory.Exists(gdtfs))
            foreach (var file in Directory.GetFiles(gdtfs, "*.gdtf"))
            {
                var g = Gdtf.Read(File.ReadAllBytes(file), null, out string? error);
                var b = g?.Body;
                _out.WriteLine($"{Path.GetFileName(file)}: {(g == null ? error : b == null ? "no body" : $"base {b.Base?.Triangles ?? 0}, yoke {b.Yoke?.Triangles ?? 0}, head {b.Head?.Triangles ?? 0} tris; pivots {b.YokePivot} {b.HeadPivot}, beam {b.BeamAt}")}");
            }
    }
}
