using System.IO;
using System.IO.Compression;
using System.Text;
using Godot;
using UnitSport.Concert;
using UnitSport.Concert.Mvr;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Video on imported screens (#975): a VideoScreen's size from its geometry, its sources, and what the rig carries of them.</summary>
public class MvrVideoTests
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

    /// <summary>An Ogg page holding Theora's identification header, padded: enough for the sniff.</summary>
    private static byte[] Theora()
    {
        var b = new byte[200];
        Encoding.ASCII.GetBytes("OggS").CopyTo(b, 0);
        b[28] = 0x80;
        Encoding.ASCII.GetBytes("theora").CopyTo(b, 29);
        return b;
    }

    private static string Screen(string name, string uuid, string at, string sources, string geometry = "<Geometry3D fileName=\"wall.3ds\"/>") => $$"""
        <VideoScreen name="{{name}}" uuid="{{uuid}}"><Matrix>{1,0,0}{0,1,0}{0,0,1}{{at}}</Matrix>
          <Geometries>{{geometry}}</Geometries><Sources>{{sources}}</Sources></VideoScreen>
        """;

    /// <summary>Five walls (a 4 x 0.2 x 2.25 m box each, but one by a symbol): a Theora file, an MP4, NDI, CITP, a missing file; and a projector.</summary>
    private static byte[] Plot() => Zip(("GeneralSceneDescription.xml", Text($$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <GeneralSceneDescription verMajor="1" verMinor="6" provider="Acme" providerVersion="1">
          <Scene>
            <AUXData><Symdef name="Wall" uuid="51111111-2222-4333-8444-555555555555"><ChildList>
              <Geometry3D fileName="wall.3ds"><Matrix>{1,0,0}{0,1,0}{0,0,1}{-2000,0,0}</Matrix></Geometry3D></ChildList></Symdef></AUXData>
            <Layers><Layer name="Video" uuid="B1111111-2222-4333-8444-555555555555"><ChildList>
              {{Screen("Theora wall", "A1111111-2222-4333-8444-555555555555", "{0,6000,4000}", "<Source linkedGeometry=\"Display\" type=\"File\">clip.ogv</Source>")}}
              {{Screen("MP4 wall", "A2111111-2222-4333-8444-555555555555", "{6000,6000,4000}", "<Source linkedGeometry=\"Display\" type=\"File\">clip.mp4</Source>")}}
              {{Screen("NDI wall", "A3111111-2222-4333-8444-555555555555", "{-6000,6000,4000}",
                  "<Source linkedGeometry=\"Display\" type=\"CITP\">media 1</Source><Source linkedGeometry=\"Display\" type=\"NDI\">STUDIO (Program)</Source>",
                  "<Symbol symdef=\"51111111-2222-4333-8444-555555555555\"/>")}}
              {{Screen("CITP wall", "A4111111-2222-4333-8444-555555555555", "{0,9000,4000}", "<Source linkedGeometry=\"Display\" type=\"CITP\">media 2</Source>", "")}}
              {{Screen("Lost wall", "A5111111-2222-4333-8444-555555555555", "{0,12000,4000}", "<Source linkedGeometry=\"Display\" type=\"File\">gone.ogv</Source>")}}
              <Projector name="Beamer" uuid="A6111111-2222-4333-8444-555555555555"><Matrix>{1,0,0}{0,1,0}{0,0,1}{0,0,6000}</Matrix><Geometries/>
                <Projections><Projection><Source linkedGeometry="Lens" type="NDI">Program</Source><ScaleHandeling>ScaleKeepRatio</ScaleHandeling></Projection></Projections></Projector>
            </ChildList></Layer></Layers>
          </Scene>
        </GeneralSceneDescription>
        """)), ("wall.3ds", MvrSceneTests.Box3ds(4000, 200, 2250, red: false)), ("clip.ogv", Theora()), ("clip.mp4", new byte[300]));

    private static MvrFile.Import Read(byte[] mvr)
    {
        var import = MvrFile.Read(mvr, "video.mvr", out string? error);
        Assert.True(import != null, error);
        Assert.Null(import!.Rig.Check());
        return import;
    }

    private static RigData.FixtureRow Named(RigData rig, string name) => rig.Fixtures.Single(f => f.Name == name);

    [Fact]
    public void A_screen_is_its_geometrys_size_and_shows_on_its_front_face_lifted_over_it()
    {
        var rig = Read(Plot()).Rig;
        var wall = Named(rig, "Theora wall");
        Assert.Equal(new Vector3(4f, 2.25f, 0f), RigData.V(wall.Screen));
        // the box's front (-Y, the audience) 1 cm nearer the audience, centred across and up: MVR (2000, 5990, 5125) mm
        var at = RigData.V(wall.Pos);
        Assert.True(at.DistanceTo(new Vector3(2f, 5.125f, -5.99f)) < 1e-3f, $"{at}");
        // a symbol's geometry, moved by its own matrix: centred on the screen's origin
        var ndi = RigData.V(Named(rig, "NDI wall").Pos);
        Assert.True(ndi.DistanceTo(new Vector3(-6f, 5.125f, -5.99f)) < 1e-3f, $"{ndi}");
        // no geometry: our 8 by 4.5 m wall at its origin, as before
        var citp = Named(rig, "CITP wall");
        Assert.Equal(new Vector3(8f, 4.5f, 0f), RigData.V(citp.Screen));
        Assert.True(RigData.V(citp.Pos).DistanceTo(new Vector3(0f, 4f, -9f)) < 1e-3f);
    }

    [Fact]
    public void A_theora_file_is_kept_by_its_hash_and_cached_from_the_archive()
    {
        var mvr = Plot();
        var rig = Read(mvr).Rig;
        var wall = Named(rig, "Theora wall");
        Assert.Equal((int)VideoSource.File, wall.VideoSource);
        Assert.Equal("clip.ogv", wall.VideoName);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Theora())), wall.VideoHash);
        var videos = MvrFile.Videos(mvr, rig);
        Assert.Single(videos);
        Assert.Equal(Theora(), videos[wall.VideoHash!]);
    }

    [Fact]
    public void Sources_the_game_cannot_play_are_kept_with_a_note_and_the_show_plays()
    {
        var import = Read(Plot());
        var rig = import.Rig;
        var mp4 = Named(rig, "MP4 wall");
        Assert.Equal((int)VideoSource.File, mp4.VideoSource);
        Assert.Null(mp4.VideoHash);
        Assert.Contains(import.Notes, n => n.StartsWith("MP4 wall:") && n.Contains("Theora"));
        Assert.Null(Named(rig, "Lost wall").VideoHash);
        Assert.Contains(import.Notes, n => n.StartsWith("Lost wall:") && n.Contains("not in the file"));
        var citp = Named(rig, "CITP wall");
        Assert.Equal(((int)VideoSource.Citp, "media 2"), (citp.VideoSource, citp.VideoName));
        Assert.Contains(import.Notes, n => n.StartsWith("CITP wall:") && n.Contains("not read"));
        // NDI after a CITP source: the first one the game shows
        var ndi = Named(rig, "NDI wall");
        Assert.Equal(((int)VideoSource.Ndi, "STUDIO (Program)", (string?)null), (ndi.VideoSource, ndi.VideoName, ndi.VideoHash));
        Assert.Contains(import.Notes, n => n.Contains("1 projector projection(s) not shown"));
    }

    [Fact]
    public void The_sources_survive_the_wire_and_the_server_refuses_bad_ones()
    {
        var rig = Read(Plot()).Rig;
        var back = RigData.Unpack(rig.Pack(), out string? error);
        Assert.True(back != null, error);
        var a = Named(rig, "Theora wall");
        var b = Named(back!, "Theora wall");
        Assert.Equal((a.VideoSource, a.VideoName, a.VideoHash), (b.VideoSource, b.VideoName, b.VideoHash));

        string? Bad(Action<RigData.FixtureRow> spoil, string name = "Theora wall")
        {
            var r = RigData.Unpack(rig.Pack(), out _)!;
            spoil(Named(r, name));
            return r.Check();
        }
        Assert.NotNull(Bad(f => f.VideoSource = 9));
        Assert.NotNull(Bad(f => f.VideoHash = "XYZ"));
        Assert.NotNull(Bad(f => f.VideoName = "a\u0007b"));
        Assert.NotNull(Bad(f => f.VideoName = new string('x', RigData.MaxVideoName + 1)));
        Assert.NotNull(Bad(f => f.VideoHash = a.VideoHash, "NDI wall"));
        Assert.NotNull(Bad(f => { f.VideoSource = 0; }));
        Assert.NotNull(Bad(f => f.Screen = null));
    }

    [Fact]
    public void Only_ogg_theora_is_taken_for_a_video_godot_plays()
    {
        Assert.True(MvrFile.IsTheora(Theora()));
        var vorbis = Theora();
        Encoding.ASCII.GetBytes("vorbis").CopyTo(vorbis, 29);
        Assert.False(MvrFile.IsTheora(vorbis));
        Assert.False(MvrFile.IsTheora(new byte[300]));
    }
}
