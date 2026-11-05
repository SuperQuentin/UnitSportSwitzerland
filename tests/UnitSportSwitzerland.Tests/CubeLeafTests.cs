using System.Security.Cryptography;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The cube leaf content format (#1505, G2 B1): every tile kind as a section of one leaf entry,
/// entries bundled in <c>.cubl</c> files; and the LV95 codecs split into a head and a key-free body
/// without changing a byte of their files.
/// </summary>
public class CubeLeafTests
{
    private static readonly CubeKey Leaf = CubeKey.FromLatLon(46.2044, 6.1432, 14);
    private static readonly int Bits = CubeKey.LatticeBits(14);
    private static readonly int Side = (1 << Bits) + 1;

    private static byte[] Bytes(Action<Stream> write)
    {
        using var ms = new MemoryStream();
        write(ms);
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    // ---- the LV95 files, byte-identical after the body split ---------------------------------

    /// <summary>The SHA-256 of each sample's file, taken from the codecs before the split (main at e3dfeffa).</summary>
    [Theory]
    [InlineData("road", "F63E92220C89D7A80C1F8616E05288F29E397688F9FD4CAA5A1B4C02552546B1", 590)]
    [InlineData("bldg", "98D676522C563E2632ADCB71BD4B52FF2D6B81B24CA2AE2B0146396F9EE99CB1", 136)]
    [InlineData("trees", "24AD99120F0F29B5765238D9B95DF8CACA5D28A4ABBB1E9CED4CCB364B6EA096", 80)]
    [InlineData("pst", "20ABB95DB9A4F9AE7DBD982D3CB5C2D4B60F15221D8DFD467DB38048FF526B82", 296119)]
    [InlineData("fld", "116486009C61B262C6DBE8DCDDB6C0AE8A630982353CAB110C00022F17AB6507", 136)]
    public void Lv95_files_are_byte_identical_after_the_body_split(string kind, string sha, int length)
    {
        var id = CubeLeafSamples.Id;
        byte[] file = kind switch
        {
            "road" => Bytes(s => RoadCodec.Encode(CubeLeafSamples.Road(), s)),
            "bldg" => Bytes(s => BuildingCodec.Encode(CubeLeafSamples.Buildings(), s)),
            "trees" => Bytes(s => TreeFormat.Encode(id, CubeLeafSamples.Trees(), s)),
            "pst" => Bytes(s => PisteFormat.Encode(CubeLeafSamples.Pistes(PisteFormat.Size), s)),
            _ => Bytes(s => FieldFormat.Encode(id, CubeLeafSamples.Fields(), s)),
        };
        Assert.Equal(length, file.Length);
        Assert.Equal(sha, Sha(file));

        // and the file is its 16-byte head (magic, version, flags, tile id) followed by the body
        byte[] body = kind switch
        {
            "road" => Bytes(s => RoadCodec.EncodeBody(CubeLeafSamples.Road(), s)),
            "bldg" => Bytes(s => BuildingCodec.EncodeBody(CubeLeafSamples.Buildings(), s)),
            "trees" => Bytes(s => TreeFormat.EncodeBody(CubeLeafSamples.Trees(), s)),
            "pst" => Bytes(s => PisteFormat.EncodeBody(CubeLeafSamples.Pistes(PisteFormat.Size), s, PisteFormat.Size)),
            _ => Bytes(s => FieldFormat.EncodeBody(CubeLeafSamples.Fields(), s)),
        };
        Assert.Equal(TileHeader.HeadSize + body.Length, file.Length);
        Assert.True(file.AsSpan(TileHeader.HeadSize).SequenceEqual(body));
        Assert.Equal(id, TileHeader.Read(file).Id);
    }

    [Fact]
    public void Lv95_files_still_decode_through_their_bodies()
    {
        var road = RoadCodec.Decode(new MemoryStream(Bytes(s => RoadCodec.Encode(CubeLeafSamples.Road(), s))));
        Assert.Equal(CubeLeafSamples.Id, road.Id);
        Assert.Equal(RoadTileFlags.Osm | RoadTileFlags.Network | RoadTileFlags.Bikes, road.Flags);
        AssertRoad(CubeLeafSamples.Road(), road);
        var pistes = PisteFormat.Decode(new MemoryStream(Bytes(s => PisteFormat.Encode(CubeLeafSamples.Pistes(PisteFormat.Size), s))));
        Assert.Equal(CubeLeafSamples.Id, pistes.Id);
        AssertPistes(CubeLeafSamples.Pistes(PisteFormat.Size), pistes);
        Assert.Equal(CubeLeafSamples.Id, BuildingCodec.Decode(new MemoryStream(Bytes(s => BuildingCodec.Encode(CubeLeafSamples.Buildings(), s)))).Id);
        Assert.Equal(CubeLeafSamples.Trees(), TreeFormat.Decode(new MemoryStream(Bytes(s => TreeFormat.Encode(CubeLeafSamples.Id, CubeLeafSamples.Trees(), s)))));
        AssertFields(CubeLeafSamples.Fields(), FieldFormat.Decode(new MemoryStream(Bytes(s => FieldFormat.Encode(CubeLeafSamples.Id, CubeLeafSamples.Fields(), s)))));
    }

    // ---- one section per kind --------------------------------------------------------------------

    private static CubeTile Heights()
    {
        var h = new float[Side * Side];
        for (int j = 0; j < Side; j++)
            for (int i = 0; i < Side; i++)
                h[j * Side + i] = 420f + 0.31f * i - 0.17f * j + 6f * MathF.Sin(i * 0.05f) * MathF.Cos(j * 0.07f);
        h[5] = float.NaN;
        return new CubeTile { Key = Leaf, Bits = Bits, Heights = h, Quantum = CubeKey.QuantumFor(14) };
    }

    private static byte[] Cover()
    {
        var c = new byte[Side * Side];
        for (int k = 0; k < c.Length; k++) c[k] = (byte)(k / 9000 % 3 == 0 ? CoverClass.Forest : k % 977 == 0 ? CoverClass.Water : CoverClass.Open);
        return c;
    }

    private static CubeWater Water()
    {
        var w = CubeWater.Dry(Bits);
        int n = w.Side;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n / 3; c++)
            {
                w.Level[r * n + c] = 372.14f + 0.004f * r;   // a river falling towards the south
                w.Fetch[r * n + c] = (byte)(1 + c % 200);
            }
        return w;
    }

    private static CubePhoto Photo() => new()
    {
        Format = CubeAlbedoFormat.Jpeg, Width = 320, Height = 320,
        Image = [0xFF, 0xD8, .. Enumerable.Range(0, 300).Select(i => (byte)(i * 7)), 0xFF, 0xD9],
    };

    private static CubeLeafContent Everything() => new()
    {
        Key = Leaf,
        Heights = Heights(),
        Cover = Cover(),
        Holes = [LeafHoles.Quad(0, 0, Bits), LeafHoles.Quad(511, 0, Bits), LeafHoles.Quad(200, 300, Bits), LeafHoles.Quad(511, 511, Bits)],
        Water = Water(),
        Roads = CubeLeafSamples.Road(),
        Buildings = CubeLeafSamples.Buildings(),
        Trees = CubeLeafSamples.Trees(),
        Pistes = CubeLeafSamples.Pistes(Side),
        Fields = CubeLeafSamples.Fields(),
        Photo = Photo(),
    };

    [Fact]
    public void Heights_section_is_the_cube_tile_codec_bytes_as_they_are()
    {
        var tile = Heights();
        var s = LeafHeights.Encode(tile);
        Assert.Equal(CubeTileCodec.Encode(tile), s.Payload);
        var back = LeafHeights.Decode(s);
        Assert.Equal(Leaf, back.Key);
        for (int k = 0; k < tile.Heights.Length; k++)
            if (float.IsNaN(tile.Heights[k])) Assert.True(float.IsNaN(back.Heights[k]));
            else Assert.True(Math.Abs(tile.Heights[k] - back.Heights[k]) <= 0.005f + 1e-4f);
    }

    [Fact]
    public void Cover_holes_water_and_photo_round_trip()
    {
        var cover = Cover();
        Assert.Equal(cover, LeafCover.Decode(LeafCover.Encode(cover, Bits)));
        Assert.Throws<ArgumentException>(() => LeafCover.Encode(new byte[10], Bits));

        int[] holes = [LeafHoles.Quad(511, 511, Bits), 0, LeafHoles.Quad(3, 4, Bits), 0];
        Assert.Equal(new[] { 0, 3 + 4 * 512, 511 + 511 * 512 }, LeafHoles.Decode(LeafHoles.Encode(holes, Bits)));
        Assert.Empty(LeafHoles.Decode(LeafHoles.Encode([], Bits)));
        Assert.Throws<ArgumentException>(() => LeafHoles.Encode([512 * 512], Bits));

        var water = Water();
        Assert.Equal(257, water.Side);
        var back = LeafWater.Decode(LeafWater.Encode(water));
        Assert.Equal(water.Fetch, back.Fetch);
        for (int k = 0; k < water.Level.Length; k++)
            if (float.IsNaN(water.Level[k])) Assert.True(float.IsNaN(back.Level[k]), "dry stays dry");
            else Assert.True(Math.Abs(water.Level[k] - back.Level[k]) <= 0.005f + 1e-4f, $"level {k}");
        var dry = LeafWater.Decode(LeafWater.Encode(CubeWater.Dry(Bits)));
        Assert.All(dry.Level, l => Assert.True(float.IsNaN(l)));

        var photo = Photo();
        var p = LeafPhoto.Decode(LeafPhoto.Encode(photo));
        Assert.Equal((photo.Format, photo.Width, photo.Height), (p.Format, p.Width, p.Height));
        Assert.Equal(photo.Image, p.Image);
    }

    [Fact]
    public void Record_sections_round_trip_without_their_tile_id()
    {
        var road = CubeLeafSamples.Road();
        var rs = LeafRoads.Encode(road);
        Assert.Equal((ushort)road.Flags, rs.Flags);
        var r = LeafRoads.Decode(rs);
        Assert.Equal(default, r.Id);
        Assert.Equal(road.Flags, r.Flags);
        AssertRoad(road, r);

        var b = LeafBuildings.Decode(LeafBuildings.Encode(CubeLeafSamples.Buildings()));
        Assert.Equal(default, b.Id);
        Assert.Equal(CubeLeafSamples.Buildings().Buildings.Select(x => (x.Kind, x.Floors, x.YearBuilt, x.Egid, x.MinY, x.MaxY)),
            b.Buildings.Select(x => (x.Kind, x.Floors, x.YearBuilt, x.Egid, x.MinY, x.MaxY)));
        Assert.Equal(CubeLeafSamples.Buildings().Buildings[0].Triangles, b.Buildings[0].Triangles);

        Assert.Equal(CubeLeafSamples.Trees(), LeafTrees.Decode(LeafTrees.Encode(CubeLeafSamples.Trees())));
        AssertFields(CubeLeafSamples.Fields(), LeafFields.Decode(LeafFields.Encode(CubeLeafSamples.Fields())));

        // pistes carry their rasters on the leaf's lattice, not the LV95 tile's 1001² grid
        var ps = LeafPistes.Encode(CubeLeafSamples.Pistes(Side), Bits);
        Assert.Equal(PisteFormat.FlagOsm, ps.Flags);
        var pistes = LeafPistes.Decode(ps);
        Assert.Equal(Side * Side, pistes.Slot.Length);
        AssertPistes(CubeLeafSamples.Pistes(Side), pistes);
        Assert.Throws<ArgumentException>(() => LeafPistes.Encode(CubeLeafSamples.Pistes(PisteFormat.Size), Bits));
    }

    // ---- the whole leaf --------------------------------------------------------------------------

    [Fact]
    public void A_leaf_with_every_kind_round_trips()
    {
        var content = Everything();
        byte[] entry = content.Encode();
        var (key, table) = CubeLeafCodec.ReadTable(entry);
        Assert.Equal(Leaf, key);
        // every kind a region's leaf holds; the road stage's raw kinds live in a temporary .cubl only
        Assert.Equal(Enum.GetValues<LeafKind>().Where(k => !LeafKinds.IsRaw(k)), table.Select(t => t.Kind));

        var back = CubeLeafContent.Decode(entry);
        Assert.Empty(back.Skipped);
        Assert.Equal(content.Cover, back.Cover);
        Assert.Equal(content.Holes!.Order(), back.Holes);
        Assert.Equal(content.Water!.Fetch, back.Water!.Fetch);
        Assert.Equal(content.Trees, back.Trees);
        Assert.Equal(content.Photo!.Image, back.Photo!.Image);
        AssertRoad(content.Roads!, back.Roads!);
        AssertPistes(content.Pistes!, back.Pistes!);
        AssertFields(content.Fields!, back.Fields!);
        Assert.Equal(content.Buildings!.Buildings.Count, back.Buildings!.Buildings.Count);
        Assert.Equal(Leaf, back.Heights!.Key);
        for (int k = 0; k < content.Heights!.Heights.Length; k++)
            Assert.True(float.IsNaN(content.Heights.Heights[k]) ? float.IsNaN(back.Heights.Heights[k])
                : Math.Abs(content.Heights.Heights[k] - back.Heights.Heights[k]) <= 0.005f + 1e-4f, $"height {k}");

        // decoding and encoding again gives the same bytes: nothing is lost or reordered. Heights
        // aside: G1's header keeps the height range before quantising, so decoded heights write a
        // range a few millimetres off (the same heights otherwise); their values are compared above.
        var again = CubeLeafCodec.Decode(back.Encode());
        foreach (var s in CubeLeafCodec.Decode(entry).Sections.Where(s => s.Kind != LeafKind.Heights))
            Assert.True(s.Payload.AsSpan().SequenceEqual(again.Find(s.Kind)!.Payload), $"{s.Kind} re-encodes to other bytes");
        Assert.Equal(entry, CubeLeafCodec.Encode(CubeLeafCodec.Decode(entry)));
        // one kind without decoding the rest
        Assert.Equal(content.Cover, LeafCover.Decode(CubeLeafCodec.ReadSection(entry, LeafKind.Cover)!));
        Assert.Null(CubeLeafCodec.ReadSection(new CubeLeafContent { Key = Leaf, Trees = [] }.Encode(), LeafKind.Cover));
    }

    [Fact]
    public void An_unknown_kind_and_a_newer_codec_version_are_skipped_and_kept()
    {
        var leaf = new CubeLeafContent { Key = Leaf, Cover = Cover(), Trees = CubeLeafSamples.Trees() }.ToLeaf();
        var future = new LeafSection((LeafKind)999, 1, 7, [1, 2, 3, 4, 5]);
        var newerTrees = leaf.Find(LeafKind.Trees)! with { Version = (ushort)(LeafTrees.Version + 1), Payload = [9, 9, 9] };
        leaf.Set(future);
        leaf.Set(newerTrees);
        byte[] entry = CubeLeafCodec.Encode(leaf);

        var content = CubeLeafContent.Decode(entry);
        Assert.Equal(Cover(), content.Cover);
        Assert.Null(content.Trees);
        Assert.Equal(new[] { LeafKind.Trees, (LeafKind)999 }, content.Skipped);

        // a rewrite of another kind keeps both bit for bit
        byte[] rewritten = CubeLeafCodec.Replace(entry, [LeafCover.Encode(new byte[Side * Side], Bits)]);
        var again = CubeLeafCodec.Decode(rewritten);
        Assert.Equal(future, again.Find((LeafKind)999)! with { Payload = future.Payload });
        Assert.Equal(future.Payload, again.Find((LeafKind)999)!.Payload);
        Assert.Equal(newerTrees.Payload, again.Find(LeafKind.Trees)!.Payload);
        Assert.Equal(newerTrees.Version, again.Find(LeafKind.Trees)!.Version);
    }

    [Fact]
    public void Replacing_sections_keeps_the_others_bit_for_bit()
    {
        byte[] entry = Everything().Encode();
        var before = CubeLeafCodec.Decode(entry);
        var roads = CubeLeafSamples.Road();
        roads.Segments.RemoveAt(1);
        byte[] after = CubeLeafCodec.Replace(entry, [LeafRoads.Encode(roads)], [LeafKind.Photo]);
        var leaf = CubeLeafCodec.Decode(after);

        Assert.Null(leaf.Find(LeafKind.Photo));
        Assert.Single(LeafRoads.Decode(leaf.Find(LeafKind.Roads)!).Segments);
        foreach (var s in before.Sections.Where(s => s.Kind is not (LeafKind.Roads or LeafKind.Photo)))
        {
            var kept = leaf.Find(s.Kind)!;
            Assert.Equal((s.Version, s.Flags), (kept.Version, kept.Flags));
            Assert.Equal(s.Payload, kept.Payload);
        }
        Assert.Equal(before.Sections.Count - 1, leaf.Sections.Count);
        Assert.Equal(leaf.Sections.Select(s => s.Kind).Order(), leaf.Sections.Select(s => s.Kind));
    }

    [Fact]
    public void Entry_reader_refuses_what_is_not_a_leaf()
    {
        byte[] entry = Everything().Encode();
        Assert.Throws<InvalidDataException>(() => CubeLeafCodec.ReadTable(entry.AsSpan(0, 30)));
        Assert.Throws<InvalidDataException>(() => CubeLeafCodec.ReadTable(CubeTileCodec.Encode(Heights())));
        var twice = new CubeLeaf { Key = Leaf, Sections = [new(LeafKind.Trees, 1, 0, []), new(LeafKind.Trees, 1, 0, [])] };
        Assert.Throws<ArgumentException>(() => CubeLeafCodec.Encode(twice));
    }

    // ---- bundles ---------------------------------------------------------------------------------

    [Fact]
    public void A_bundle_of_leaves_reads_back_by_index_and_a_stage_reruns_alone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cubeleaf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bundle = Leaf.AncestorAt(9);
            string path = Path.Combine(dir, CubeBundle.LeafFileName(bundle));
            Assert.EndsWith(".cubl", path);
            var keys = Leaf.Neighbours().Where(k => k.AncestorAt(9) == bundle).Take(2).Prepend(Leaf).ToArray();
            Assert.Equal(3, keys.Length);

            // the ground stage writes cover in every leaf, the trees stage trees in the first
            CubeBundle.UpdateLeaves(path, bundle, keys.ToDictionary(k => k, k => new List<LeafSection> { LeafCover.Encode(Cover(), Bits) }), [LeafKind.Cover]);
            CubeBundle.UpdateLeaves(path, bundle, new Dictionary<CubeKey, List<LeafSection>> { [keys[0]] = [LeafTrees.Encode(CubeLeafSamples.Trees())] }, [LeafKind.Trees]);

            using (var fs = File.OpenRead(path))
                Assert.Equal(keys.ToHashSet(), CubeBundle.ReadIndex(fs, CubeBundle.LeafMagic).Keys.ToHashSet());
            using (var fs = File.OpenRead(path))
                Assert.Throws<InvalidDataException>(() => CubeBundle.ReadIndex(fs));   // not a .cubb
            var first = CubeLeafContent.Decode(CubeBundle.ReadBytes(path, keys[0], CubeBundle.LeafMagic)!);
            Assert.Equal(CubeLeafSamples.Trees(), first.Trees);
            Assert.Equal(Cover(), first.Cover);
            Assert.Null(CubeBundle.ReadLeaf(path, keys[1])!.Find(LeafKind.Trees));
            Assert.Null(CubeBundle.ReadLeaf(path, Leaf.Neighbour(-3, -3)!.Value));

            // the trees stage reruns with none: its kind goes, the cover stays as it was, bit for bit
            byte[] coverBefore = CubeBundle.ReadLeaf(path, keys[0])!.Find(LeafKind.Cover)!.Payload;
            CubeBundle.UpdateLeaves(path, bundle, new Dictionary<CubeKey, List<LeafSection>>(), [LeafKind.Trees]);
            var leaf = CubeBundle.ReadLeaf(path, keys[0])!;
            Assert.Null(leaf.Find(LeafKind.Trees));
            Assert.Equal(coverBefore, leaf.Find(LeafKind.Cover)!.Payload);

            // and a leaf left with nothing is dropped from the bundle
            CubeBundle.UpdateLeaves(path, bundle, new Dictionary<CubeKey, List<LeafSection>>(), [LeafKind.Cover]);
            using (var fs = File.OpenRead(path))
                Assert.Empty(CubeBundle.ReadIndex(fs, CubeBundle.LeafMagic));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- comparisons -----------------------------------------------------------------------------

    private static void AssertRoad(RoadTile a, RoadTile b)
    {
        Assert.Equal(a.Segments.Count, b.Segments.Count);
        for (int k = 0; k < a.Segments.Count; k++)
        {
            Assert.Equal((a.Segments[k].Class, a.Segments[k].Surface, a.Segments[k].Width), (b.Segments[k].Class, b.Segments[k].Surface, b.Segments[k].Width));
            Assert.Equal(a.Segments[k].Points, b.Segments[k].Points);
        }
        Assert.Equal(a.Junctions.Select(j => j.Vertices), b.Junctions.Select(j => j.Vertices));
        Assert.Equal(a.PointProps, b.PointProps);
        Assert.Equal(a.LinearProps.Select(p => p.Points), b.LinearProps.Select(p => p.Points));
        Assert.Equal(a.AreaProps.Select(p => p.Vertices), b.AreaProps.Select(p => p.Vertices));
        Assert.Equal(a.Signals.Count, b.Signals.Count);
        Assert.Equal(a.Approaches.Select(p => p.Lanes.Count), b.Approaches.Select(p => p.Lanes.Count));
        Assert.Equal(a.Paint.Select(p => (p.Type, p.Offset, p.Vertices.Length)), b.Paint.Select(p => (p.Type, p.Offset, p.Vertices.Length)));
    }

    private static void AssertPistes(PisteTile a, PisteTile b)
    {
        Assert.Equal(a.Slot, b.Slot);
        Assert.Equal(a.Aux, b.Aux);
        Assert.Equal(a.Records.Count, b.Records.Count);
        for (int k = 0; k < a.Records.Count; k++)
        {
            var (x, y) = (a.Records[k], b.Records[k]);
            Assert.Equal((x.Id, x.Type, x.Difficulty, x.Grooming, x.IsArea, x.WidthM, x.Name, x.Ref, x.AltLow, x.AltHigh, x.Sun, x.SteepDeg),
                (y.Id, y.Type, y.Difficulty, y.Grooming, y.IsArea, y.WidthM, y.Name, y.Ref, y.AltLow, y.AltHigh, y.Sun, y.SteepDeg));
            Assert.Equal(a.Records[k].Path, b.Records[k].Path);
        }
    }

    private static void AssertFields(List<FieldPolygon> a, List<FieldPolygon> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (int k = 0; k < a.Count; k++)
        {
            Assert.Equal((a[k].Id, a[k].Crop, a[k].Source, a[k].LnfCode), (b[k].Id, b[k].Crop, b[k].Source, b[k].LnfCode));
            Assert.Equal(a[k].Rings, b[k].Rings);
        }
    }
}
