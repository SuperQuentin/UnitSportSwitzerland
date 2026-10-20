using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Godot;
using UnitSport.Concert;
using UnitSport.Concert.Mvr;
using Xunit;

namespace UnitSport.Tests;

/// <summary>MVR and GDTF (#941): our rig out and back, and someone else's in.</summary>
public class MvrTests
{
    private static readonly FixtureType[] All =
    {
        FixtureType.Spot, FixtureType.Beam, FixtureType.Wash, FixtureType.Blinder, FixtureType.Strobe, FixtureType.PixelBar,
        FixtureType.Screen, FixtureType.Hazer, FixtureType.Flame, FixtureType.Co2, FixtureType.Confetti, FixtureType.Gerb,
        FixtureType.Airburst, FixtureType.LowFog,
    };

    [Fact]
    public void Every_type_of_ours_comes_back_from_its_gdtf_the_same()
    {
        foreach (var t in All)
        {
            var g = Gdtf.Read(Gdtf.Write(t), null, out string? error);
            Assert.True(g != null, $"{t.Name}: {error}");
            var back = Gdtf.ToFixtureType(g!, out string? why);
            Assert.True(back != null, $"{t.Name}: {why}");
            Assert.Equal(t.Name, back!.Name);
            Assert.Equal(t.Kind, back.Kind);
            Assert.Equal(t.Channels, back.Channels);
            Assert.Equal(t.PanRange, back.PanRange, 3);
            Assert.Equal(t.TiltRange, back.TiltRange, 3);
            Assert.Equal(t.BeamMin, back.BeamMin, 3);
            Assert.Equal(t.BeamMax, back.BeamMax, 3);
        }
    }

    [Fact]
    public void A_gdtf_names_standard_attributes_and_folds_fine_channels()
    {
        var x = Gdtf.Describe(FixtureType.Spot).ToString();
        Assert.Contains("Attribute=\"Pan\"", x);
        Assert.Contains("Offset=\"1,2\"", x);   // pan coarse and fine
        Assert.Contains("Offset=\"6,7\"", x);   // dimmer coarse and fine
        Assert.Contains("ColorSub_C", x);
        Assert.Contains("Gobo1", x);
        Assert.Contains("DataVersion=\"1.2\"", x);
    }

    private static DmxPatch Rig()
    {
        var p = new DmxPatch();
        // hung, standing, sideways and leaning: every orientation a stage has
        p.Add(FixtureType.Spot, RigGroup.FrontTruss, new Vector3(-6, 14, 30), new Vector3(0, -0.3f, 1), Vector3.Down);
        p.Add(FixtureType.Beam, RigGroup.MidTruss, new Vector3(2, 14, 26), new Vector3(0, 0, 1), Vector3.Down);
        p.Add(FixtureType.Beam, RigGroup.Floor, new Vector3(4, 2, 31), new Vector3(0, 0.2f, 1), Vector3.Up);
        p.Add(FixtureType.Wash, RigGroup.BackTruss, new Vector3(0, 14, 20), new Vector3(0, 0, 1), Vector3.Down);
        p.Add(FixtureType.Blinder, RigGroup.Blinders, new Vector3(8, 13, 31), new Vector3(0, -0.4f, 1), new Vector3(0, 0, 1));
        p.Add(FixtureType.Strobe, RigGroup.Strobes, new Vector3(-8, 13, 31), new Vector3(0.3f, -0.3f, 1), new Vector3(0.3f, 0, 1));
        p.Add(FixtureType.PixelBar, RigGroup.PixelBars, new Vector3(0, 2, 32), new Vector3(0, 0, 1), new Vector3(0, 0, 1));
        p.AddScreen(new Vector3(0, 9, 18), new Vector3(0, 0, 1), new Vector3(20, 8, 0), 36, 14);
        p.Add(FixtureType.Hazer, RigGroup.Haze, new Vector3(-10, 1.5f, 22), new Vector3(0, 0, 1), Vector3.Up);
        p.Add(FixtureType.Flame, RigGroup.Flames, new Vector3(-9, 2, 32), Vector3.Up, Vector3.Up);
        p.Add(FixtureType.Confetti, RigGroup.Confetti, new Vector3(9, 2, 32), new Vector3(0, 0.6f, 1), Vector3.Up);
        for (int i = 0; i < 60; i++)   // past one universe
            p.Add(FixtureType.Spot, RigGroup.FrontTruss, new Vector3(i - 30, 15, 28), new Vector3(0, 0, 1), Vector3.Down);
        return p;
    }

