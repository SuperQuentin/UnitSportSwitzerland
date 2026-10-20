using System.Buffers.Binary;
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

/// <summary>
/// Imported 3D's own looks (#973): PNGs read and shrunk in .NET, 3DS faces' materials and texture
/// maps, glTF primitives' colours and textures, the vertex split, decimation keeping them, the wire's
/// colours, coordinates, runs and textures, their caps and budget.
/// </summary>
public class MvrTextureTests
{
    private readonly ITestOutputHelper _out;
    public MvrTextureTests(ITestOutputHelper output) => _out = output;

    // ---- images -------------------------------------------------------------------------------------

    [Fact]
    public void A_png_round_trips_and_a_large_one_is_box_filtered_to_64()
    {
        var px = Pixels(3, 2, (x, y) => ((byte)(x * 100), (byte)(y * 200), 7));
        var back = MvrImages.DecodeSmall(MvrImages.EncodePng(3, 2, px));
        Assert.NotNull(back);
        Assert.Equal((3, 2), (back!.Value.W, back.Value.H));
        Assert.Equal(px, back.Value.Rgb);

        // 200 x 100, left half red, right half blue: 64 x 32, the halves kept, the mean purple
        var big = MvrImages.EncodePng(200, 100, Pixels(200, 100, (x, _) => x < 100 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255)));
        var img = MvrImages.Shrink(big)!;
        Assert.Equal((64, 32), (img.Width, img.Height));
        Assert.InRange(img.Average.R, 0.49f, 0.51f);
        Assert.InRange(img.Average.B, 0.49f, 0.51f);
        var small = MvrImages.DecodeSmall(img.Png)!.Value;
        Assert.Equal(255, small.Rgb[0]);                      // top left red
        Assert.Equal(255, small.Rgb[(63) * 3 + 2]);           // top right blue
        Assert.True(img.Png.Length <= RigData.MaxTextureFileBytes);
    }

    [Fact]
    public void Pngs_of_every_filter_palette_and_depth_decode()
    {
        // filters Sub, Up, Average, Paeth on an RGB gradient
        var rgb = Pixels(5, 4, (x, y) => ((byte)(x * 40 + y), (byte)(y * 50), (byte)(x * y * 9)));
        for (int f = 1; f <= 4; f++)
        {
            var png = Png(5, 4, 8, 2, Filtered(rgb, 5, 4, 3, f), null);
            Assert.Equal(rgb, MvrImages.DecodeSmall(png)!.Value.Rgb);
        }
        // a 2-bit palette: entries 0..3 in a row
        var palette = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9 };
        var row = new byte[] { 0, 0b00_01_10_11 };
        var p = MvrImages.DecodeSmall(Png(4, 1, 2, 3, row, palette))!.Value;
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9 }, p.Rgb);
        // 16-bit grey with alpha: the high byte, grey on all three
        var g16 = MvrImages.DecodeSmall(Png(1, 1, 16, 4, new byte[] { 0, 0x80, 0x01, 0xFF, 0xFF }, null))!.Value;
        Assert.Equal(new byte[] { 0x80, 0x80, 0x80 }, g16.Rgb);
        // junk is null, never an exception
        Assert.Null(MvrImages.Shrink(Encoding.ASCII.GetBytes("not an image")));
        var broken = MvrImages.EncodePng(8, 8, new byte[192]);
        Assert.Null(MvrImages.Shrink(broken[..(broken.Length - 20)]));
        Assert.Null(MvrImages.DecodeSmall(MvrImages.EncodePng(65, 1, new byte[195])));   // over the size
    }

    // ---- 3DS ----------------------------------------------------------------------------------------

    [Fact]
    public void A_3ds_box_of_two_materials_splits_its_shared_corners()
    {
        var mesh = MvrGeometry.Read("box.3ds", Box3ds(twoColours: true, texture: null), out string? error);
        Assert.True(mesh != null, error);
        Assert.Equal(12, mesh!.Triangles);
        Assert.NotNull(mesh.Colors);
        Assert.True(mesh.Positions.Length > 8, $"{mesh.Positions.Length} vertices: corners two colours meet are split");
        Assert.Null(mesh.Uvs);
        // each triangle one colour on all its corners: the first six red, the rest blue
        for (int t = 0; t < 12; t++)
        {
            var want = t < 6 ? Colors.Red : Colors.Blue;
            for (int k = 0; k < 3; k++) Assert.Equal(want, mesh.ColorAt(mesh.Indices[t * 3 + k]));
        }
    }

    [Fact]
    public void A_3ds_texture_map_is_read_from_the_archive_through_its_mapping()
    {
        var png = MvrImages.EncodePng(2, 2, Pixels(2, 2, (x, y) => ((byte)(x * 255), (byte)(y * 255), 0)));
        var mesh = MvrGeometry.Read("box.3ds", Box3ds(twoColours: false, texture: "WOOD.PNG"),
            name => name.Equals("wood.png", StringComparison.OrdinalIgnoreCase) ? png : null, out string? error);
        Assert.True(mesh != null, error);
        Assert.Single(mesh!.Images!);
        Assert.All(mesh.TriangleImages!, i => Assert.Equal(0, i));
        // the texture replaces the diffuse colour; 3DS v is up, ours down
        Assert.All(mesh.Indices, i => Assert.Equal(Colors.White, mesh.ColorAt(i)));
        int corner = Array.FindIndex(mesh.Positions, p => p == Vector3.Zero);
        Assert.Equal(new Vector2(0, 1), mesh.Uvs![corner]);
        // no file: the material's colour, untextured
        var plain = MvrGeometry.Read("box.3ds", Box3ds(twoColours: false, texture: "WOOD.PNG"), _ => null, out _)!;
        Assert.Null(plain.Images);
        Assert.Equal(Colors.Red, plain.Color);
    }

    // ---- glTF ---------------------------------------------------------------------------------------

    [Fact]
    public void A_glb_keeps_each_primitive_colour_and_its_embedded_texture()
    {
        var png = MvrImages.EncodePng(4, 4, Pixels(4, 4, (x, y) => (200, 100, (byte)(x * 60))));
        var mesh = MvrGeometry.Read("two.glb", TwoPrimitives(png, external: false), out string? error);
        Assert.True(mesh != null, error);
        Assert.Equal(2, mesh!.Triangles);
        Assert.Equal(-1, mesh.ImageOf(0));                 // the green one, plain
        Assert.Equal(0, mesh.ImageOf(1));                  // the textured one
        Assert.InRange(mesh.ColorAt(mesh.Indices[0]).G, 0.99f, 1f);
        Assert.Equal(Colors.White, mesh.ColorAt(mesh.Indices[3]));   // a texture without a factor: white times it
        Assert.Equal(new Vector2(1, 0), mesh.Uvs![mesh.Indices[4]]);
        // the image as a file beside it in the archive
        var external = MvrGeometry.Read("two.glb", TwoPrimitives(png, external: true), uri => uri == "tex/a b.png" ? png : null, out error);
        Assert.True(external != null, error);
        Assert.Single(external!.Images!);
    }

    // ---- the wire -----------------------------------------------------------------------------------

    [Fact]
    public void A_row_carries_colours_coordinates_and_runs_and_the_server_checks_them()
    {
        var png = MvrImages.EncodePng(4, 4, Pixels(4, 4, (x, y) => (255, 255, 255)));
        var mesh = MvrGeometry.Read("two.glb", TwoPrimitives(png, external: false), out _)!;
        var rig = Rig();
        var row = RigData.Quantise(mesh, rig.AddTexture);
        Assert.Single(rig.Textures);
        Assert.Equal(new[] { -1, 1, 0, 1 }, row.Runs);     // untextured first
        Assert.NotNull(row.VertexColors);
        Assert.Equal(row.VertexCount * 4, row.Uvs!.Length);
        Assert.Equal(new[] { (-1, 0, 1), (0, 1, 1) }, RigData.RunsOf(row));
        var uv = RigData.UvsOf(row)!;
        Assert.Contains(uv, u => u.DistanceTo(new Vector2(1, 0)) < 1e-4f);
        Assert.Equal(Colors.Green, RigData.VertexColorsOf(row)[RigData.Indices(row)[0]]);
        rig.Meshes.Add(row);
        rig.Objects.Add(new RigData.ObjectRow { Mesh = 0, Xf = RigData.X(Transform3D.Identity) });
        Assert.Null(rig.Check());
        var back = RigData.Unpack(rig.Pack(), out string? error);
        Assert.True(back != null, error);
        Assert.Equal(rig.Textures[0], back!.Textures[0]);
        Assert.True(back.Meshes[0].Same(row));

        // what the server refuses
        string? Bad(Action<RigData> spoil)
        {
            var r = RigData.Unpack(rig.Pack(), out _)!;
            spoil(r);
            return r.Check();
        }
        Assert.NotNull(Bad(r => r.Meshes[0].Runs = new[] { 1, 2 }));                         // a texture it has not
        Assert.NotNull(Bad(r => r.Meshes[0].Runs = new[] { 0, 1 }));                         // runs short of its triangles
        Assert.NotNull(Bad(r => r.Meshes[0].Uvs = new byte[3]));                             // coordinates not one a vertex
        Assert.NotNull(Bad(r => r.Meshes[0].VertexColors = new byte[1]));
        Assert.NotNull(Bad(r => r.Meshes[0].UvMin = new[] { float.NaN, 0 }));
        Assert.NotNull(Bad(r => r.Meshes[0].Uvs = null));                                    // runs without coordinates
        Assert.NotNull(Bad(r => r.Textures[0] = MvrImages.EncodePng(65, 1, new byte[195])));   // over 64 a side
        Assert.NotNull(Bad(r => r.Textures[0] = Encoding.ASCII.GetBytes("junk")));
        Assert.NotNull(Bad(r => r.Textures.AddRange(Enumerable.Repeat(png, RigData.MaxTextures))));
    }

    [Fact]
    public void A_texture_past_the_budget_is_left_off_and_its_faces_take_its_mean()
    {
        var rig = Rig();
        // noisy 64 x 64 textures, each about 12 kB: the budget holds about twenty
        int added = 0, refused = 0;
        for (int i = 0; i < 40; i++)
        {
            var img = MvrImages.Shrink(MvrImages.EncodePng(64, 64, Noise(64, 64, i)))!;
            if (rig.AddTexture(img) >= 0) added++; else refused++;
        }
        Assert.True(refused > 0 && added > 5, $"{added} added, {refused} refused");
        Assert.True(rig.Textures.Sum(t => t.Length) <= RigData.MaxTextureBytes);
        // the same PNG twice is one texture
        Assert.Equal(0, rig.AddTexture(new MvrImage(64, 64, rig.Textures[0], Colors.White)));

        var red = MvrImages.Shrink(MvrImages.EncodePng(2, 2, Pixels(2, 2, (_, _) => (255, 0, 0))))!;
        var mesh = new MvrMesh(new[] { Vector3.Zero, Vector3.Right, Vector3.Up }, new[] { 0, 1, 2 }, Colors.White)
        {
            Uvs = new[] { Vector2.Zero, Vector2.Right, Vector2.Down }, TriangleImages = new[] { 0 }, Images = new[] { red },
        };
        var row = RigData.Quantise(mesh, _ => -1);
        Assert.Null(row.Runs);
        Assert.Null(row.Uvs);
        Assert.Equal(Colors.Red, RigData.VertexColorsOf(row)[0]);
    }

    [Fact]
    public void An_import_packs_textures_in_its_budget_and_notes_the_rest()
    {
        // thirty pieces, each its own glb with its own noisy texture
        var files = new List<(string, byte[])>();
        var sb = new StringBuilder();
        for (int i = 0; i < 30; i++)
        {
            files.Add(($"p{i}.glb", TwoPrimitives(MvrImages.EncodePng(64, 64, Noise(64, 64, i + 100)), external: false)));
            sb.Append($"<SceneObject uuid=\"O{i}\" name=\"p{i}\"><Matrix>{{1,0,0}}{{0,1,0}}{{0,0,1}}{{{i * 1500},0,0}}</Matrix><Geometries><Geometry3D fileName=\"p{i}.glb\"/></Geometries></SceneObject>");
        }
        var import = MvrFile.Read(Mvr(sb.ToString(), files.ToArray()), "tex.mvr", out string? error);
        Assert.True(import != null, error);
        var rig = import!.Rig;
        Assert.Null(rig.Check());
        foreach (var n in import.Notes) _out.WriteLine(n);
        Assert.InRange(rig.Textures.Count, 5, 29);
        Assert.Contains(import.Notes, n => n.Contains("left off past the budget"));
        Assert.Contains(import.Notes, n => n.Contains("texture(s) on the objects"));
        // every mesh drawn: textured ones with runs, the rest by colour
        Assert.Equal(30, rig.Meshes.Count);
        Assert.Equal(rig.Textures.Count, rig.Meshes.Count(m => m.Runs != null));
        Assert.True(rig.Pack().Length < RigData.MaxBytes);
    }

    // ---- simplified and bodies ----------------------------------------------------------------------

    [Fact]
    public void Decimating_keeps_colour_seams_and_textures()
    {
        // a strip of 400 triangles, left half red, right half textured
        var red = MvrImages.Shrink(MvrImages.EncodePng(1, 1, new byte[] { 0, 0, 255 }))!;
        var pos = new List<Vector3>();
        var cols = new List<Color>();
        var uvs = new List<Vector2>();
        var idx = new List<int>();
        var img = new List<int>();
        for (int i = 0; i < 200; i++)
        {
            bool left = i < 100;
            int at = pos.Count;
            foreach (var (x, y) in new[] { (i, 0), (i + 1, 0), (i + 1, 1), (i, 1) })
            {
                pos.Add(new Vector3(x * 0.01f, y * 0.5f, 0));
                cols.Add(left ? Colors.Red : Colors.White);
                uvs.Add(new Vector2(x / 200f, y));
            }
            idx.AddRange(new[] { at, at + 1, at + 2, at, at + 2, at + 3 });
            img.Add(left ? -1 : 0); img.Add(left ? -1 : 0);
        }
        var mesh = new MvrMesh(pos.ToArray(), idx.ToArray(), Colors.White) { Colors = cols.ToArray(), Uvs = uvs.ToArray(), TriangleImages = img.ToArray(), Images = new[] { red } };
        var small = MvrFile.Decimate(mesh, 60);
        Assert.InRange(small.Triangles, 1, 60);
        Assert.Equal(small.Triangles, small.TriangleImages!.Length);
        Assert.Contains(0, small.TriangleImages);
        Assert.Contains(-1, small.TriangleImages);
        // a plain triangle is red on all corners, a textured one white
        for (int t = 0; t < small.Triangles; t++)
            for (int k = 0; k < 3; k++)
                Assert.Equal(small.ImageOf(t) < 0 ? Colors.Red : Colors.White, small.ColorAt(small.Indices[t * 3 + k]));
    }

    [Fact]
    public void A_gdtf_body_keeps_its_model_textures_and_shares_them_in_the_rig()
    {
        var png = MvrImages.EncodePng(2, 2, Pixels(2, 2, (x, y) => ((byte)(x * 255), 0, 0)));
        var files = new Dictionary<string, byte[]>
        {
            ["models/3ds/body.3ds"] = Box3ds(twoColours: false, texture: "skin.png"),
            ["models/3ds/skin.png"] = png,
        };
        var ft = XElement.Parse("""
            <FixtureType><Models><Model Name="Body" File="body" Length="0.3" Width="0.3" Height="0.4"/></Models>
            <Geometries><Geometry Name="Body" Model="Body"/></Geometries></FixtureType>
            """);
        var body = GdtfBody.Read(ft, null, null, null, n => files.GetValueOrDefault(n))!;
        Assert.NotNull(body.Base!.Images);
        var rig = Rig();
        var row = new RigData.TypeRow { Name = "T", Channels = new[] { 0 } };
        Assert.True(rig.AddBody(row, body));
        Assert.Single(rig.Textures);
        Assert.NotNull(rig.Bodies[0].Runs);
        Assert.True(rig.AddBody(new RigData.TypeRow { Name = "U", Channels = new[] { 0 } }, body));
        Assert.Single(rig.Bodies);                                   // the same part, shared
        Assert.Single(rig.Textures);
    }

    /// <summary>The python-mvr test files (not committed), when <c>MVR_SAMPLES</c> names their folder: their textures.</summary>
    [Fact]
    public void Real_files_when_present()
    {
        string? dir = System.Environment.GetEnvironmentVariable("MVR_SAMPLES");
        if (dir == null || !Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.mvr"))
        {
            var import = MvrFile.Read(File.ReadAllBytes(file), Path.GetFileName(file), out string? error);
            if (import == null) { _out.WriteLine($"{Path.GetFileName(file)}: {error}"); continue; }
            var rig = import.Rig;
            Assert.Null(rig.Check());
            _out.WriteLine($"{Path.GetFileName(file)}: {rig.Textures.Count} textures ({rig.Textures.Sum(t => t.Length)} bytes), " +
                $"{rig.Meshes.Count(m => m.VertexColors != null)} of {rig.Meshes.Count} meshes coloured per face, {rig.Meshes.Count(m => m.Runs != null)} textured; " +
                $"{rig.Bodies.Count(m => m.VertexColors != null)} of {rig.Bodies.Count} body meshes coloured per face, {rig.Bodies.Count(m => m.Runs != null)} textured; " +
                $"{rig.Pack().Length} packed");
            foreach (var n in import.Notes.Where(n => n.Contains("texture") || n.Contains("scene objects"))) _out.WriteLine("  " + n);
        }
        // the textured glbs read directly: Vectorworks' room pieces are the venue, so not drawn
        string so = Path.Combine(dir, "scene_objects.mvr");
        if (!File.Exists(so)) return;
        using var zip = new ZipArchive(File.OpenRead(so));
        int textured = 0;
        foreach (var e in zip.Entries.Where(e => e.Name.EndsWith(".glb")))
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var mesh = MvrGeometry.Read(e.Name, ms.ToArray(), GdtfBody.Files(zip), out _);
            if (mesh?.Images is { Length: > 0 } ims)
            {
                textured++;
                if (textured <= 3) _out.WriteLine($"  {e.Name}: {ims[0].Width}x{ims[0].Height}, {ims[0].Png.Length} bytes, mean {ims[0].Average}");
            }
        }
        _out.WriteLine($"scene_objects.mvr: {textured} textured glbs read");
    }

    // ---- files made byte by byte --------------------------------------------------------------------

    private static RigData Rig()
    {
        var rig = new RigData { Source = "t" };
        rig.Types.Add(RigData.Row(FixtureType.Wash));
        rig.Fixtures.Add(new RigData.FixtureRow { Name = "w", Pos = new[] { 0f, 10f, 0f }, Up = new[] { 0f, -1f, 0f }, Home = new[] { 0f, 0f, 1f } });
        return rig;
    }

    private static byte[] Pixels(int w, int h, Func<int, int, (byte, byte, byte)> at)
    {
        var px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = at(x, y);
                px[(y * w + x) * 3] = r; px[(y * w + x) * 3 + 1] = g; px[(y * w + x) * 3 + 2] = b;
            }
        return px;
    }

    private static byte[] Noise(int w, int h, int seed)
    {
        var px = new byte[w * h * 3];
        new Random(seed).NextBytes(px);
        return px;
    }

    /// <summary>Rows with a filter applied (bpp bytes a pixel), each led by its filter byte.</summary>
    private static byte[] Filtered(byte[] raw, int w, int h, int bpp, int filter)
    {
        int stride = w * bpp;
        var o = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            o.Add((byte)filter);
            for (int i = 0; i < stride; i++)
            {
                int x = raw[y * stride + i], a = i >= bpp ? raw[y * stride + i - bpp] : 0, b = y > 0 ? raw[(y - 1) * stride + i] : 0;
                int c = i >= bpp && y > 0 ? raw[(y - 1) * stride + i - bpp] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                int pred = filter switch { 1 => a, 2 => b, 3 => (a + b) >> 1, _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c };
                o.Add((byte)(x - pred));
            }
        }
        return o.ToArray();
    }

    /// <summary>A PNG of given rows (filter bytes included), no CRCs checked by the reader.</summary>
    private static byte[] Png(int w, int h, int depth, int type, byte[] rows, byte[]? palette)
    {
        var o = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        void Chunk(string id, byte[] data)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            o.AddRange(len); o.AddRange(Encoding.ASCII.GetBytes(id)); o.AddRange(data); o.AddRange(new byte[4]);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = (byte)depth; ihdr[9] = (byte)type;
        Chunk("IHDR", ihdr);
        if (palette != null) Chunk("PLTE", palette);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(rows);
        Chunk("IDAT", ms.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return o.ToArray();
    }

    private static byte[] Chunk(ushort id, params byte[][] body)
    {
        var all = body.SelectMany(b => b).ToArray();
        return BitConverter.GetBytes(id).Concat(BitConverter.GetBytes(all.Length + 6)).Concat(all).ToArray();
    }

    /// <summary>A 1 m box: its first six faces "Red", the rest "Blue" or Red too; Red's texture map, mapping coordinates (u = x, v = y).</summary>
    private static byte[] Box3ds(bool twoColours, string? texture)
    {
        var v = new List<byte>(BitConverter.GetBytes((ushort)8));
        var uv = new List<byte>(BitConverter.GetBytes((ushort)8));
        for (int i = 0; i < 8; i++)
        {
            foreach (float f in new[] { (i & 1) * 1000f, (i >> 1 & 1) * 1000f, (i >> 2 & 1) * 1000f }) v.AddRange(BitConverter.GetBytes(f));
            uv.AddRange(BitConverter.GetBytes((float)(i & 1))); uv.AddRange(BitConverter.GetBytes((float)(i >> 1 & 1)));
        }
        int[] faces = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
        var f3 = new List<byte>(BitConverter.GetBytes((ushort)12));
        for (int i = 0; i < 12; i++)
        {
            for (int k = 0; k < 3; k++) f3.AddRange(BitConverter.GetBytes((ushort)faces[i * 3 + k]));
            f3.AddRange(BitConverter.GetBytes((ushort)7));
        }
        byte[] Group(string name, int from, int to)
        {
            var g = new List<byte>(Encoding.ASCII.GetBytes(name + "\0"));
            g.AddRange(BitConverter.GetBytes((ushort)(to - from)));
            for (int i = from; i < to; i++) g.AddRange(BitConverter.GetBytes((ushort)i));
            return Chunk(0x4130, g.ToArray());
        }
        var faceList = twoColours ? Chunk(0x4120, f3.ToArray(), Group("Red", 0, 6), Group("Blue", 6, 12)) : Chunk(0x4120, f3.ToArray(), Group("Red", 0, 12));
        var obj = Chunk(0x4000, Encoding.ASCII.GetBytes("Box\0"), Chunk(0x4100, Chunk(0x4110, v.ToArray()), Chunk(0x4140, uv.ToArray()), faceList));
        byte[] Mat(string name, byte r, byte g, byte b, string? map) => Chunk(0xAFFF, Chunk(0xA000, Encoding.ASCII.GetBytes(name + "\0")),
            Chunk(0xA020, Chunk(0x0011, new[] { r, g, b })),
            map == null ? Array.Empty<byte>() : Chunk(0xA200, Chunk(0xA300, Encoding.ASCII.GetBytes(map + "\0"))));
        return Chunk(0x4D4D, Chunk(0x3D3D, Mat("Red", 255, 0, 0, texture), Mat("Blue", 0, 0, 255, null), obj));
    }

    /// <summary>
    /// A GLB of two triangles, two primitives: one green (a factor), one textured (no factor) with
    /// TEXCOORD_0 and its image in the BIN chunk, or as a file "tex/a b.png" beside it.
    /// </summary>
    private static byte[] TwoPrimitives(byte[] png, bool external)
    {
        var bin = new List<byte>();
        foreach (float f in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 0, 1, 1 }) bin.AddRange(BitConverter.GetBytes(f));   // 2 x 3 positions
        foreach (float f in new float[] { 0, 0, 1, 0, 0, 1 }) bin.AddRange(BitConverter.GetBytes(f));                                   // 3 uvs
        int imageAt = bin.Count;
        bin.AddRange(png);
        string image = external ? "{\"uri\":\"tex/a%20b.png\"}" : $"{{\"bufferView\":3,\"mimeType\":\"image/png\"}}";
        string json = """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":BINLEN}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":36},{"buffer":0,"byteOffset":72,"byteLength":24},
                            {"buffer":0,"byteOffset":IMAGEAT,"byteLength":PNGLEN}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":2,"componentType":5126,"count":3,"type":"VEC2"}],
             "materials":[{"pbrMetallicRoughness":{"baseColorFactor":[0,1,0,1]}},{"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}}],
             "textures":[{"source":0}],"images":[IMAGE],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"material":0},{"attributes":{"POSITION":1,"TEXCOORD_0":2},"material":1}]}],
             "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
            """.Replace("IMAGEAT", imageAt.ToString()).Replace("PNGLEN", png.Length.ToString()).Replace("BINLEN", bin.Count.ToString()).Replace("IMAGE", image);
        var j = Encoding.UTF8.GetBytes(json).ToList();
        while (j.Count % 4 != 0) j.Add((byte)' ');
        while (bin.Count % 4 != 0) bin.Add(0);
        var glb = new List<byte>();
        glb.AddRange(BitConverter.GetBytes(0x46546C67u));
        glb.AddRange(BitConverter.GetBytes(2u));
        glb.AddRange(BitConverter.GetBytes((uint)(12 + 8 + j.Count + 8 + bin.Count)));
        glb.AddRange(BitConverter.GetBytes((uint)j.Count)); glb.AddRange(BitConverter.GetBytes(0x4E4F534Au)); glb.AddRange(j);
        glb.AddRange(BitConverter.GetBytes((uint)bin.Count)); glb.AddRange(BitConverter.GetBytes(0x004E4942u)); glb.AddRange(bin);
        return glb.ToArray();
    }

    /// <summary>An MVR of a wash fixture and the given objects (a layer's child list) and files.</summary>
    private static byte[] Mvr(string objects, params (string Name, byte[] Bytes)[] files)
    {
        var wash = FixtureType.Wash;
        string scene = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <GeneralSceneDescription verMajor="1" verMinor="6"><Scene><Layers><Layer name="Rig" uuid="L1"><ChildList>
              <Fixture name="Wash 1" uuid="F1"><GDTFSpec>{Gdtf.FileName(wash)}</GDTFSpec><GDTFMode>{Gdtf.ModeName}</GDTFMode>
                <Addresses><Address break="0">1</Address></Addresses></Fixture>
              {objects}
            </ChildList></Layer></Layers></Scene></GeneralSceneDescription>
            """;
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in files.Append(("GeneralSceneDescription.xml", Encoding.UTF8.GetBytes(scene))).Append((Gdtf.FileName(wash), Gdtf.Write(wash))))
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(bytes, 0, bytes.Length);
            }
        return ms.ToArray();
    }
}
