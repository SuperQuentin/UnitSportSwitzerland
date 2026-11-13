using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Gpx;

/// <summary>
/// <c>godot --path . -- --replaycheck --chunks fixture:straight --snaproads [--frame earth]</c> (#1752): the GPX replay
/// mode itself on the world's ground, in either frame. Writes <c>--gpxcheck</c>'s 1.5 km course as a .gpx file, opens it
/// as a player would (the session's <see cref="GpxSession.Load"/>, the replay mode), and checks in turn:
/// the runner's feet and the ribbon on loaded ground; Absolute Cinema's route survey (<see cref="Cinema.Corridor"/>)
/// holding tiles, with a height under nearly every metre of the course that agrees with the ground's; the road snap
/// reading the world's tiles; and the video exporter's warm-up settling on the world (then cancelled: a headless run
/// has no frames to grab). RESULT ok or FAILED with the faults.
/// </summary>
public partial class ReplayCheck : Node
{
    private const double StepTimeoutS = 180;
    private const double DrapedShareMin = 0.95, CorridorAgreeM = 3;

    private readonly IWorldGround _ground;
    private readonly WorldOrigin _origin;
    private readonly Func<GpxSession?> _session;
    private readonly Action<string> _open;
    private readonly List<string> _faults = new();

    private enum Step { Ground, Runner, Corridor, Snap, Export, Done }
    private Step _step;
    private ulong _stepStart = Time.GetTicksMsec();
    private GpxTrack? _track;

    public ReplayCheck(IWorldGround ground, WorldOrigin origin, Func<GpxSession?> session, Action<string> open)
    {
        _ground = ground;
        _origin = origin;
        _session = session;
        _open = open;
    }

    public static bool Requested() => CmdArgs.Has("--replaycheck");

    private double StepSeconds => (Time.GetTicksMsec() - _stepStart) / 1000.0;

    private void Next(Step step)
    {
        _step = step;
        _stepStart = Time.GetTicksMsec();
    }

    public override void _Process(double delta)
    {
        if (_step == Step.Done) return;
        // threads (the leaves, the survey, the matcher) on the wall clock (general/fast-checks)
        if (StepSeconds > StepTimeoutS)
        {
            Fault($"timeout after {StepTimeoutS} s waiting for {_step}");
            Finish();
            return;
        }
        var session = _session();
        switch (_step)
        {
            case Step.Ground:
            {
                var (e, n) = SpawnPoint.ParseTarget();
                if (session == null || !_ground.TryGetHeight(_origin.ToWorld(e, n, 0), out _)) return;
                _track = GpxCheck.Course(e, n);
                string path = WriteGpx(_track);
                GD.Print($"[replaycheck] opening {path} (a {_track.Length / 1000:F1} km course from LV95 {e:F0}/{n:F0})");
                _open(path);
                Next(Step.Runner);
                break;
            }
            case Step.Runner:
            {
                if (session == null || session.Race.Focused is not { } runner || session.Ribbon?.Mesh == null) return;
                if (session.Race.Time < 5) return;   // a few seconds in, the avatar on its course
                var foot = runner.FootWorld;
                if (!_ground.TryGetHeight(foot, out float g)) return;   // the ground there still streaming
                if (Mathf.Abs(foot.Y - g) > 3f) Fault($"the runner's feet {foot} are {foot.Y - g:F2} m off the ground");
                int over = RibbonOverGround(session.Ribbon);
                GD.Print($"[replaycheck] runner on the ground ({foot.Y - g:F2} m), {over} ribbon vertices over loaded ground");
                if (over == 0) Fault("no ribbon vertex over loaded ground");
                session.StartCinema();
                Next(Step.Corridor);
                break;
            }
            case Step.Corridor:
            {
                if (session?.LastCorridor is not { } corridor) return;
                CheckCorridor(session, corridor);
                session.Race.SetSnapToRoads(true);
                Next(Step.Snap);
                break;
            }
            case Step.Snap:
            {
                if (session == null || session.Race.Matching || StepSeconds < 0.5) return;
                string status = session.Race.SnapStatus;
                GD.Print($"[replaycheck] road snap: {status}");
                if (status == "no terrain source") Fault("the road snap found no tile source");
                // "--snaproads": the course runs on a road (a fixture's), so the snap must match it
                else if (CmdArgs.Has("--snaproads") && status != "on") Fault($"the road snap did not match the course's road: {status}");
                session.Race.SetSnapToRoads(false);
                session.Race.Speed = 30;
                session.Exporter.WarmupSeconds = 120;
                string folder = ProjectSettings.GlobalizePath("res://test_output/replaycheck/export");
                if (!session.ExportInto(folder, 10))
                {
                    Fault($"the export did not start: {session.Exporter.Status}");
                    Finish();
                    return;
                }
                Next(Step.Export);
                break;
            }
            case Step.Export:
            {
                if (session == null || !session.Exporter.PastWarmup) return;
                GD.Print($"[replaycheck] export warm-up {(session.Exporter.WarmupSettled ? "settled" : "ran out")} after {StepSeconds:F1} s");
                if (!session.Exporter.WarmupSettled) Fault("the exporter's warm-up never saw the world settle");
                session.Exporter.Cancel();
                Finish();
                break;
            }
        }
    }