    [Fact]
    public void Our_rig_comes_back_from_its_mvr_where_it_was()
    {
        var src = Rig();
        var rig = RigData.FromPatch(src, "test");
        var mvr = MvrFile.Write(rig, "Test rig", stageGlb: new byte[] { 1, 2, 3 });
        var import = MvrFile.Read(mvr, "test.mvr", out string? error);
        Assert.True(import != null, error);
        Assert.True(import!.OursStadiumFrame);
        Assert.Empty(import.Notes);
        var back = new DmxPatch();
        import.Rig.PatchInto(back);
        Assert.Equal(src.Fixtures.Count, back.Fixtures.Count);
        for (int i = 0; i < src.Fixtures.Count; i++)
        {
            var a = src.Fixtures[i];
            var b = back.Fixtures[i];
            Assert.Equal(a.Type.Name, b.Type.Name);
            Assert.Equal(a.Type.Kind, b.Type.Kind);
            Assert.Equal(a.Type.Channels, b.Type.Channels);
            Assert.Equal(a.Group, b.Group);
            Assert.Equal(a.Universe, b.Universe);
            Assert.Equal(a.Address, b.Address);
            Assert.True(a.Position.DistanceTo(b.Position) < 0.001f, $"{i}: {a.Position} -> {b.Position}");
            Assert.True(a.Up.Dot(b.Up) > 0.99999f, $"{i}: up {a.Up} -> {b.Up}");
            Assert.True(a.Home.Dot(b.Home) > 0.99999f, $"{i}: home {a.Home} -> {b.Home}");
            Assert.Equal(a.ScreenSize, b.ScreenSize);
            Assert.Equal(a.PixelsX, b.PixelsX);
        }
        Assert.Equal(src.Universes.Count, back.Universes.Count);
    }

    [Fact]
    public void An_mvr_matrix_says_where_the_beam_goes()
    {
        // a hung head: its GDTF Z (beam at pan/tilt zero along -Z) is minus our yoke axis
        var axes = MvrFile.Axes(Vector3.Down, new Vector3(0, 0, 1));
        Assert.True((-axes.Z).Dot(Vector3.Down) > 0.9999f);
        Assert.True(axes.Y.Dot(new Vector3(0, 0, 1)) > 0.9999f);
        Assert.True(axes.Determinant() > 0.999f);   // right-handed
        // MVR is Z up: a head 14 m over the centre spot is at {0,0,14000} mm
        Assert.Equal(new Vector3(0, 0, 14000), MvrFile.ToMvr(new Vector3(0, 14, 0)) * 1000f);
        Assert.Equal(new Vector3(1, 2, 3), MvrFile.FromMvr(MvrFile.ToMvr(new Vector3(1, 2, 3))));
    }

    [Fact]
    public void A_packed_rig_round_trips_and_a_bad_one_is_refused()
    {
        var rig = RigData.FromPatch(Rig(), "t");
        var packed = rig.Pack();
        Assert.True(packed.Length < 16 * 1024, $"{packed.Length} bytes");
        var back = RigData.Unpack(packed, out string? error);
        Assert.True(back != null, error);
        Assert.Equal(rig.Fixtures.Count, back!.Fixtures.Count);

        rig.Fixtures[0].Address = 510;   // an 18-channel spot cannot start at 511
        Assert.Null(RigData.Unpack(rig.Pack(), out error));
        Assert.Contains("does not fit", error);
        Assert.Null(RigData.Unpack(new byte[] { 1, 2, 3 }, out _));
        rig.Fixtures[0].Address = 0;
        rig.Fixtures[0].Pos = new[] { 0f, 0f, 5000f };
        Assert.Null(RigData.Unpack(rig.Pack(), out _));
    }

