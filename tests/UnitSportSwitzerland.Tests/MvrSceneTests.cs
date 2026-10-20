using System.IO;
using System.IO.Compression;
using System.Text;
using Godot;
using UnitSport.Concert;
using UnitSport.Concert.Mvr;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>MVR scene objects (#958): 3DS and glTF read in .NET, Symdefs and nesting placed, caps kept, the wire round trip.</summary>
public class MvrSceneTests
{
    private readonly ITestOutputHelper _out;
    public MvrSceneTests(ITestOutputHelper output) => _out = output;

    // ---- the readers --------------------------------------------------------------------------------

    [Fact]
    public void A_3ds_is_read_in_millimetres_with_its_material_colour()
    {
        var mesh = MvrGeometry.Read("box.3ds", Box3ds(1000, 2000, 500, red: true), out string? error);
        Assert.True(mesh != null, error);
        Assert.Equal(12, mesh!.Triangles);
        Assert.Equal(8, mesh.Positions.Length);
        Assert.Contains(new Vector3(1, 2, 0.5f), mesh.Positions);   // mm to m, Z up as MVR
        Assert.Equal(Color.Color8(255, 0, 0), mesh.Color);
        // no extension is a 3DS too, as MVR says; no material is the default grey
        var plain = MvrGeometry.Read("box", Box3ds(10, 10, 10, red: false), out error);
        Assert.True(plain != null, error);
        Assert.Equal(MvrGeometry.DefaultColor, plain!.Color);
    }

    [Fact]
    public void A_glb_is_y_up_metres_turned_z_up_through_its_nodes()
    {
        // one triangle at glTF (0,0,0) (1,0,0) (0,1,0), its node moved 2 m along glTF Z and scaled twice
        var glb = Glb(Triangle(), """{"translation":[0,0,2],"scale":[2,2,2]}""", "[0.2140411,0.2140411,0.2140411,1]");
        var mesh = MvrGeometry.Read("tri.glb", glb, out string? error);
        Assert.True(mesh != null, error);
        Assert.Equal(1, mesh!.Triangles);
        // glTF (x, y, z) is MVR (x, -z, y)
        Near(new Vector3(0, -2, 0), mesh.Positions[0]);
        Near(new Vector3(2, -2, 0), mesh.Positions[1]);
        Near(new Vector3(0, -2, 2), mesh.Positions[2]);
        Assert.InRange(mesh.Color.R, 0.49f, 0.51f);   // linear 0.214 is sRGB 0.5
    }

    [Fact]
    public void A_gltf_reads_its_buffer_from_a_data_uri_or_the_archive()
    {
        var bin = Triangle();
        string Json(string uri) => $$"""
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":{{bin.Length}},"uri":"{{uri}}"}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],"nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}]}
            """;
        var inline = MvrGeometry.Read("a.gltf", Text(Json("data:application/octet-stream;base64," + Convert.ToBase64String(bin))), out string? error);
        Assert.True(inline != null, error);
        Assert.Equal(1, inline!.Triangles);
        var beside = MvrGeometry.Read("b.gltf", Text(Json("b%20data.bin")), n => n == "b data.bin" ? bin : null, out error);
        Assert.True(beside != null, error);
        Assert.Null(MvrGeometry.Read("c.gltf", Text(Json("missing.bin")), _ => null, out error));
        Assert.Contains("not in the file", error);
    }

    [Fact]
    public void Broken_or_forbidden_files_are_an_error_not_a_crash()
    {
        Assert.Null(MvrGeometry.Read("x.3ds", new byte[] { 1, 2, 3 }, out string? error));
        Assert.NotNull(error);
        Assert.Null(MvrGeometry.Read("x.glb", Encoding.ASCII.GetBytes("glTF\u0002\0\0\0garbage-garbage"), out error));
        Assert.Null(MvrGeometry.Read("x.obj", new byte[10], out error));
        var draco = Glb(Triangle(), "{}", null, required: "\"extensionsRequired\":[\"KHR_draco_mesh_compression\"],");
        Assert.Null(MvrGeometry.Read("d.glb", draco, out error));
        Assert.Contains("KHR_draco", error);
        // a face pointing past its vertices is dropped, a chunk running past the file ends it
        var box = Box3ds(10, 10, 10, red: false);
        MvrGeometry.Read("cut.3ds", box[..^20], out _);
        var rnd = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            var junk = (byte[])box.Clone();
            junk[rnd.Next(junk.Length)] = (byte)rnd.Next(256);
            MvrGeometry.Read("junk.3ds", junk, out _);
        }
    }

    // ---- the scene ----------------------------------------------------------------------------------

    /// <summary>
    /// A Symdef (a 3DS box with its own matrix) used by 200 trusses in a GroupObject raised 6 m, a
    /// SceneObject with a GLB, a FocusPoint, and a Symbol of a missing Symdef.
    /// </summary>
    private static byte[] Scene(int trusses = 200)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < trusses; i++)
            sb.Append($"<Truss name=\"T{i}\" uuid=\"T{i}\"><Matrix>{{1,0,0}}{{0,1,0}}{{0,0,1}}{{{i * 1000},0,0}}</Matrix>")
              .Append("<Geometries><Symbol uuid=\"S").Append(i).Append("\" symdef=\"SD1\"/></Geometries></Truss>");
        var wash = FixtureType.Wash;
        string scene = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GeneralSceneDescription verMajor="1" verMinor="6">
              <Scene>
                <AUXData><Symdef uuid="SD1" name="truss 1 m"><ChildList>
                  <Geometry3D fileName="box.3ds"><Matrix>{"{1,0,0}{0,1,0}{0,0,1}{0,0,100}"}</Matrix></Geometry3D>
                </ChildList></Symdef></AUXData>
                <Layers><Layer name="Rig" uuid="L1"><ChildList>
                  <GroupObject uuid="G1" name="grid"><Matrix>{"{1,0,0}{0,1,0}{0,0,1}{0,0,6000}"}</Matrix><ChildList>{sb}</ChildList></GroupObject>
                  <SceneObject uuid="O1" name="riser"><Matrix>{"{0,1,0}{-1,0,0}{0,0,1}{0,-3000,0}"}</Matrix>
                    <Geometries><Geometry3D fileName="riser.glb"/><Symbol uuid="S-1" symdef="NOPE"/></Geometries></SceneObject>
                  <FocusPoint uuid="P1" name="A"><Geometries><Geometry3D fileName="box.3ds"/></Geometries></FocusPoint>
                  <Fixture name="Wash 1" uuid="F1"><Matrix>{"{1,0,0}{0,1,0}{0,0,1}{0,0,6000}"}</Matrix>
                    <GDTFSpec>{Gdtf.FileName(wash)}</GDTFSpec><GDTFMode>{Gdtf.ModeName}</GDTFMode>
                    <Addresses><Address break="0">1</Address></Addresses></Fixture>
                  <SceneObject uuid="H1" name="hall"><Geometries><Geometry3D fileName="box.3ds"><Matrix>{"{40,0,0}{0,1,0}{0,0,1}{0,0,0}"}</Matrix></Geometry3D></Geometries></SceneObject>
                </ChildList></Layer>
                <Layer name="Venue" uuid="L2"><ChildList>
                  <SceneObject uuid="V1" name="chair"><Geometries><Geometry3D fileName="box.3ds"/></Geometries></SceneObject>
                </ChildList></Layer></Layers>
              </Scene>
            </GeneralSceneDescription>
            """;
        return Zip(("GeneralSceneDescription.xml", Text(scene)), (Gdtf.FileName(wash), Gdtf.Write(wash)),
            ("box.3ds", Box3ds(1000, 300, 300, red: true)), ("riser.glb", Glb(Triangle(), "{}", null)));
    }

    [Fact]
    public void Symdefs_and_nesting_place_every_piece_and_share_one_mesh()
    {
        var import = MvrFile.Read(Scene(), "scene.mvr", out string? error);
        Assert.True(import != null, error);
        var rig = import!.Rig;
        Assert.Single(rig.Fixtures);
        Assert.Equal(2, rig.Meshes.Count);                 // the box (trusses and the focus point) and the riser
        Assert.Equal(202, rig.Objects.Count);
        Assert.Equal(201, rig.Objects.Count(o => o.Mesh == rig.Objects[0].Mesh));
        Assert.Contains(import.Notes, n => n.Contains("no Symdef"));
        Assert.Contains(import.Notes, n => n.Contains("202 pieces of 2 meshes"));
        Assert.Contains(import.Notes, n => n.StartsWith("2 venue piece(s) not drawn"));   // the 40 m hall and the Venue layer's chair

        // truss 3: group 6 m up, its own 3 m along MVR X, the symdef's geometry 0.1 m up; the box's far
        // corner (1, 0.3, 0.3) m: in ours (x, z, -y)
        var mesh = rig.Meshes[rig.Objects[3].Mesh];
        var far = RigData.Positions(mesh).OrderByDescending(p => p.X + p.Y - p.Z).First();
        Near(new Vector3(4f, 6.4f, -0.3f), RigData.X(rig.Objects[3].Xf) * far, 0.001f);

        // the riser turned a quarter about Z: glTF (1,0,0) is MVR (1,0,0), turned to MVR (0,1,0), 3 m downstage
        int riser = rig.Objects.Single(o => rig.Meshes[o.Mesh].TriangleCount == 1).Mesh;
        var rx = RigData.X(rig.Objects.Single(o => o.Mesh == riser).Xf);
        var corner = RigData.Positions(rig.Meshes[riser]).OrderByDescending(p => p.X).First();
        Near(new Vector3(0, 0, 3f - 1f), rx * corner, 0.001f);

        Assert.Null(rig.Check());
        var back = RigData.Unpack(rig.Pack(), out error);
        Assert.True(back != null, error);
        Assert.Equal(202, back!.Objects.Count);
        Assert.Equal(RigData.Indices(rig.Meshes[0]), RigData.Indices(back.Meshes[0]));
    }

    [Fact]
    public void Our_own_file_does_not_draw_its_stage_twice()
    {
        var rig = new RigData { Source = "t" };
        rig.Types.Add(RigData.Row(FixtureType.Wash));
        rig.Fixtures.Add(new RigData.FixtureRow { Name = "w", Pos = new[] { 0f, 10f, 0f }, Up = new[] { 0f, -1f, 0f }, Home = new[] { 0f, 0f, 1f } });
        var mvr = MvrFile.Write(rig, "ours", Glb(Triangle(), "{}", null));
        var import = MvrFile.Read(mvr, "ours.mvr", out string? error);
        Assert.True(import != null, error);
        Assert.True(import!.OursStadiumFrame);
        Assert.Empty(import.Rig.Objects);
    }

    [Fact]
    public void Heavy_meshes_are_simplified_to_the_caps_and_the_server_refuses_a_broken_one()
    {
        // a 120x120 grid of 2 triangles a cell = 28800 triangles, over the cap of one mesh
        var grid = Grid(120);
        Assert.True(grid.Triangles > RigData.MaxMeshTriangles);
        var small = MvrFile.Decimate(grid, 5000);
        Assert.InRange(small.Triangles, 1000, 5000);

        var scene = Encoding.UTF8.GetString(Unzip(Scene(2), "GeneralSceneDescription.xml")).Replace("riser.glb", "grid.glb");
        var mvr = Zip(("GeneralSceneDescription.xml", Text(scene)), (Gdtf.FileName(FixtureType.Wash), Gdtf.Write(FixtureType.Wash)),
            ("box.3ds", Box3ds(1000, 300, 300, red: true)), ("grid.glb", Glb(GridBin(grid), "{}", null, grid.Positions.Length, grid.Indices.Length)));
        var import = MvrFile.Read(mvr, "grid.mvr", out string? error);
        Assert.True(import != null, error);
        Assert.Contains(import!.Notes, n => n.Contains("simplified"));
        Assert.All(import.Rig.Meshes, m => Assert.True(m.TriangleCount <= RigData.MaxMeshTriangles));
        Assert.Null(import.Rig.Check());

        var rig = import.Rig;
        rig.Meshes[0].Indices[0] = 0xFF; rig.Meshes[0].Indices[1] = 0xFF;   // an index past the vertices
        Assert.Contains("past its vertices", rig.Check());
        rig.Meshes[0].Indices[0] = 0; rig.Meshes[0].Indices[1] = 0;
        rig.Objects[0].Xf[9] = 5000f;
        Assert.Contains("placed", rig.Check());
        rig.Objects[0].Xf[9] = 0f;
        rig.Objects[0].Mesh = 99;
        Assert.NotNull(rig.Check());
    }

    [Fact]
    public void Objects_move_with_the_stage_frame()
    {
        var rig = new RigData();
        rig.Objects.Add(new RigData.ObjectRow { Mesh = 0, Xf = RigData.X(new Transform3D(Basis.Identity, new Vector3(1, 2, 3))) });
        // the stage turned half round and 50 m down the pitch
        rig.MoveObjects(new Transform3D(new Basis(Vector3.Up, MathF.PI), new Vector3(0, 0, 50)));
        var x = RigData.X(rig.Objects[0].Xf);
        Near(new Vector3(-1, 2, 47), x.Origin, 0.0001f);
        Near(new Vector3(-1, 0, 0), x.Basis.Column0, 0.0001f);
    }

    /// <summary>The python-mvr test files (not committed), when <c>MVR_SAMPLES</c> names their folder: what they make.</summary>
    [Fact]
    public void Real_files_when_present()
    {
        string? dir = System.Environment.GetEnvironmentVariable("MVR_SAMPLES");
        if (dir == null || !Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.mvr"))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var import = MvrFile.Read(File.ReadAllBytes(file), Path.GetFileName(file), out string? error);
            if (import == null) { _out.WriteLine($"{Path.GetFileName(file)}: {error}"); continue; }
            var rig = import.Rig;
            Assert.Null(rig.Check());
            var packed = rig.Pack();
            Assert.True(packed.Length <= RigData.MaxBytes, $"{packed.Length} bytes packed");
            _out.WriteLine($"{Path.GetFileName(file)}: {rig.Fixtures.Count} fixtures, {rig.Objects.Count} objects, {rig.Meshes.Count} meshes, " +
                $"{rig.Meshes.Sum(m => m.TriangleCount)} triangles, {rig.Meshes.Sum(m => m.Bytes)} mesh bytes, {packed.Length} packed, {sw.ElapsedMilliseconds} ms");
            foreach (var n in import.Notes) _out.WriteLine("  " + n);
        }
    }

    // ---- files made byte by byte --------------------------------------------------------------------

    private static void Near(Vector3 want, Vector3 got, float tol = 0.0001f) =>
        Assert.True(want.DistanceTo(got) < tol, $"want {want}, got {got}");

    private static byte[] Chunk(ushort id, params byte[][] body)
    {
        var all = body.SelectMany(b => b).ToArray();
        return BitConverter.GetBytes(id).Concat(BitConverter.GetBytes(all.Length + 6)).Concat(all).ToArray();
    }

    /// <summary>A box from the origin to (x, y, z) mm, optionally with a red material on all its faces.</summary>
    internal static byte[] Box3ds(float x, float y, float z, bool red)
    {
        var v = new List<byte>(BitConverter.GetBytes((ushort)8));
        for (int i = 0; i < 8; i++)
            foreach (float f in new[] { (i & 1) * x, (i >> 1 & 1) * y, (i >> 2 & 1) * z }) v.AddRange(BitConverter.GetBytes(f));
        int[] faces = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
        var f3 = new List<byte>(BitConverter.GetBytes((ushort)12));
        for (int i = 0; i < 12; i++)
        {
            for (int k = 0; k < 3; k++) f3.AddRange(BitConverter.GetBytes((ushort)faces[i * 3 + k]));
            f3.AddRange(BitConverter.GetBytes((ushort)7));
        }
        var name = Encoding.ASCII.GetBytes("Red\0");
        var faceMat = new List<byte>(name);
        faceMat.AddRange(BitConverter.GetBytes((ushort)12));
        for (int i = 0; i < 12; i++) faceMat.AddRange(BitConverter.GetBytes((ushort)i));
        var faceList = Chunk(0x4120, f3.ToArray(), red ? Chunk(0x4130, faceMat.ToArray()) : Array.Empty<byte>());
        var obj = Chunk(0x4000, Encoding.ASCII.GetBytes("Box\0"), Chunk(0x4100, Chunk(0x4110, v.ToArray()), faceList));
        var mat = Chunk(0xAFFF, Chunk(0xA000, name), Chunk(0xA020, Chunk(0x0011, new byte[] { 255, 0, 0 })));
        return Chunk(0x4D4D, Chunk(0x3D3D, red ? mat : Array.Empty<byte>(), obj));
    }

    /// <summary>Three float positions then three u16 indices (padded to 4).</summary>
    internal static byte[] Triangle()
    {
        var b = new List<byte>();
        foreach (float f in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) b.AddRange(BitConverter.GetBytes(f));
        foreach (ushort i in new ushort[] { 0, 1, 2, 0 }) b.AddRange(BitConverter.GetBytes(i));
        return b.ToArray();
    }

    private static MvrMesh Grid(int n)
    {
        var p = new List<Vector3>();
        var idx = new List<int>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++) p.Add(new Vector3(i * 0.05f, MathF.Sin(i * 0.3f) * 0.2f, j * 0.05f));
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i;
                idx.AddRange(new[] { a, a + 1, a + n + 2, a, a + n + 2, a + n + 1 });
            }
        return new MvrMesh(p.ToArray(), idx.ToArray(), Colors.White);
    }

    private static byte[] GridBin(MvrMesh m)
    {
        var b = new List<byte>();
        foreach (var v in m.Positions) { b.AddRange(BitConverter.GetBytes(v.X)); b.AddRange(BitConverter.GetBytes(v.Y)); b.AddRange(BitConverter.GetBytes(v.Z)); }
        foreach (int i in m.Indices) b.AddRange(BitConverter.GetBytes((uint)i));
        return b.ToArray();
    }

    /// <summary>A GLB of one mesh: positions then indices (u16 for the triangle, u32 for a grid) in its BIN chunk.</summary>
    internal static byte[] Glb(byte[] bin, string node, string? colour, int verts = 3, int corners = 3, string required = "")
    {
        int posBytes = verts * 12;
        int indexType = verts == 3 ? 5123 : 5125;
        string material = colour == null ? "" : ",\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorFactor\":" + colour + "}}]";
        string json = $$"""
            {"asset":{"version":"2.0"},{{required}}"buffers":[{"byteLength":{{bin.Length}}}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":{{posBytes}}},{"buffer":0,"byteOffset":{{posBytes}},"byteLength":{{bin.Length - posBytes}}}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":{{verts}},"type":"VEC3"},{"bufferView":1,"componentType":{{indexType}},"count":{{corners}},"type":"SCALAR"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1{{(colour == null ? "" : ",\"material\":0")}}}]}],
             "nodes":[{{node.TrimEnd('}')}}{{(node == "{}" ? "" : ",")}}"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0{{material}}}
            """;
        var j = Encoding.UTF8.GetBytes(json).ToList();
        while (j.Count % 4 != 0) j.Add((byte)' ');
        var b = bin.ToList();
        while (b.Count % 4 != 0) b.Add(0);
        var glb = new List<byte>();
        glb.AddRange(BitConverter.GetBytes(0x46546C67u));
        glb.AddRange(BitConverter.GetBytes(2u));
        glb.AddRange(BitConverter.GetBytes((uint)(12 + 8 + j.Count + 8 + b.Count)));
        glb.AddRange(BitConverter.GetBytes((uint)j.Count)); glb.AddRange(BitConverter.GetBytes(0x4E4F534Au)); glb.AddRange(j);
        glb.AddRange(BitConverter.GetBytes((uint)b.Count)); glb.AddRange(BitConverter.GetBytes(0x004E4942u)); glb.AddRange(b);
        return glb.ToArray();
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in files)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(bytes, 0, bytes.Length);
            }
        return ms.ToArray();
    }

    private static byte[] Unzip(byte[] zip, string name)
    {
        using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var s = z.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);
}
