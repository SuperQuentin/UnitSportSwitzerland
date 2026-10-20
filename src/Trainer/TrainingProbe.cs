using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Trainer;

/// <summary>
/// <c>--trainingcheck --trainer-sim --world fixture --chunks fixture:hairpin</c> (#901): a whole training,
/// offline, on RideLink's simulated rig (a trainer, a strap and a power meter speaking real GATT).
/// It writes a GPX of the first 600 m of the fixture road (bends and 7% down), loads it in the
/// session, connects the rig, starts, and rides it to the end. The checks: the course snaps, the
/// bike rides on the rig's watts, stays on the road without anyone steering, and the trainer is
/// sent the road's grade halved by the 50% difficulty. Read the <c>[trainingcheck]</c> lines.
/// </summary>
public partial class TrainingProbe : Node
{
    public static bool Requested => CmdArgs.Has("--trainingcheck");

    private readonly Func<FootPlayer?> _local;
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private int _failed;

    public TrainingProbe(Func<FootPlayer?> local, ChunkManager chunks, WorldOrigin origin)
    {
        _local = local;
        _chunks = chunks;
        _origin = origin;
        Name = "TrainingProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[trainingcheck] {what}");

    private void Check(bool ok, FormattableString what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {FormattableString.Invariant(what)}");
        if (!ok) _failed++;
    }

    private void Finish(string? fatal = null)
    {
        if (fatal != null) Log($"RESULT: FAILED {fatal}");
        else Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED {_failed} check(s)");
        GetTree().Quit(fatal == null && _failed == 0 ? 0 : 1);
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1200 && (me == null || !me.IsOnFloor()); i++)
        {
            me = _local();
            // the world opens on the spectator camera: on foot, as a player would press it
            if (me == null && i % 50 == 25)
                foreach (bool down in new[] { true, false })
                    Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = down });
            await Wait(0.1);
        }
        var session = TrainingSession.Instance;
        var link = TrainerLink.Instance;
        if (me == null || session == null || link == null || _chunks.Source is not { } source)
        {
            Finish($"missing: player {me != null}, session {session != null}, link {link != null}, terrain {_chunks.Source != null}");
            return;
        }

        // the course: the fixture's road from the spawn, 600 m of it, as a GPX in WGS84
        var road = await RaceRoute.BuildAsync(source, _origin, me.GlobalPosition);
        if (road == null) { Finish("no road at the spawn"); return; }
        string path = ProjectSettings.GlobalizePath("res://test_output/training/course.gpx");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var gpx = new StringBuilder("<?xml version=\"1.0\"?>\n<gpx version=\"1.1\" creator=\"trainingcheck\"><trk><name>Fixture hairpins</name><type>cycling</type><trkseg>\n");
        float last = -100;
        for (int i = 0; i < road.Centre.Count && road.Arc[i] <= 600; i++)
        {
            if (road.Arc[i] - last < 8) continue;
            last = road.Arc[i];
            var g = _origin.ToGlobal(road.Centre[i]);
            var (lat, lon) = SwissProjection.ToWgs84(g.E, g.N);
            gpx.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"{lat:0.0000000}\" lon=\"{lon:0.0000000}\"><ele>{g.Alt:0.0}</ele></trkpt>\n");
        }
        gpx.Append("</trkseg></trk></gpx>\n");
        System.IO.File.WriteAllText(path, gpx.ToString());

        // the rig: found, connected, its watts arriving
        Check(link.Available, $"a transport (the simulator) is there");
        link.Scan(2);
        await Wait(3);
        foreach (var d in link.Found.ToList()) link.Connect(d);
        for (int i = 0; i < 100 && !(link.PowerLive && link.HasTrainer); i++) await Wait(0.1);
        Check(link.PowerLive && link.HasTrainer, $"the rig connected: {link.Found.Count} found, {link.Watts} W, trainer {link.HasTrainer}");

        session.Load(path);
        for (int i = 0; i < 300 && session.State == TrainingSession.Phase.Loading; i++) await Wait(0.1);
        Check(session.State == TrainingSession.Phase.Ready, $"the course loaded: {session.Status}");
        Check(session.Status.StartsWith("On the roads", StringComparison.Ordinal), $"it snapped onto the fixture road");
        Check(session.CourseKm is > 0.5 and < 0.7, $"its length {session.CourseKm:0.000} km");

        session.Start();
        for (int i = 0; i < 300 && session.State != TrainingSession.Phase.Riding; i++) await Wait(0.1);
        Check(session.State == TrainingSession.Phase.Riding && me.Ride == RideKind.RoadBike, $"riding on the road bike ({session.State}, {me.Ride})");

        var trainer = link.SimRig?.Devices.OfType<RideLink.Simulation.SimulatedTrainer>().FirstOrDefault();
        float worstOff = 0, top = 0;
        double minSent = 0, minFelt = 0;
        int samples = 0, onWatts = 0;
        for (int i = 0; i < 1800 && session.State == TrainingSession.Phase.Riding; i++)
        {
            await Wait(0.1);
            worstOff = Mathf.Max(worstOff, session.OffCourse);
            top = Mathf.Max(top, me.RideSpeed);
            if (me.Vehicle is Bicycle bike && link.PowerLive)
            {
                samples++;
                if (Mathf.Abs(bike.RiderWatts - link.Watts) < 1) onWatts++;
            }
            if (link.GradeSent is { } sent) minSent = Math.Min(minSent, sent);
            if (trainer != null) minFelt = Math.Min(minFelt, trainer.GradeFraction);
        }
        Check(session.State == TrainingSession.Phase.Finished, $"finished the course: {session.Status} ({session.Along:0} m, {session.RideSeconds:0} s)");
        Check(samples > 0 && onWatts >= samples * 0.95, $"the bike rode on the rig's watts ({onWatts}/{samples} samples)");
        Check(worstOff < 4f, $"held on the road with nobody steering: worst {worstOff:0.0} m off the centreline, top {top * 3.6f:0} km/h");
        // 7% down at 50% difficulty: about -3.5%, as the trainer itself decoded it from the wire
        Check(minSent < -0.02 && minSent > -0.05, $"the descent sent as SIM at half: steepest {minSent * 100:0.0}%");
        Check(trainer != null && Math.Abs(minFelt - minSent) < 0.002, $"the trainer decoded the same off the wire: steepest {minFelt * 100:0.0}%");
        Finish();
    }
}