    // ---- someone else's ---------------------------------------------------------------------------

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

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    private const string ThirdWash = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.1">
          <FixtureType Name="Aura" Manufacturer="Acme" Description="an LED wash">
            <Geometries><Geometry Name="Base"><Axis Name="Yoke"><Axis Name="Head"><Beam Name="Beam" BeamAngle="20"/></Axis></Axis></Geometry></Geometries>
            <DMXModes>
              <DMXMode Name="Mode 1 - 14ch" Geometry="Base"><DMXChannels>
                <DMXChannel DMXBreak="1" Offset="1,2" Geometry="Yoke"><LogicalChannel Attribute="Pan"><ChannelFunction Attribute="Pan" PhysicalFrom="-270" PhysicalTo="270"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="3,4" Geometry="Head"><LogicalChannel Attribute="Tilt"><ChannelFunction Attribute="Tilt" PhysicalFrom="-135" PhysicalTo="135"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="5" Geometry="Beam"><LogicalChannel Attribute="Shutter1"><ChannelFunction Attribute="Shutter1"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="6,7" Geometry="Beam"><LogicalChannel Attribute="Dimmer"><ChannelFunction Attribute="Dimmer"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="8" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_R"><ChannelFunction Attribute="ColorAdd_R"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="9" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_G"><ChannelFunction Attribute="ColorAdd_G"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="10" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_B"><ChannelFunction Attribute="ColorAdd_B"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="11" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_W"><ChannelFunction Attribute="ColorAdd_W"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="12,13" Geometry="Beam"><LogicalChannel Attribute="Zoom"><ChannelFunction Attribute="Zoom" PhysicalFrom="7" PhysicalTo="55"/></LogicalChannel></DMXChannel>
                <DMXChannel DMXBreak="1" Offset="14" Geometry="Beam"><LogicalChannel Attribute="Control1"><ChannelFunction Attribute="Control1"/></LogicalChannel></DMXChannel>
              </DMXChannels></DMXMode>
            </DMXModes>
          </FixtureType>
        </GDTF>
        """;

    private const string ThirdBar = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.1">
          <FixtureType Name="Bar 4" Manufacturer="Acme">
            <DMXModes><DMXMode Name="13ch"><DMXChannels>
              <DMXChannel Offset="1" Geometry="Base"><LogicalChannel Attribute="Dimmer"/></DMXChannel>
              <DMXChannel Offset="2" Geometry="P1"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
              <DMXChannel Offset="3" Geometry="P1"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
              <DMXChannel Offset="4" Geometry="P1"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
              <DMXChannel Offset="5" Geometry="P2"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
              <DMXChannel Offset="6" Geometry="P2"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
              <DMXChannel Offset="7" Geometry="P2"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
              <DMXChannel Offset="8" Geometry="P3"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
              <DMXChannel Offset="9" Geometry="P3"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
              <DMXChannel Offset="10" Geometry="P3"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
              <DMXChannel Offset="11" Geometry="P4"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
              <DMXChannel Offset="12" Geometry="P4"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
              <DMXChannel Offset="13" Geometry="P4"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
            </DMXChannels></DMXMode></DMXModes>
          </FixtureType>
        </GDTF>
        """;

