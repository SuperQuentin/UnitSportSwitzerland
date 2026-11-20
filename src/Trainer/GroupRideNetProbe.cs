using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Trainer;

/// <summary>
/// <c>--groupridecheck A|B</c> with <c>--connect</c> and <c>--trainer-sim</c> (driven by <c>tools/groupridenetcheck.sh</c>,
/// #1467): a group ride over loopback, each side checked on its own peer.
/// <list type="bullet">
/// <item>A writes a GPX of the hairpin fixture's first 600 m, loads it and hosts a group ride on it;</item>
/// <item>B finds it in the list (its name, length, host), joins: the course comes from the server, loads and starts;</item>
/// <item>both ride it on RideLink's simulated rig; each sees the group's standings with both riders, the other's course
/// metres growing, and the other's copy on a road bike;</item>
/// <item>both finish; the group closes once both have left.</item>
/// </list>
/// </summary>
public partial class GroupRideNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--groupridecheck");

    private readonly Func<FootPlayer?> _local;
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    public GroupRideNetProbe(ItemController items, Func<FootPlayer?> local, ChunkManager chunks, WorldOrigin origin)
        : base(items, "groupride", "GR")
    {
        _local = local;
        _chunks = chunks;
        _origin = origin;
    }

    public GroupRideNetProbe() : this(null!, () => null, null!, null!) { }

    protected override FootPlayer? Me => _local() ?? base.Me;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => Other() != null))
        {
            await Finish(0);
            return;
        }
        await Run(Me!);
        await Finish(3);
    }

    private void Shot(string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/" + file);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"{Log} screenshot {path}");
    }

    /// <summary>The other client's player, as drawn here.</summary>
    private FootPlayer? Other()
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != Me && !p.Npc && !p.Bot) return p;
        return null;
    }

    private async Task Run(FootPlayer me)
    {
        string other = _role == "A" ? "B" : "A";
        var session = TrainingSession.Instance;
        var link = TrainerLink.Instance;
        var groups = GroupRides.Instance;
        if (session == null || link == null || groups == null || _chunks.Source is not { } source)
        {
            Fail($"missing: session {session != null}, link {link != null}, groups {groups != null}, terrain {_chunks.Source != null}");
            return;
        }
        // the rig, as on a solo ride
        link.Scan(2);
        await Until(() => link.Found.Count > 0, 5);
        foreach (var d in link.Found.ToList()) link.Connect(d);
        Expect(await Until(() => link.PowerLive && link.HasTrainer, 10), $"the rig connected ({link.Watts} W)");

        if (_role == "A")
        {
            // the course: the fixture road from the spawn, 600 m of it, as a GPX
            var road = await RaceRoute.BuildAsync(source, _origin, me.GlobalPosition);
            if (road == null) { Fail("no road at the spawn"); return; }
            string path = ProjectSettings.GlobalizePath("res://test_output/training/Hairpin group.gpx");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var gpx = new StringBuilder("<?xml version=\"1.0\"?>\n<gpx version=\"1.1\" creator=\"groupridecheck\"><trk><name>Hairpin group</name><type>cycling</type><trkseg>\n");
            float last = -100;
            for (int i = 0; i < road.Centre.Count && road.Arc[i] <= 600; i++)
            {
                if (road.Arc[i] - last < 8) continue;
                last = road.Arc[i];
                var g = _origin.ToGlobal(road.Centre[i]);
                var (lat, lon) = Terrain.Format.SwissGeodesy.ToLatLon(g.Lv95.E, g.Lv95.N, g.Alt);
                gpx.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"{lat:0.0000000}\" lon=\"{lon:0.0000000}\"><ele>{g.Alt:0.0}</ele></trkpt>\n");
            }
            gpx.Append("</trkseg></trk></gpx>\n");
            System.IO.File.WriteAllText(path, gpx.ToString());
            session.Load(path);
            Expect(await Until(() => session.State == TrainingSession.Phase.Ready, 30), $"the course loaded: {session.Status}");
            Expect(groups.Host(session), "asked to host a group ride");
            Expect(await Until(() => groups.Joined != 0, 15), $"the server opened the group ({groups.Joined})");
            Say("hosted");
            if (!await Heard("B", "joined", 120)) { Fail("B never joined"); return; }
            session.Start();
        }
        else
        {
            if (!await Heard("A", "hosted", 120)) { Fail("A never hosted"); return; }
            Expect(await Until(() => groups.Open.Count > 0, 15), $"the group ride is listed ({groups.Open.Count})");
            var listed = groups.Open.FirstOrDefault();
            Expect(listed.Name == "Hairpin group" && listed.Km is > 0.5 and < 0.7 && listed.Host.Contains("GroupA", StringComparison.Ordinal) && listed.Riders == 1,
                FormattableString.Invariant($"listed as \"{listed.Name}\", {listed.Km:0.00} km, hosted by {listed.Host}, {listed.Riders} riding"));
            groups.Join(listed.Id);
            Expect(await Until(() => groups.Joined == listed.Id, 15), $"joined it ({groups.Joined})");
            Expect(await Until(() => session.State == TrainingSession.Phase.Riding, 90), $"its course came from the server, loaded and started ({session.State}: {session.Status})");
            Expect(session.CourseName == "Hairpin group" && session.CourseKm is > 0.5 and < 0.7, FormattableString.Invariant($"riding the same course: {session.CourseName}, {session.CourseKm:0.00} km"));
            Say("joined");
        }

        // the ride: the standings with both, the other's metres growing, its copy on a road bike
        Expect(await Until(() => session.State == TrainingSession.Phase.Riding, 60), $"riding ({session.State})");
        int mostRiders = 0;
        float otherFurthest = 0;
        bool otherOnBike = false, shot = false;
        string otherName = _role == "A" ? "GroupB" : "GroupA";
        for (int i = 0; i < 1500 && session.State == TrainingSession.Phase.Riding; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            mostRiders = Math.Max(mostRiders, groups.Standings.Count);
            foreach (var s in groups.Standings)
                if (s.Name.Contains(otherName, StringComparison.Ordinal)) otherFurthest = Math.Max(otherFurthest, s.Along);
            if (Other() is { } o && IsInstanceValid(o) && o.Ride == RideKind.RoadBike)
            {
                otherOnBike = true;
                // windowed: the other rider in view, once, a while into the ride (the HUD's group lines on it)
                if (!shot && session.Along > 120 && o.GlobalPosition.DistanceTo(me.GlobalPosition) < 25f)
                {
                    shot = true;
                    Shot($"training/groupride_{_role}.png");
                }
            }
            if (i % 50 == 0) GD.Print($"{Log} at {session.Along:0} m: {groups.StandingsText((float)session.Along).Replace("\n", " | ")}");
        }
        Expect(session.State == TrainingSession.Phase.Finished, $"finished the group ride: {session.Status}");
        Expect(mostRiders == 2, $"the standings held both riders ({mostRiders})");
        Expect(otherFurthest > 300, FormattableString.Invariant($"{other}'s course metres reached here: {otherFurthest:0} m"));
        Expect(otherOnBike, $"{other}'s copy rode a road bike here");
        Say("done");
        if (!await Heard(other, "done", 120)) { Fail($"{other} never finished"); return; }
        groups.Leave();
        Expect(await Until(() => groups.Open.Count == 0, 15), $"the group closed once both left ({groups.Open.Count} open)");
    }
}
