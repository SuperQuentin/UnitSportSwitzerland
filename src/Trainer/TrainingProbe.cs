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
/// <c>--trainingcheck junctions</c> on <c>fixture:junction</c>: the turns, the way back and ERG (<see cref="RunJunctions"/>).
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

    private void Shot(string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
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

        if (CmdArgs.Value("--trainingcheck", notFlag: true) == "junctions")
        {
            await RunJunctions(me, session, link, source);
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

        // as a player does it: the Training app open on the phone, Start, the trip to the start behind its screen
        var phone = Phone.PhoneController.Instance;
        phone?.Open(Phone.PhoneScreens.Page.Training);
        await Wait(1.5);
        Check(phone?.Screens.Shown == Phone.PhoneScreens.Page.Training, $"the Training app opens on the phone ({phone?.Screens.Shown})");
        Shot("training_1_app.png");
        session.Start();
        for (int i = 0; i < 300 && session.State != TrainingSession.Phase.Riding; i++) await Wait(0.1);
        for (int i = 0; i < 50 && phone?.IsOut == true; i++) await Wait(0.1);
        Check(phone?.IsOut != true, $"the phone went back in the pocket after the trip to the start");
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
                // the rig's 230 W wobbles by a few watts between a physics step and this read
                if (Mathf.Abs(bike.RiderWatts - link.Watts) < 20 && bike.RiderWatts > 205) onWatts++;
            }
            if (link.GradeSent is { } sent) minSent = Math.Min(minSent, sent);
            if (trainer != null) minFelt = Math.Min(minFelt, trainer.GradeFraction);
            if (i is 150 or 300 or 420)
            {
                Shot($"training_2_ride_{i}.png");
                Log(FormattableString.Invariant($"view at {i}: phone look {me.PhoneLook:F2} cover {me.PhoneCover:F2} backdrop {me.PhoneBackdropShown:F2} state {me.PhoneState}"));
            }
        }
        Check(session.State == TrainingSession.Phase.Finished, $"finished the course: {session.Status} ({session.Along:0} m, {session.RideSeconds:0} s)");
        Check(samples > 0 && onWatts >= samples * 0.95, $"the bike rode on the rig's watts ({onWatts}/{samples} samples)");
        Check(worstOff < 4f, $"held on the road with nobody steering: worst {worstOff:0.0} m off the centreline, top {top * 3.6f:0} km/h");
        // 7% down at 50% difficulty: about -3.5%, as the trainer itself decoded it from the wire
        Check(minSent < -0.02 && minSent > -0.05, $"the descent sent as SIM at half: steepest {minSent * 100:0.0}%");
        Check(trainer != null && Math.Abs(minFelt - minSent) < 0.002, $"the trainer decoded the same off the wire: steepest {minFelt * 100:0.0}%");
        Finish();
    }

    /// <summary>
    /// <c>--trainingcheck junctions --trainer-sim --world fixture --chunks fixture:junction</c>: a course east
    /// along the fixture's main road past its T junction. At the T the rider takes the road left (the steering's
    /// intent, <see cref="TrainingSession.Choose"/>), turns round on it, asks for the way back
    /// (<see cref="TrainingSession.Rejoin"/>), and finishes on the course. On the way, ERG on, +10 W and off,
    /// read back from the rig's trainer.
    /// </summary>
    private async Task RunJunctions(FootPlayer me, TrainingSession session, TrainerLink link, IChunkSource source)
    {
        // the fixture's main road: the Major class, east-west; the T is 700 m along it
        var at = _origin.ToGlobal(me.GlobalPosition);
        var net = await Gpx.RoadNetwork.LoadAsync(source, at.E - 2500, at.N - 2500, at.E + 2500, at.N + 2500, pad: 0);
        var main = net.Edges.Where(e => e.Class == RoadClass.Major).ToList();
        if (main.Count == 0) { Finish("no main road on the junction fixture"); return; }
        double e0 = main.Min(e => e.E.Min()), n0 = main[0].N[0];
        const double From = 560, To = 1150, Tee = 700;
        string path = ProjectSettings.GlobalizePath("res://test_output/training/junctions.gpx");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var gpx = new StringBuilder("<?xml version=\"1.0\"?>\n<gpx version=\"1.1\" creator=\"trainingcheck\"><trk><name>Fixture junction</name><type>cycling</type><trkseg>\n");
        for (double x = From; x <= To; x += 8)
        {
            var (lat, lon) = SwissProjection.ToWgs84(e0 + x, n0);
            gpx.Append(CultureInfo.InvariantCulture, $"<trkpt lat=\"{lat:0.0000000}\" lon=\"{lon:0.0000000}\"><ele>{main[0].Height[0]:0.0}</ele></trkpt>\n");
        }
        gpx.Append("</trkseg></trk></gpx>\n");
        System.IO.File.WriteAllText(path, gpx.ToString());

        link.Scan(2);
        await Wait(3);
        foreach (var d in link.Found.ToList()) link.Connect(d);
        for (int i = 0; i < 100 && !(link.PowerLive && link.HasTrainer); i++) await Wait(0.1);
        Check(link.PowerLive && link.HasTrainer, $"the rig connected ({link.Watts} W)");
        session.Load(path);
        for (int i = 0; i < 300 && session.State == TrainingSession.Phase.Loading; i++) await Wait(0.1);
        Check(session.State == TrainingSession.Phase.Ready, $"the course loaded: {session.Status}");
        session.Start();
        for (int i = 0; i < 300 && session.State != TrainingSession.Phase.Riding; i++) await Wait(0.1);
        Check(session.State == TrainingSession.Phase.Riding, $"riding ({session.State})");

        double X() => _origin.ToGlobal(me.GlobalPosition).E - e0;
        double Y() => _origin.ToGlobal(me.GlobalPosition).N - n0;

        // the T in sight: its roads, straight on taken
        for (int i = 0; i < 300 && session.NextJunction == null; i++) await Wait(0.1);
        var j = session.NextJunction;
        Check(j != null && j.Taken >= 0 && JunctionChoice.Pick(j, -1) >= 0,
            $"a junction ahead at x {X() + session.JunctionIn:0} with a road left: {(j == null ? "none" : string.Join(", ", j.Branches.Select(b => $"{b.Bearing:0}{(b.Taken ? "*" : "")}")))}");
        Check(session.OnCourse, $"on the course before it");
        // its arrows: a row over the junction, one per road, the one taken lit and pointing up (straight on)
        await Wait(0.3);
        var arrows = session.Arrows?.Showing.ToList() ?? new();
        var jAt = j == null ? Vector3.Zero : _origin.ToWorld(e0 + Tee, n0, main[0].Height[0]);
        Check(j != null && arrows.Count == j.Branches.Count && arrows.All(a => RaceRoute.Flat(a.At - jAt).Length() < 30f && a.At.Y - jAt.Y is > 2f and < 5f),
            $"{arrows.Count} arrows over the T: {string.Join(", ", arrows.Select(a => FormattableString.Invariant($"{RaceRoute.Flat(a.At - jAt).Length():0} m from it, {a.At.Y - jAt.Y:0.0} m up{(a.Taken ? " lit" : "")}")))}");
        Check(arrows.Count(a => a.Taken) == 1 && arrows.First(a => a.Taken).Points.Y > 0.9f, $"the lit one points up: straight on");
        session.Choose(-1);
        await Wait(0.5);
        arrows = session.Arrows?.Showing.ToList() ?? new();
        var lit = arrows.FirstOrDefault(a => a.Taken);
        Check(arrows.Count(a => a.Taken) == 1 && lit.Points.Z < -0.3f, $"left chosen: the lit arrow leans left, north (up {lit.Points.Y:0.00}, north {-lit.Points.Z:0.00})");
        Shot("training_j1_left.png");
        float worst = 0;
        bool near = false;
        for (int i = 0; i < 600 && Y() < 60; i++)
        {
            await Wait(0.1);
            if (!near && X() > Tee - 50) { near = true; Shot("training_j1b_arrows.png"); }
            worst = Mathf.Max(worst, session.OffCourse);
            if (i % 20 == 0) Log(FormattableString.Invariant($"  at x {X():0.0} y {Y():0.0}, {me.RideSpeed * 3.6f:0} km/h, way left {session.WayLeft:0} m, {session.OffCourse:0.0} m off it"));
        }
        Check(Y() >= 60 && Math.Abs(X() - Tee) < 15, $"took the road left: at x {X():0}, y {Y():0} (worst {worst:0.0} m off the way)");
        Check(!session.OnCourse, $"off the course on it");
        Shot("training_j2_off.png");

        // turn round, then the way back
        session.UTurn();
        await Wait(0.5);
        Check(me.RideSpeed < 1f, $"turned round: stopped ({me.RideSpeed * 3.6f:0} km/h)");
        session.Rejoin();
        Check(session.Rejoining, $"on the way back to the course");
        for (int i = 0; i < 600 && !session.OnCourse; i++)
        {
            await Wait(0.1);
            worst = Mathf.Max(worst, session.OffCourse);
            if (i % 20 == 0) Log(FormattableString.Invariant($"  back at x {X():0.0} y {Y():0.0}, {me.RideSpeed * 3.6f:0} km/h, way left {session.WayLeft:0} m, {session.OffCourse:0.0} m off it"));
        }
        Check(session.OnCourse && X() > Tee, $"back on the course east of the T: x {X():0}, y {Y():0}, at {session.Along:0} m of it");

        // ERG on the way to the finish: on at the step above the rider's power, +10 W, off back to SIM
        var trainer = link.SimRig?.Devices.OfType<RideLink.Simulation.SimulatedTrainer>().FirstOrDefault();
        link.SetErg(true);
        await Wait(1.5);
        Check(link.Erg && link.ErgTarget is >= 230 and <= 250 && trainer?.Mode == RideLink.Capabilities.ControlMode.Erg && trainer.TargetWatts == link.ErgTarget,
            $"ERG on at {link.ErgTarget} W; the trainer holds {trainer?.TargetWatts} W ({trainer?.Mode})");
        int was = link.ErgTarget;
        link.StepErg(1);
        await Wait(1.5);
        Check(link.ErgTarget == was + 10 && trainer?.TargetWatts == was + 10, $"+10 W: {link.ErgTarget} W, the trainer {trainer?.TargetWatts} W");
        link.SetErg(false);
        await Wait(2);
        Check(!link.Erg && trainer?.Mode == RideLink.Capabilities.ControlMode.Simulation && link.GradeSent != null,
            $"ERG off: the trainer back in {trainer?.Mode}, grade {link.GradeSent * 100:0.0}% sent");

        for (int i = 0; i < 900 && session.State == TrainingSession.Phase.Riding; i++)
        {
            await Wait(0.1);
            worst = Mathf.Max(worst, session.OffCourse);
        }
        Check(session.State == TrainingSession.Phase.Finished, $"finished: {session.Status}");
        Check(worst < 4f, $"held on the way throughout: worst {worst:0.0} m off it");
        Finish();
    }
}