    /// <summary>The survey: tiles, a height under the course's metres, and that height the ground's at the runner.</summary>
    private void CheckCorridor(GpxSession session, Cinema.Corridor corridor)
    {
        // the course as the session parsed it from the file: its own plane, which the survey is asked on
        if (session.Race.Focused is not { } focused)
        {
            Fault("no runner while the corridor came");
            return;
        }
        var track = focused.Active;
        int samples = 0, draped = 0;
        for (double d = 0; d <= track.Length; d += 25)
        {
            var (x, y, _, _, _) = track.Sample(d / track.Length * track.Duration);
            samples++;
            if (corridor.HeightAt(x, y) != null) draped++;
        }
        double share = samples == 0 ? 0 : draped / (double)samples;
        GD.Print($"[replaycheck] corridor: {corridor.TileCount} tiles, {corridor.BuildingCount} buildings, "
            + $"{draped}/{samples} course samples with a height, {session.PlannedEvents} events planned");
        if (corridor.TileCount == 0) Fault("the corridor holds no tile");
        if (share < DrapedShareMin) Fault($"only {share:P0} of the course has a corridor height");

        // the survey's height against the ground's, under the runner (a different source: the tiles read whole)
        if (_ground.TryGetHeight(focused.FootWorld, out float g))
        {
            var foot = focused.FootWorld;
            var place = _origin.ToGlobal(new Vector3(foot.X, g, foot.Z));
            var (px, py) = track.PlaneOf(place);
            if (corridor.HeightAt(px, py) is not { } h) Fault("no corridor height under the runner");
            else
            {
                double gap = h - place.Alt;
                GD.Print($"[replaycheck] corridor height under the runner {h:F1} m, the ground's {place.Alt:F1} m ({gap:+0.00;-0.00;0.00} m)");
                if (Math.Abs(gap) > CorridorAgreeM) Fault($"the corridor's height is {gap:F1} m off the ground under the runner");
            }
        }
    }

    private int RibbonOverGround(TrackRibbon ribbon)
    {
        var verts = ribbon.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var xf = ribbon.GlobalTransform;
        int over = 0;
        for (int i = 0; i < verts.Length; i += 18)
            if (_ground.TryGetHeight(xf * verts[i], out _)) over++;
        return over;
    }

    /// <summary>The course as a GPX 1.1 file in <c>test_output/replaycheck</c>, its fixes a second apart, no elevation.</summary>
    private static string WriteGpx(GpxTrack track)
    {
        string dir = ProjectSettings.GlobalizePath("res://test_output/replaycheck");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "course.gpx");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var start = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
          .Append("<gpx version=\"1.1\" creator=\"replaycheck\" xmlns=\"http://www.topografix.com/GPX/1/1\">\n")
          .Append("<trk><name>replaycheck</name><type>running</type><trkseg>\n");
        foreach (var p in track.Points)
        {
            var (lat, lon) = track.Plane.Inverse(p.X, p.Y);
            sb.Append("<trkpt lat=\"").Append(lat.ToString("0.0000000", inv)).Append("\" lon=\"").Append(lon.ToString("0.0000000", inv))
              .Append("\"><time>").Append(start.AddSeconds(p.Seconds).ToString("yyyy-MM-ddTHH:mm:ssZ", inv)).Append("</time></trkpt>\n");
        }
        sb.Append("</trkseg></trk>\n</gpx>\n");
        System.IO.File.WriteAllText(path, sb.ToString());
        return path;
    }

    private void Fault(string what) => _faults.Add(what);

    private void Finish()
    {
        _step = Step.Done;
        foreach (var f in _faults.Take(12)) GD.Print($"[replaycheck]   {f}");
        GD.Print(_faults.Count == 0 ? "[replaycheck] RESULT: ok" : $"[replaycheck] RESULT: FAILED {_faults.Count} faults");
        SetProcess(false);
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }
}
