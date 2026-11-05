using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Navigation;
using UnitSport.Adventure.Rewards;
using UnitSport.Core;
using UnitSport.Movie;
using UnitSport.Net;
using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The saves in Earth positions (#1503, G2 A2b): every store that held an LV95 position or a grid-north heading, from
/// a file as the game wrote it before (the fixtures below), converted in place to ECEF places and true-north headings,
/// back to the same place within 0.1 mm and the same heading within 1e-6 rad, and a second load changes nothing. At
/// Geneva, where grid north is 0.95° off true north, so a heading left on the grid would show.
/// </summary>
public class EarthSavesTests
{
    private const double E = 2_500_123.25, N = 1_118_456.75, Alt = 402.5;
    private const float Yaw = 1.25f;

    private static readonly GlobalPos Here = GlobalPos.FromLv95(E, N, Alt);

    private static void Near(double e, double n, double alt, GlobalPos got)
    {
        var (ge, gn, gh) = got.Lv95;
        Assert.True(Math.Abs(ge - e) < 1e-4 && Math.Abs(gn - n) < 1e-4 && Math.Abs(gh - alt) < 1e-4, $"{e}/{n}/{alt} came back as {got}");
    }

    private static GlobalPos AtOf(JsonNode o) => SavedPlace.Of(o[SavedPlace.At]!);

    /// <summary>The true heading saved at <paramref name="o"/>'s place: the grid one back within 1e-6 rad, and not the grid one itself.</summary>
    private static void TrueYawOf(JsonNode o, float grid)
    {
        var at = AtOf(o);
        float t = o[SavedPlace.YawTrue]!.GetValue<float>();
        Assert.Equal(grid, Lv95Grid.GridYaw(at, t), 1e-6f);
        Assert.True(Math.Abs(EnuAxes.Wrap(t - grid)) > 0.01f, $"at Geneva true north is ~0.9° off the grid's, got {t} for {grid}");
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ---- the shared shape -------------------------------------------------------------------------------------------

    [Fact]
    public void A_place_is_x_y_z_ECEF_and_comes_back_to_the_tenth_of_a_millimetre()
    {
        var node = SavedPlace.Node(Here);
        Assert.Equal(new[] { "x", "y", "z" }, node.AsObject().Select(kv => kv.Key));
        var (x, y, z) = Here.ToWire();
        Assert.Equal(x, node["x"]!.GetValue<double>());
        Assert.True(Math.Sqrt(x * x + y * y + z * z) > 6.3e6, "metres from the Earth's centre");
        Near(E, N, Alt, SavedPlace.Of(node));
        // on any record: the converter is on the type
        var json = JsonSerializer.Serialize(new { At = Here });
        Assert.Contains("\"x\":", json);
        Assert.DoesNotContain("\"E\":", json);
    }

    [Fact]
    public void A_place_saved_and_loaded_again_and_again_stays_put()
    {
        // the stores load and save their records many times: LV95 to ECEF and back must not creep
        var p = Here;
        for (int i = 0; i < 200; i++) p = SavedPlace.Of(JsonNode.Parse(SavedPlace.Node(p).ToJsonString())!);
        Near(E, N, Alt, p);
    }

    // ---- the stores read by Godot nodes (EarthSaves) -----------------------------------------------------------------

    // user://structures/server.json as Structures.Save wrote it before A2b (JsonStore.Indented)
    private const string OldStructures = """
        {
          "Next": 4,
          "Structures": [
            {
              "Id": 3,
              "E": 2500123.25,
              "N": 1118456.75,
              "Altitude": 402.5,
              "Yaw": 1.25,
              "Owner": "pk:9f2c",
              "OwnerName": "Anna",
              "Pieces": [ { "Slot": [ 0, 0, 0, 0, 0 ], "Kind": "Floor", "Dir": 0, "Material": "Wood", "Grounded": true, "Owner": "pk:9f2c", "OwnerName": "Anna", "Damage": 0 } ]
            }
          ]
        }
        """;

    [Fact]
    public void Structures_move_to_an_Earth_corner_and_a_true_heading()
    {
        var root = Parse(OldStructures);
        Assert.True(EarthSaves.Structures(root));
        Assert.Equal(EarthSaves.Current, SavedPlace.VersionOf(root));
        var s = root["Structures"]![0]!;
        foreach (var gone in new[] { "E", "N", "Altitude", "Yaw" }) Assert.Null(s[gone]);
        Near(E, N, Alt, AtOf(s));
        TrueYawOf(s, Yaw);
        Assert.Equal("Floor", s["Pieces"]![0]!["Kind"]!.GetValue<string>());
        string once = root.ToJsonString();
        Assert.False(EarthSaves.Structures(root));
        Assert.Equal(once, root.ToJsonString());
    }

    // user://placed/server.json before A2b: a flag turned on the grid, and a zipline whose payload is its high end
    private const string OldPlaced = """
        {
          "Next": 9,
          "Objects": [
            { "Id": 7, "Kind": "Flag", "Owner": "pk:9f2c", "E": 2500123.25, "N": 1118456.75, "Altitude": 402.5,
              "Rotation": [ 0, 0.5646425, 0, 0.8253356 ], "Payload": "" },
            { "Id": 8, "Kind": "Zipline", "Owner": "pk:9f2c", "E": 2500100, "N": 1118400, "Altitude": 390,
              "Rotation": [ 0, 0, 0, 1 ], "Payload": "2500160.00;1118450.00;420.00" }
          ],
          "OwnerNames": { "pk:9f2c": "Anna" }
        }
        """;

    [Fact]
    public void Placed_objects_move_to_an_Earth_place_a_local_turn_and_an_ECEF_zipline()
    {
        var root = Parse(OldPlaced);
        Assert.True(EarthSaves.Placed(root));
        var flag = root["Objects"]![0]!;
        Assert.Null(flag["E"]);
        Assert.Null(flag["Rotation"]);
        Near(E, N, Alt, AtOf(flag));
        var turn = flag[SavedPlace.Turn]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
        var back = SavedPlace.GridTurn(AtOf(flag), turn)!.Value;
        var grid = new Quaternion(0, 0.5646425f, 0, 0.8253356f);
        Assert.True(Math.Abs(back.Dot(grid)) > 1 - 1e-6f, $"{grid} came back as {back}");
        // the turn about up in the local ENU is the true heading: the grid's turned by the convergence
        Assert.Equal(Lv95Grid.TrueYaw(AtOf(flag), grid.GetEuler().Y), new Quaternion(turn[0], turn[1], turn[2], turn[3]).GetEuler().Y, 1e-5f);

        var zip = root["Objects"]![1]!;
        string payload = zip["Payload"]!.GetValue<string>();
        Assert.StartsWith("4", payload);   // ECEF x, about 4 300 km at Geneva
        Assert.Equal("2500160.00;1118450.00;420.00", EarthSaves.ZipFromEarth(payload));
        Assert.Equal(payload, EarthSaves.ZipToEarth(EarthSaves.ZipFromEarth(payload)));
        Assert.Equal("Anna", root["OwnerNames"]!["pk:9f2c"]!.GetValue<string>());

        string once = root.ToJsonString();
        Assert.False(EarthSaves.Placed(root));
        Assert.Equal(once, root.ToJsonString());
    }

    // a photo's sidecar, user://photos/polaroid/<id>.json, before A2b
    private const string OldPhoto = """
        {
          "Id": "0123456789abcdef",
          "E": 2500123.3,
          "N": 1118456.8,
          "Altitude": 402.5,
          "Taken": "2026-10-30T14:02:11",
          "FocalMm": 35,
          "Source": "C:/Users/x/AppData/Roaming/Godot/app_userdata/UnitSportSwitzerland/photos/shot.png",
          "Spot": ""
        }
        """;

    [Fact]
    public void A_photos_sidecar_moves_to_an_Earth_place()
    {
        var root = Parse(OldPhoto);
        Assert.True(EarthSaves.Photo(root));
        Assert.Null(root["E"]);
        Near(2500123.3, 1118456.8, 402.5, AtOf(root));
        Assert.Equal("2026-10-30T14:02:11", root["Taken"]!.GetValue<string>());
        Assert.False(EarthSaves.Photo(root));
    }

    // user://settings.json (camelCase) before A2b: a landing, and no host spawn picked (zeros)
    private const string OldSettings = """
        {
          "renderDistanceRings": 6,
          "lastLandingE": 2500123.25,
          "lastLandingN": 1118456.75,
          "hostSpawnE": 0,
          "hostSpawnN": 0,
          "hostTeleport": true
        }
        """;

    [Fact]
    public void Settings_keep_the_landing_as_an_Earth_place_and_drop_an_unset_spawn()
    {
        var root = Parse(OldSettings);
        Assert.True(EarthSaves.Settings(root));
        Assert.Null(root["lastLandingE"]);
        Assert.Null(root["hostSpawnE"]);
        Assert.Null(root["hostSpawn"]);
        Near(E, N, 0, SavedPlace.Of(root["lastLanding"]!));
        Assert.Equal(6, root["renderDistanceRings"]!.GetValue<int>());
        string once = root.ToJsonString();
        Assert.False(EarthSaves.Settings(root));
        Assert.Equal(once, root.ToJsonString());
    }

    // ---- the plain C# stores ----------------------------------------------------------------------------------------

    // user://sleepers/server.json before A2b: the records as they were, LV95 and a grid heading
    private const string OldSleepers = """
        {
          "Next": 3,
          "Sleepers": [
            { "Id": 2, "Key": "pk:9f2c", "Name": "Anna", "E": 2500123.25, "N": 1118456.75, "Alt": 402.5, "Yaw": 1.25,
              "Rider": 5, "Outfit": 123456789, "Appearance": 42, "Headwear": 3 }
          ]
        }
        """;

    [Fact]
    public void Sleepers_move_to_an_Earth_place_and_a_true_heading()
    {
        var book = SleeperBook.FromJson(OldSleepers, out bool migrated);
        Assert.True(migrated);
        var s = Assert.Single(book.All);
        Near(E, N, Alt, s.At);
        Assert.Equal(Yaw, s.Yaw);
        Assert.Equal(("Anna", 5, 123456789L, 42, 3), (s.Name, s.Rider, s.Outfit, s.Appearance, s.Headwear));

        string json = book.ToJson();
        var saved = Parse(json)["Sleepers"]![0]!;
        Assert.Null(saved["E"]);
        TrueYawOf(saved, Yaw);
        var again = SleeperBook.FromJson(json, out bool twice);
        Assert.False(twice);
        var t = Assert.Single(again.All);
        Near(E, N, Alt, t.At);
        Assert.Equal(Yaw, t.Yaw, 1e-6f);
        Assert.Equal(3, again.Lay(t with { Key = "k2" }, out _).Id);
    }

    // <containers>/awake.json before A2b: the bare dictionary of AwakeSlot
    private const string OldAwake = """
        {"12_34|0":{"Since":1761830000.5,"E":2500123.25,"N":1118456.75,"Yaw":1.25,"KindId":70,"Train":0,"Load":0.5},"12_34|1":{"Since":1761830001,"E":null,"N":null,"Yaw":0,"KindId":-1,"Train":0,"Load":0}}
        """;

    [Fact]
    public void Awake_slots_move_to_an_Earth_bay_and_a_true_heading()
    {
        var slots = AwakeSlots.FromJson(OldAwake, out bool old);
        Assert.True(old);
        Assert.Equal(E, slots["12_34|0"].E);
        string json = AwakeSlots.ToJson(slots);
        var file = Parse(json);
        Assert.Equal(2, SavedPlace.VersionOf(file));
        var bay = file["slots"]!["12_34|0"]!;
        Assert.Null(bay["E"]);
        Near(E, N, 0, AtOf(bay));
        TrueYawOf(bay, Yaw);
        Assert.Null(file["slots"]!["12_34|1"]![SavedPlace.At]);

        var back = AwakeSlots.FromJson(json, out bool twice);
        Assert.False(twice);
        var b = back["12_34|0"];
        Assert.True(Math.Abs(b.E!.Value - E) < 1e-4 && Math.Abs(b.N!.Value - N) < 1e-4);
        Assert.Equal(Yaw, b.Yaw, 1e-6f);
        Assert.Equal((70, 0, 0.5f, 1761830000.5), (b.KindId, b.Train, b.Load, b.Since));
        Assert.Null(back["12_34|1"].E);
        Assert.True(ContainerRules.BackInBay(b, Here, Yaw, 70, 0, 0.5f, pristine: true));
    }

    // user://versteckis/history.json and user://br/history.json before A2b: LV95 pairs
    private const string OldHistory = """
        [
          [ 2500123.25, 1118456.75 ],
          [ 2600000, 1200000 ]
        ]
        """;

    [Fact]
    public void A_history_of_places_moves_to_Earth_places()
    {
        var places = SavedPlace.ReadPlaces(OldHistory, out bool old);
        Assert.True(old);
        Assert.Equal(2, places.Count);
        string json = SavedPlace.PlacesNode(places).ToJsonString();
        var again = SavedPlace.ReadPlaces(json, out bool twice);
        Assert.False(twice);
        Near(E, N, 0, again[0]);
        Near(2600000, 1200000, 0, again[1]);
    }

    // an adventure progress file (user://adventure/players/<key>.json) with the pins and travel sections at version 1
    private const string OldProgress = """
        {"v":1,"key":"pk:9f2c","name":"Anna","created":1761000000,"seen":1761830000,"sections":{
          "pins":{"v":1,"data":{"pins":[{"id":1,"e":2500123.25,"n":1118456.75,"icon":2,"note":"Fondue","shared":true,"created":1761800000}],"nextId":2}},
          "travel":{"v":1,"data":{"unlocked":["station:geneve"],"hasGa":true,"hasHalfFare":false,"lastTravel":0,"trips":3,"spent":40,"flag":{"e":2500123.25,"n":1118456.75,"alt":402.5,"placed":1761810000}}},
          "fog":{"v":1,"data":{"v":1,"mask":"abc"}}}}
        """;

    [Fact]
    public void Pins_and_the_travel_flag_move_to_Earth_places()
    {
        var p = AdventureBook.FromJson(OldProgress);
        var pins = p.Get(PinBook.Section);
        var travel = p.Get(TravelBook.Section);
        var pin = Assert.Single(pins.Pins);
        Near(E, N, 0, pin.At);
        Assert.Equal(("Fondue", (byte)2, true, 2), (pin.Note, pin.Icon, pin.Shared, pins.NextId));
        Near(E, N, Alt, travel.Flag!.At);
        Assert.Equal(1761810000, travel.Flag.Placed);
        // migrated: written back at version 2 at the next save
        Assert.True(p.NeedsSave);
        var file = AdventureBook.ToNode(p);
        Assert.Equal(2, file["sections"]!["pins"]!["v"]!.GetValue<int>());
        var savedPin = file["sections"]!["pins"]!["data"]!["pins"]![0]!;
        Assert.Null(savedPin["e"]);
        Near(E, N, 0, AtOf(savedPin));
        Near(E, N, Alt, AtOf(file["sections"]!["travel"]!["data"]!["flag"]!));
        // the fog's cells stay an LV95 lattice (re-keyed with the tiles at G2 D)
        Assert.Equal("abc", file["sections"]!["fog"]!["data"]!["mask"]!.GetValue<string>());

        var again = AdventureBook.FromJson(file.ToJsonString());
        Near(E, N, 0, again.Get(PinBook.Section).Pins[0].At);
        Assert.False(again.NeedsSave);
        // a save after that writes the same places (a record holds LV95 in memory until G2 A1: an ulp of ECEF apart at most)
        Near(E, N, Alt, AtOf(AdventureBook.ToNode(again)["sections"]!["travel"]!["data"]!["flag"]!));
        Assert.Equal(2, AdventureBook.ToNode(again)["sections"]!["travel"]!["v"]!.GetValue<int>());
    }

    // ---- binary stores ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_stored_GSH1_ghost_is_rewritten_as_GSH2_at_the_same_places()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0x31485347u); w.Write(3); w.Write(E); w.Write(N);
            w.Write(0f); w.Write(0f); w.Write(402.5f); w.Write(Yaw);
            w.Write(12f); w.Write(-5f); w.Write(404f); w.Write(1.3f);
            w.Write(40f); w.Write(-20f); w.Write(410f); w.Write(-2f);
        }
        var upgraded = GhostTrack.Upgrade(ms.ToArray())!;
        Assert.Null(GhostTrack.Upgrade(upgraded));
        var back = GhostTrack.Decode(upgraded)!;
        Assert.Equal(3, back.Count);
        Assert.True(Math.Abs(back[0].E - E) < 1e-4 && Math.Abs(back[0].N - N) < 1e-4, $"{back[0]}");
        // later samples are float offsets in the ENU at the first one: a millimetre within 50 m
        Assert.True(Math.Abs(back[2].E - (E + 40)) < 1e-3 && Math.Abs(back[2].N - (N - 20)) < 1e-3, $"{back[2]}");
        Assert.Equal(Yaw, back[0].Yaw, 1e-6f);
        Assert.Equal(-2f, back[2].Yaw, 1e-6f);
    }

    private static readonly string[] Props = { "RideKindId" };

    private static MovieProject Project()
    {
        var p = new MovieProject(Props) { Name = "Geneva" };
        var ring = new ReplayRing(30, Props.Length);
        for (int k = 0; k < 30; k++)
        {
            var s = new ActorState(Props.Length) { E = E + k, N = N - k * 0.5, Alt = Alt };
            s.F[Channels.Vel] = 30; s.F[Channels.Vel + 2] = 15;
            s.F[Channels.Yaw] = Yaw + k * 0.01f;
            s.F[Channels.DeckPos] = 0.5f; s.F[Channels.DeckYaw] = 0.25f;
            ring.Append(k / Channels.Rate, s);
        }
        int lane = p.LaneFor("5", "Anna", 5);
        p.AddTrack(lane, ring.Slice(double.NegativeInfinity, out _)!, 0);
        p.Camera.Set(new CameraKey { T = 0, E = E + 20, N = N + 10, Alt = Alt + 5, Qx = 0, Qy = 0.5646425f, Qz = 0, Qw = 0.8253356f }, -1);
        var chase = p.AddCamera();
        chase.Rig = ShotRig.Chase;
        chase.Target = lane;
        chase.Set(new CameraKey { T = 0, E = 3, N = 4, Alt = 1.5, Qw = 1 }, -1);
        var group = new WorldEntity { Parent = -1, Born = 0, Times = new[] { 0.0 }, Poses = new[] { WorldPose.Identity with { X = E, Y = N, Z = Alt, Rot = new Quaternion(0, 0.5646425f, 0, 0.8253356f) } } };
        var part = new WorldEntity { Parent = 0, Born = 0, Times = new[] { 0.0 }, Poses = new[] { WorldPose.Identity with { X = 1, Y = 2, Z = 3 } } };
        p.Worlds.Add(new WorldTrack(new[] { group, part }, Array.Empty<WorldLook>(), 1));
        return p;
    }

    [Fact]
    public void A_movie_older_than_version_7_reads_as_it_was_and_7_keeps_Earth_places()
    {
        var p = Project();
        var v6 = new MemoryStream();
        MovieFile.Write(p, v6, 6);
        var old = MovieFile.Read(new MemoryStream(v6.ToArray()), Props, out bool wasOld);
        Assert.True(wasOld);
        Assert.Equal(p.Tracks[0].Pos, old.Tracks[0].Pos);

        var v7 = new MemoryStream();
        MovieFile.Write(old, v7);
        var q = MovieFile.Read(new MemoryStream(v7.ToArray()), Props, out bool twice);
        Assert.False(twice);
        UnitSportSwitzerland.Tests.MovieTests.AssertSameTracks(p, q);
        // the frames on disk are ECEF, the velocity and heading local
        var earth = MovieFile.Earth(p.Tracks[0], toEarth: true);
        Assert.True(new Vector3((float)earth.Pos[0], (float)earth.Pos[1], (float)earth.Pos[2]).Length() > 6.3e6f);
        Assert.True(Math.Abs(EnuAxes.Wrap(earth.F[Channels.Yaw] - Yaw)) > 0.01f, "the heading is true north's");
        Assert.Equal(0.25f, earth.F[Channels.DeckYaw]);
        // a camera on an actor keys offsets in the actor's frame: untouched
        var chase = q.Cameras[1].Keys[0];
        Assert.Equal((3.0, 4.0, 1.5), (chase.E, chase.N, chase.Alt));
        // a group stands at a place, its parts in its frame
        var world = q.Worlds[0].Entities;
        Assert.True(Math.Abs(world[0].Poses[0].X - E) < 1e-4 && Math.Abs(world[0].Poses[0].Y - N) < 1e-4 && Math.Abs(world[0].Poses[0].Z - Alt) < 1e-4);
        Assert.True(Math.Abs(world[0].Poses[0].Rot.Dot(new Quaternion(0, 0.5646425f, 0, 0.8253356f))) > 1 - 1e-6f);
        Assert.Equal((1.0, 2.0, 3.0), (world[1].Poses[0].X, world[1].Poses[0].Y, world[1].Poses[0].Z));
        var onDisk = MovieFile.Earth(p.Worlds[0].Entities, toEarth: true);
        Assert.True(Math.Abs(onDisk[0].Poses[0].X) > 1e6 && onDisk[1].Poses[0].X == 1.0);
    }
}
