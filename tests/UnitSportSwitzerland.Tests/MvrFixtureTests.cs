using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Godot;
using UnitSport.Concert;
using UnitSport.Concert.Mvr;
using Xunit;

namespace UnitSport.Tests;

/// <summary>An MVR fixture's details (#959): focus points, DMX invert, breaks, video screens, and an MVR 1.6 export.</summary>
public class MvrFixtureTests
{
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

    /// <summary>A moving wash whose LED ring (two RGB cells) is on a second DMX break.</summary>
    private const string RingWash = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2">
          <FixtureType Name="Ring" Manufacturer="Acme">
            <Geometries><Geometry Name="Base"><Axis Name="Yoke"><Axis Name="Head"><Beam Name="Beam" BeamAngle="20"/>
              <Beam Name="R1"/><Beam Name="R2"/><Beam Name="R3"/><Beam Name="R4"/></Axis></Axis></Geometry></Geometries>
            <DMXModes><DMXMode Name="Two breaks" Geometry="Base"><DMXChannels>
              <DMXChannel DMXBreak="1" Offset="1,2" Geometry="Yoke"><LogicalChannel Attribute="Pan"><ChannelFunction Attribute="Pan" PhysicalFrom="-270" PhysicalTo="270"/></LogicalChannel></DMXChannel>
              <DMXChannel DMXBreak="1" Offset="3,4" Geometry="Head"><LogicalChannel Attribute="Tilt"><ChannelFunction Attribute="Tilt" PhysicalFrom="-135" PhysicalTo="135"/></LogicalChannel></DMXChannel>
              <DMXChannel DMXBreak="1" Offset="5" Geometry="Beam"><LogicalChannel Attribute="Dimmer"/></DMXChannel>
              <DMXChannel DMXBreak="1" Offset="6" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
              <DMXChannel DMXBreak="1" Offset="7" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
              <DMXChannel DMXBreak="1" Offset="8" Geometry="Beam"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
              <DMXChannel DMXBreak="2" Offset="1" Geometry="R1"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
              <DMXChannel DMXBreak="2" Offset="2" Geometry="R1"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
              <DMXChannel DMXBreak="2" Offset="3" Geometry="R1"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
              <DMXChannel DMXBreak="2" Offset="4" Geometry="R2"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
              <DMXChannel DMXBreak="2" Offset="5" Geometry="R2"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
              <DMXChannel DMXBreak="2" Offset="6" Geometry="R2"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
            </DMXChannels></DMXMode></DMXModes>
          </FixtureType>
        </GDTF>
        """;


    /// <summary>A pixel bar as GDTF's geometry references lay one out: a "Pixel" template, four references, its channels "Overwrite".</summary>
    private const string RefBar = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2">
          <FixtureType Name="RefBar" Manufacturer="Acme">
            <Geometries>
              <Geometry Name="Body">
                <GeometryReference Name="Pixel 1" Geometry="Pixel"><Break DMXBreak="1" DMXOffset="2"/></GeometryReference>
                <GeometryReference Name="Pixel 2" Geometry="Pixel"><Break DMXBreak="1" DMXOffset="5"/></GeometryReference>
                <GeometryReference Name="Pixel 3" Geometry="Pixel"><Break DMXBreak="1" DMXOffset="8"/></GeometryReference>
                <GeometryReference Name="Pixel 4" Geometry="Pixel"><Break DMXBreak="1" DMXOffset="11"/></GeometryReference>
              </Geometry>
              <Beam Name="Pixel" BeamAngle="15"/>
            </Geometries>
            <DMXModes><DMXMode Name="13ch" Geometry="Body"><DMXChannels>
              <DMXChannel DMXBreak="1" Offset="1" Geometry="Body"><LogicalChannel Attribute="Dimmer"/></DMXChannel>
              <DMXChannel DMXBreak="Overwrite" Offset="1" Geometry="Pixel"><LogicalChannel Attribute="ColorAdd_R"/></DMXChannel>
              <DMXChannel DMXBreak="Overwrite" Offset="2" Geometry="Pixel"><LogicalChannel Attribute="ColorAdd_G"/></DMXChannel>
              <DMXChannel DMXBreak="Overwrite" Offset="3" Geometry="Pixel"><LogicalChannel Attribute="ColorAdd_B"/></DMXChannel>
            </DMXChannels></DMXMode></DMXModes>
          </FixtureType>
        </GDTF>
        """;