    private const string Odd = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.1"><FixtureType Name="Motor" Manufacturer="Acme"><DMXModes><DMXMode Name="1ch"><DMXChannels>
          <DMXChannel Offset="1"><LogicalChannel Attribute="Speed"/></DMXChannel>
        </DMXChannels></DMXMode></DMXModes></FixtureType></GDTF>
        """;

    [Fact]
    public void Someone_elses_wash_and_pixel_bar_come_in_with_their_own_layout()
    {
        var wash = Gdtf.ToFixtureType(Gdtf.FromXml(XDocument.Parse(ThirdWash), null, out _)!, out string? why);
        Assert.True(wash != null, why);
        Assert.Equal(FixtureKind.Wash, wash!.Kind);
        Assert.Equal(14, wash.Footprint);
        Assert.Equal(0, wash.Offset(Attr.Pan));
        Assert.Equal(1, wash.Offset(Attr.PanFine));
        Assert.Equal(5, wash.Offset(Attr.Dimmer));
        Assert.Equal(6, wash.Offset(Attr.DimmerFine));
        Assert.Equal(11, wash.Offset(Attr.Zoom));
        Assert.Equal(Attr.Other, wash.Channels[12]);   // the zoom's fine
        Assert.Equal(540f, wash.PanRange, 3);
        Assert.Equal(7f, wash.BeamMin, 3);
        Assert.Equal(55f, wash.BeamMax, 3);
        Assert.Equal("Acme Aura (Mode 1 - 14ch)", wash.Name);

        var bar = Gdtf.ToFixtureType(Gdtf.FromXml(XDocument.Parse(ThirdBar), null, out _)!, out why);
        Assert.True(bar != null, why);
        Assert.Equal(FixtureKind.PixelBar, bar!.Kind);
        Assert.Equal(4, bar.Cells);
        Assert.Equal(0, bar.Offset(Attr.Dimmer));   // a master before the cells
        Assert.Equal(1, bar.CellOffsets[0]);

        Assert.Null(Gdtf.ToFixtureType(Gdtf.FromXml(XDocument.Parse(Odd), null, out _)!, out _));
    }

    [Fact]
    public void Someone_elses_mvr_lands_in_the_stage_frame_with_its_addresses()
    {
        // a truss 10 m up and 5 m upstage, a wash hung from it 2 m stage left; a bar on the floor at
        // universe 2 address 1 ("2.1") facing the audience; a device the game does not know
        const string scene = """
            <?xml version="1.0" encoding="UTF-8"?>
            <GeneralSceneDescription verMajor="1" verMinor="5">
              <Scene><Layers><Layer name="Rig" uuid="L1"><ChildList>
                <Truss name="Truss 1" uuid="T1"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,5000,10000}</Matrix>
                  <ChildList>
                    <Fixture name="Wash 1" uuid="F1"><Matrix>{1,0,0}{0,1,0}{0,0,1}{-2000,0,-300}</Matrix>
                      <GDTFSpec>Acme@Aura.gdtf</GDTFSpec><GDTFMode>Mode 1 - 14ch</GDTFMode>
                      <Addresses><Address break="0">101</Address></Addresses></Fixture>
                  </ChildList></Truss>
                <Fixture name="Bar 1" uuid="F2"><Matrix>{1,0,0}{0,0,1}{0,1,0}{0,-1000,200}</Matrix>
                  <GDTFSpec>Acme@Bar4.gdtf</GDTFSpec><GDTFMode>13ch</GDTFMode>
                  <Addresses><Address break="0">2.1</Address></Addresses></Fixture>
                <Fixture name="Motor 1" uuid="F3"><GDTFSpec>Acme@Motor.gdtf</GDTFSpec><GDTFMode>1ch</GDTFMode>
                  <Addresses><Address break="0">400</Address></Addresses></Fixture>
              </ChildList></Layer></Layers></Scene>
            </GeneralSceneDescription>
            """;
        var mvr = Zip(("GeneralSceneDescription.xml", Text(scene)),
            ("Acme@Aura.gdtf", Zip(("description.xml", Text(ThirdWash)))),
            ("Acme@Bar4.gdtf", Zip(("description.xml", Text(ThirdBar)))),
            ("Acme@Motor.gdtf", Zip(("description.xml", Text(Odd)))));
        var import = MvrFile.Read(mvr, "acme.mvr", out string? error);
        Assert.True(import != null, error);
        Assert.False(import!.OursStadiumFrame);
        Assert.Equal(2, import.Rig.Fixtures.Count);
        Assert.Contains(import.Notes, n => n.Contains("Motor"));

        var wash = import.Rig.Fixtures[0];
        // MVR (-2, 5, 9.7) m, Z up: ours x -2, up 9.7, z -5 (upstage is away from the audience)
        Assert.True(RigData.V(wash.Pos).DistanceTo(new Vector3(-2, 9.7f, -5)) < 0.001f, $"{RigData.V(wash.Pos)}");
        Assert.True(RigData.V(wash.Up).Dot(Vector3.Down) > 0.999f, $"up {RigData.V(wash.Up)}");   // its Z up: hung, the beam down
        Assert.Equal(0, wash.Universe);
        Assert.Equal(100, wash.Address);
        Assert.Equal((int)RigGroup.BackTruss, wash.Group);

        var bar = import.Rig.Fixtures[1];
        Assert.Equal(1, bar.Universe);
        Assert.Equal(0, bar.Address);
        Assert.Equal((int)RigGroup.PixelBars, bar.Group);
        // its Z is MVR's +Y (upstage): the beam, along -Z, faces downstage, the audience (+z ours)
        Assert.True(RigData.V(bar.Up).Dot(new Vector3(0, 0, 1)) > 0.999f, $"bar faces the audience: {RigData.V(bar.Up)}");
    }

    /// <summary>A profile stripped as Capture's exported demo show's are: every channel a numbered placeholder.</summary>
    private static string Stripped(string name, int channels) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><GDTF DataVersion="1.1"><FixtureType Name="{name}" Manufacturer="Acme"><Geometries><Geometry Name="Base"/></Geometries>"""
        + """<DMXModes><DMXMode Name="Standard" Geometry="Base"><DMXChannels>"""
        + string.Concat(Enumerable.Range(1, channels).Select(i =>
            $"""<DMXChannel DMXBreak="1" Geometry="Base" Offset="{i}"><LogicalChannel Attribute="Dimmer{i}"><ChannelFunction Attribute="Dimmer{i}" DMXFrom="0/1"/></LogicalChannel></DMXChannel>"""))
        + "</DMXChannels></DMXMode></DMXModes></FixtureType></GDTF>";

    [Fact]
    public void A_stripped_profile_is_hung_as_our_type_of_its_kind()
    {
        FixtureType? Of(string name) => Gdtf.ToFixtureType(Gdtf.FromXml(XDocument.Parse(Stripped(name, 268)), null, out _)!, out _);
        var spot = Of("Alpha Spot QWO 800");
        Assert.NotNull(spot);
        Assert.Equal(FixtureKind.Spot, spot!.Kind);
        Assert.Equal(FixtureType.Spot.Channels, spot.Channels);   // ours, not its 268 placeholders
        Assert.Contains("layout unknown", spot.Name);
        Assert.Equal(FixtureKind.Wash, Of("A.leda Wash K20")!.Kind);
        Assert.Equal(FixtureKind.Beam, Of("Sharpy Beam")!.Kind);
        var cyc = Of("ALC4")!;   // no word for what it is: a static wash
        Assert.Equal(FixtureKind.Wash, cyc.Kind);
        Assert.Equal(0f, cyc.PanRange);
        // real channels we only do not know stay refused
        Assert.Null(Gdtf.ToFixtureType(Gdtf.FromXml(XDocument.Parse(Odd), null, out _)!, out _));
    }

    [Fact]
    public void Fixtures_not_patched_in_the_file_are_patched_after_the_rest()
    {
        // a plot drawn before the patch: address 0, as BlenderDMX's and Vectorworks' samples have them
        const string scene = """
            <?xml version="1.0" encoding="UTF-8"?>
            <GeneralSceneDescription verMajor="1" verMinor="5">
              <Scene><Layers><Layer name="Rig" uuid="L1"><ChildList>
                <Fixture name="A" uuid="F1"><GDTFSpec>Acme@Aura.gdtf</GDTFSpec><GDTFMode>Mode 1 - 14ch</GDTFMode>
                  <Addresses><Address break="0">0</Address></Addresses></Fixture>
                <Fixture name="B" uuid="F2"><GDTFSpec>Acme@Aura.gdtf</GDTFSpec><GDTFMode>Mode 1 - 14ch</GDTFMode>
                  <Addresses><Address break="0">491</Address></Addresses></Fixture>
                <Fixture name="C" uuid="F3"><GDTFSpec>Acme@Aura.gdtf</GDTFSpec><GDTFMode>Mode 1 - 14ch</GDTFMode></Fixture>
              </ChildList></Layer></Layers></Scene>
            </GeneralSceneDescription>
            """;
        var import = MvrFile.Read(Zip(("GeneralSceneDescription.xml", Text(scene)), ("Acme@Aura.gdtf", Zip(("description.xml", Text(ThirdWash))))), "plot.mvr", out string? error);
        Assert.True(import != null, error);
        var f = import!.Rig.Fixtures;
        Assert.Equal(3, f.Count);
        Assert.Equal((0, 490), (f[1].Universe, f[1].Address));
        // 504 + 14 would cross the universe's end: the next starts the second universe
        Assert.Equal((1, 0), (f[0].Universe, f[0].Address));
        Assert.Equal((1, 14), (f[2].Universe, f[2].Address));
        Assert.Contains(import.Notes, n => n.Contains("not patched"));
        Assert.Null(import.Rig.Check());
    }
}