    private const string Par = """
        <?xml version="1.0" encoding="UTF-8"?>
        <GDTF DataVersion="1.2"><FixtureType Name="Par" Manufacturer="Acme">
          <Geometries><Geometry Name="Body"><Beam Name="Beam" BeamAngle="25"/></Geometry></Geometries>
          <DMXModes><DMXMode Name="1ch" Geometry="Body"><DMXChannels>
            <DMXChannel DMXBreak="1" Offset="1" Geometry="Beam"><LogicalChannel Attribute="Dimmer"/></DMXChannel>
            <DMXChannel DMXBreak="1" Offset="" Geometry="Beam"><LogicalChannel Attribute="Zoom"/></DMXChannel>
          </DMXChannels></DMXMode></DMXModes></FixtureType></GDTF>
        """;

    private const string Focus1 = "11111111-2222-4333-8444-555555555555";
    private const string Focus2 = "21111111-2222-4333-8444-555555555555";
    private const string Wash1 = "A1111111-2222-4333-8444-555555555555";

    /// <summary>
    /// A plot: a ring wash hung 8 m up (its Z up: the beam down) aimed at a point on the floor 4 m
    /// downstage and 3 m left, its focus point inside a group 1 m over; a par hung beside it whose focus
    /// is off its beam; a video screen scaled twice; a second ring wash without its second break.
    /// </summary>
    private static readonly string Scene = $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <GeneralSceneDescription verMajor="1" verMinor="6" provider="Acme" providerVersion="1">
          <Scene>
            <AUXData><Position name="FrontTruss" uuid="99999999-2222-4333-8444-555555555555"/></AUXData>
            <Layers><Layer name="Rig" uuid="B1111111-2222-4333-8444-555555555555"><ChildList>
              <Fixture name="Wash 1" uuid="{{Wash1}}"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,0,8000}</Matrix>
                <GDTFSpec>Acme@Ring</GDTFSpec><GDTFMode>Two breaks</GDTFMode>
                <Focus>{{Focus1.Replace("-", "").Insert(8, " ")}}</Focus>
                <DMXInvertPan>true</DMXInvertPan>
                <Position>99999999-2222-4333-8444-555555555555</Position>
                <FixtureID>101</FixtureID><FixtureIDNumeric>101</FixtureIDNumeric>
                <Addresses><Address break="0">1</Address><Address break="1">2.1</Address></Addresses></Fixture>
              <Fixture name="Par 1" uuid="C1111111-2222-4333-8444-555555555555"><Matrix>{1,0,0}{0,1,0}{0,0,1}{2000,0,8000}</Matrix>
                <GDTFSpec>Acme@Par.gdtf</GDTFSpec><GDTFMode>1ch</GDTFMode>
                <Focus>{{Focus2}}</Focus>
                <CustomCommands><CustomCommand>Beam_Zoom.Zoom.Zoom 1,f 44.000000</CustomCommand></CustomCommands>
                <FixtureIDNumeric>102</FixtureIDNumeric>
                <Addresses><Address break="0">20</Address></Addresses></Fixture>
              <Fixture name="Wash 2" uuid="D1111111-2222-4333-8444-555555555555"><Matrix>{1,0,0}{0,1,0}{0,0,1}{-2000,0,8000}</Matrix>
                <GDTFSpec>Acme@Ring</GDTFSpec><GDTFMode>Two breaks</GDTFMode>
                <FixtureIDNumeric>103</FixtureIDNumeric>
                <Addresses><Address break="0">30</Address></Addresses></Fixture>
              <GroupObject name="Marks" uuid="E1111111-2222-4333-8444-555555555555"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,0,1000}</Matrix>
                <ChildList>
                  <FocusPoint name="Centre" uuid="{{Focus1}}"><Matrix>{1,0,0}{0,1,0}{0,0,1}{-3000,-4000,-1000}</Matrix><Geometries/></FocusPoint>
                  <FocusPoint name="Off" uuid="{{Focus2}}"><Matrix>{1,0,0}{0,1,0}{0,0,1}{6000,0,-1000}</Matrix><Geometries/></FocusPoint>
                </ChildList></GroupObject>
              <VideoScreen name="LED wall" uuid="F1111111-2222-4333-8444-555555555555"><Matrix>{2,0,0}{0,2,0}{0,0,2}{0,6000,4000}</Matrix>
                <Geometries/><Addresses><Address break="0">3.1</Address></Addresses><FixtureIDNumeric>200</FixtureIDNumeric></VideoScreen>
            </ChildList></Layer></Layers>
          </Scene>
        </GeneralSceneDescription>
        """;

    private static byte[] Plot() => Zip(("GeneralSceneDescription.xml", Text(Scene)),
        ("Acme@Ring.gdtf", Zip(("description.xml", Text(RingWash)))),
        ("Acme@Par.gdtf", Zip(("description.xml", Text(Par)))));

    private static MvrFile.Import Read(byte[] mvr)
    {
        var import = MvrFile.Read(mvr, "plot.mvr", out string? error);
        Assert.True(import != null, error);
        Assert.Null(import!.Rig.Check());
        return import;
    }

    private static RigData.FixtureRow Named(RigData rig, string name) => rig.Fixtures.Single(f => f.Name == name);

    [Fact]
    public void A_fixture_is_aimed_at_its_focus_point_wherever_the_point_sits()
    {
        var import = Read(Plot());
        var wash = Named(import.Rig, "Wash 1");
        Assert.True(wash.FocusTilt > 0f && wash.FocusDistance > 0f, $"tilt {wash.FocusTilt}, distance {wash.FocusDistance}");
        // the point: MVR (-3, -4, 0) m in the group 1 m up, so the floor; ours (-3, 0, 4)
        var target = new Vector3(-3f, 0f, 4f);
        var pos = RigData.V(wash.Pos);
        var beam = MvrFile.Beam(RigData.V(wash.Up), RigData.V(wash.Home), wash.FocusPan, wash.FocusTilt);
        Assert.True(beam.Dot((target - pos).Normalized()) > 0.9999f, $"beam {beam} toward {(target - pos).Normalized()}");
        Assert.Equal(pos.DistanceTo(target), wash.FocusDistance, 3);
        // the yoke axis stays as the matrix gives it: hung, down
        Assert.True(RigData.V(wash.Up).Dot(Vector3.Down) > 0.999f);
        Assert.Contains(import.Notes, n => n.Contains("aimed at their focus point"));

        // the rig points there at pan and tilt centre
        var patch = new DmxPatch();
        import.Rig.PatchInto(patch);
        var f = patch.Fixtures[0];
        Assert.Equal(wash.FocusPan, f.HomePan, 3);
        Assert.Equal(wash.FocusTilt, f.HomeTilt, 3);

        // a par that does not move keeps its matrix, noted
        var par = Named(import.Rig, "Par 1");
        Assert.Equal(0f, par.FocusDistance);
        Assert.Contains(import.Notes, n => n.Contains("do not move"));
    }

    [Fact]
    public void Dmx_invert_reaches_the_fixture_and_goes_back_out()
    {
        var import = Read(Plot());
        Assert.True(Named(import.Rig, "Wash 1").InvertPan);
        Assert.False(Named(import.Rig, "Wash 1").InvertTilt);
        var patch = new DmxPatch();
        import.Rig.PatchInto(patch);
        Assert.True(patch.Fixtures[0].InvertPan);
        var back = Read(MvrFile.Write(import.Rig, "plot"));
        Assert.True(Named(back.Rig, "Wash 1").InvertPan);
    }

    [Fact]
    public void A_second_break_is_patched_at_its_own_address_or_noted()
    {
        var g = Gdtf.FromXml(XDocument.Parse(RingWash), null, out _)!;
        Assert.Equal(new[] { 1, 2 }, g.Breaks);
        Assert.Equal(8, g.Footprint);   // the first break's
        var ring = Gdtf.ToFixtureType(g, out string? why, 2);
        Assert.True(ring != null, why);
        Assert.Equal(FixtureKind.PixelBar, ring!.Kind);
        Assert.Equal(6, ring.Footprint);

        var import = Read(Plot());
        var wash = Named(import.Rig, "Wash 1");
        Assert.NotNull(wash.Breaks);
        var b = Assert.Single(wash.Breaks!);
        Assert.Equal(new[] { 2, b[1], 1, 0 }, b);   // break 2 at 2.1
        Assert.Null(Named(import.Rig, "Wash 2").Breaks);
        Assert.Contains(import.Notes, n => n.Contains("break 2 not patched"));

        var patch = new DmxPatch();
        import.Rig.PatchInto(patch);
        var cells = patch.Fixtures.Single(f => f.Type.Kind == FixtureKind.PixelBar);
        Assert.Equal((1, 0), (cells.Universe, cells.Address));
        Assert.Equal(RigGroup.PixelBars, cells.Group);
    }

    [Fact]
    public void Geometry_references_repeat_a_template_at_their_offsets()
    {
        var g = Gdtf.FromXml(XDocument.Parse(RefBar), null, out _)!;
        Assert.Equal(13, g.Footprint);
        var bar = Gdtf.ToFixtureType(g, out string? why);
        Assert.True(bar != null, why);
        Assert.Equal(FixtureKind.PixelBar, bar!.Kind);
        Assert.Equal(4, bar.Cells);
        Assert.Equal(Attr.Dimmer, bar.Channels[0]);
        Assert.Equal(1, bar.CellOffsets[0]);
    }

    [Fact]
    public void A_video_screen_becomes_our_screen_its_size_from_its_matrix()
    {
        var import = Read(Plot());
        var wall = Named(import.Rig, "LED wall");
        Assert.Equal(FixtureKind.Screen, (FixtureKind)import.Rig.Types[wall.Type].Kind);
        Assert.Equal((int)RigGroup.Screens, wall.Group);
        Assert.Equal(new Vector3(16f, 9f, 0f), RigData.V(wall.Screen));
        Assert.Equal((2, 0), (wall.Universe, wall.Address));
        // it faces -Y, the audience: +z ours
        Assert.True(RigData.V(wall.Home).Dot(Vector3.Back) > 0.999f, $"{RigData.V(wall.Home)}");
    }

    [Fact]
    public void A_custom_command_zoom_sets_a_fixed_heads_beam()
    {
        var import = Read(Plot());
        var par = import.Rig.Types[Named(import.Rig, "Par 1").Type];
        Assert.Equal(44f, par.BeamMin);
        Assert.Equal(44f, par.BeamMax);
        Assert.Equal(44f, MvrFile.CustomZoom(XElement.Parse("<F><CustomCommands><CustomCommand>Zoom,f 44</CustomCommand></CustomCommands></F>")));
        Assert.Null(MvrFile.CustomZoom(XElement.Parse("<F><CustomCommands><CustomCommand>Yoke_Pan.Pan.Pan 1,f 0</CustomCommand></CustomCommands></F>")));
    }

    [Fact]
    public void The_group_comes_from_the_position_first()
    {
        var import = Read(Plot());
        Assert.Equal((int)RigGroup.FrontTruss, Named(import.Rig, "Wash 1").Group);
    }

    [Fact]
    public void An_export_is_mvr_1_6_and_keeps_an_imported_rigs_gdtfs_names_and_ids()
    {
        var mvr = Plot();
        var import = Read(mvr);
        var rig = import.Rig;
        var originals = MvrFile.Gdtfs(mvr, rig.Types.Select(t => t.GdtfSpec).OfType<string>());
        Assert.Equal(2, originals.Count);
        var bytes = MvrFile.Write(rig, "plot", originals: s => originals.GetValueOrDefault(s));

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.All(zip.Entries, e => Assert.DoesNotContain('/', e.FullName));   // flat
        byte[] Entry(string name)
        {
            using var s = zip.GetEntry(name)!.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        // the GDTFs byte for byte, under their own names
        Assert.Equal(Zip(("description.xml", Text(RingWash))), Entry("Acme@Ring.gdtf"));
        Assert.Equal(Zip(("description.xml", Text(Par))), Entry("Acme@Par.gdtf"));
        Assert.Null(zip.GetEntry("UnitSport@Ring_Two_breaks.gdtf"));

        var doc = XDocument.Parse(Encoding.UTF8.GetString(Entry("GeneralSceneDescription.xml")));
        var root = doc.Root!;
        Assert.Equal("1", (string?)root.Attribute("verMajor"));
        Assert.Equal("6", (string?)root.Attribute("verMinor"));
        Assert.False(string.IsNullOrEmpty((string?)root.Attribute("provider")));
        Assert.False(string.IsNullOrEmpty((string?)root.Attribute("providerVersion")));
        var classes = root.Descendants("Class").Select(c => (string)c.Attribute("uuid")!).ToHashSet();
        var positions = root.Descendants("AUXData").Elements("Position").Select(c => (string)c.Attribute("uuid")!).ToHashSet();
        var fixtures = root.Descendants("Fixture").Where(f => f.Parent?.Name == "ChildList").ToList();
        Assert.Equal(rig.Fixtures.Count, fixtures.Count);   // the second break rides on its fixture
        foreach (var f in fixtures)
        {
            Assert.False(string.IsNullOrEmpty(f.Element("FixtureID")?.Value));
            Assert.True(int.TryParse(f.Element("FixtureIDNumeric")?.Value, out _));
            Assert.Contains(f.Element("Classing")!.Value, classes);
            Assert.Contains(f.Element("Position")!.Value, positions);
        }
        var wash = fixtures.Single(f => (string?)f.Attribute("name") == "Wash 1");
        Assert.Equal(Wash1, (string?)wash.Attribute("uuid"));
        Assert.Equal("101", wash.Element("FixtureIDNumeric")!.Value);
        Assert.Equal("Acme@Ring", wash.Element("GDTFSpec")!.Value);
        Assert.Equal("Two breaks", wash.Element("GDTFMode")!.Value);
        Assert.Equal(new[] { "0", "1" }, wash.Element("Addresses")!.Elements("Address").Select(a => (string)a.Attribute("break")!));
        var focus = wash.Element("Focus")!.Value;
        Assert.Contains(root.Descendants("FocusPoint"), p => (string?)p.Attribute("uuid") == focus);
        // every uuid unique and none nil; no NaN nor infinity anywhere
        var uuids = root.Element("Scene")!.Descendants().Attributes("uuid").Select(a => a.Value).ToList();
        Assert.Equal(uuids.Count, uuids.Distinct().Count());
        Assert.All(uuids, u => Assert.True(Guid.TryParse(u, out var g) && g != Guid.Empty, u));
        Assert.DoesNotContain("NaN", doc.ToString());
        Assert.DoesNotContain("Infinity", doc.ToString());

        // and back: the same fixtures, breaks, focus and invert
        var back = Read(bytes);
        var w = Named(back.Rig, "Wash 1");
        Assert.Equal(Wash1, w.Uuid);
        Assert.Equal(101, w.FixtureId);
        Assert.NotNull(w.Breaks);
        Assert.Equal(Named(rig, "Wash 1").FocusTilt, w.FocusTilt, 2);
        Assert.Equal(Named(rig, "Wash 1").FocusPan, w.FocusPan, 2);
    }

    [Fact]
    public void Without_the_originals_each_break_is_a_fixture_of_its_own()
    {
        var import = Read(Plot());
        var back = Read(MvrFile.Write(import.Rig, "plot"));
        Assert.Equal(import.Rig.Fixtures.Count + 1, back.Rig.Fixtures.Count);
        Assert.Contains(back.Rig.Fixtures, f => f.Name == "Wash 1 break 2" && f.Universe == 1 && f.Address == 0);
    }

    [Fact]
    public void A_rig_with_bad_details_is_refused()
    {
        var rig = Read(Plot()).Rig;
        string? With(Action<RigData.FixtureRow> bad)
        {
            var copy = RigData.Unpack(rig.Pack(), out _)!;
            bad(copy.Fixtures[0]);
            return copy.Check();
        }
        Assert.Null(With(_ => { }));
        Assert.NotNull(With(f => f.FocusTilt = float.NaN));
        Assert.NotNull(With(f => f.FocusDistance = 5000f));
        Assert.NotNull(With(f => f.Uuid = "00000000-0000-0000-0000-000000000000"));
        Assert.NotNull(With(f => f.Uuid = "not a uuid"));
        Assert.NotNull(With(f => f.Breaks = new List<int[]> { new[] { 2, 99, 0, 0 } }));
        Assert.NotNull(With(f => f.Breaks = new List<int[]> { new[] { 2, 0, 0, 510 } }));
        Assert.NotNull(With(f => f.Breaks = Enumerable.Range(0, RigData.MaxBreaks + 1).Select(_ => new[] { 2, 0, 0, 0 }).ToList()));
        Assert.NotNull(With(f => f.Home = new[] { float.PositiveInfinity, 0f, 0f }));
        Assert.NotNull(With(f => f.Screen = new[] { 1000f, 1f, 0f }));
        var withName = RigData.Unpack(rig.Pack(), out _)!;
        withName.Types[0].GdtfSpec = new string('a', 300);
        Assert.NotNull(withName.Check());
    }
}
