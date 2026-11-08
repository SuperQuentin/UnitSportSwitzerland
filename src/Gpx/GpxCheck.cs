using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx;

/// <summary>
/// <c>godot --path . -- --gpxcheck [--at E,N]</c> (#1429): a GPX course drawn and run where it
/// belongs, before and after the origin moves. Builds a 1.5 km course east from the spawn in code
/// (5 m/s, no file), draws its <see cref="TrackRibbon"/> and runs a <see cref="Runner"/> along it.
/// Checks that the ribbon's tread lies on the ground it was draped onto (its world vertices against
/// <see cref="ChunkManager.TryGetHeight"/>), that <see cref="Runner.CourseAt"/> is the course's
/// place (its plane point, #1514) and that the runner's feet are on it; then shifts the origin twice mid-run and checks
/// the same in each frame, plus that the runner never jumps in LV95 between two frames.
/// RESULT ok or FAILED with the first faults. Runs on the generated world: <c>--systems terrain,generated</c>.
/// </summary>
public partial class GpxCheck : Node
{
    private const double TimeoutS = 240, CourseM = 1500, PaceMs = 5;
    private const float TreadLift = 0.28f;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly List<string> _faults = new();
    private GpxTrack? _track;
    private TrackRibbon? _ribbon;
    private Runner? _runner;
    private double _t, _raceTime;
    private int _shifts, _checks;
    private GlobalPos? _lastFoot;
    private double _maxStep;

    public GpxCheck(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    public static bool Requested() => CmdArgs.Has("--gpxcheck");

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t > TimeoutS)
        {
            Fault($"timeout after {TimeoutS} s ({_checks} checks done)");
            Finish();
            return;
        }
        if (_track == null)
        {
            var (e, n) = SpawnPoint.ParseTarget();
            if (!_chunks.Ground.TryGetHeight(_origin.ToWorld(e, n, 0), out _)) return;
            _track = Course(e, n);
            _ribbon = TrackRibbon.Create(_track, _chunks, _origin);
            _runner = Runner.Create(_track, _chunks, _origin, 0);
            AddChild(_ribbon);
            AddChild(_runner);
            GD.Print($"[gpxcheck] a {CourseM / 1000:F1} km course from LV95 {e:F0}/{n:F0}");
            return;
        }

        // the runner moves at the course's own pace; the ground under all of it is streamed by then
        _raceTime = Math.Min(_raceTime + delta * 4, _track.Duration);
        _runner!.UpdateTo(_raceTime, 4, delta);
        var foot = _origin.ToGlobal(_runner.FootWorld);
        if (_lastFoot is { } last) _maxStep = Math.Max(_maxStep, foot.HorizontalDistanceTo(last));
        _lastFoot = foot;

        if (_ribbon!.Mesh == null) return;
        // the frame after a shift, once the runner has been placed in the new world space (a game
        // shift comes first in its frame, OriginShifter's priority, so nothing reads it in between)
        if (_shiftedBy is { } by)
        {
            Verify($"the frame after shift {_shifts} by {by}");
            _shiftedBy = null;
        }
        // a check every few seconds of the run, a shift after the first and the second
        if (_raceTime < 30 * (_checks + 1)) return;
        Verify($"at {_raceTime:F0} s" + (_shifts > 0 ? $" after {_shifts} shifts" : ""));
        _checks++;
        if (_checks == 1 || _checks == 2)
        {
            var before = _origin.Frame;
            if (OriginShifter.Instance?.ShiftTo(_origin.E + 2000, _origin.N + 1000 * _checks) != true)
                Fault("the origin did not shift");
            else
            {
                _shifts++;
                _shiftedBy = _origin.Since(before);
            }
        }
        if (_checks >= 4 || _raceTime >= _track.Duration) Finish();
    }

    private OriginShift? _shiftedBy;

    private void Verify(string when)
    {
        // the course: CourseAt is the sampled plane point's place, on the ground
        var (x, y, _, _, _) = _track!.Sample(_raceTime);
        var course = _runner!.CourseAt(_raceTime);
        var at = _origin.ToGlobal(course);
        var want = _track.At(x, y, at.Alt);
        if (at.HorizontalDistanceTo(want) > 0.05)
            Fault($"{when}: CourseAt is {at.HorizontalDistanceTo(want):F2} m from the track's point {x:F2}/{y:F2} on its plane");
        // the avatar eases toward the course (a couple of metres behind it at this pace): a misplacement
        // across a shift would be kilometres, a wrong frame for the height tens of metres
        var foot = _runner.FootWorld;
        if (new Vector2(foot.X - course.X, foot.Z - course.Z).Length() > 10f || Mathf.Abs(foot.Y - course.Y) > 5f)
            Fault($"{when}: the runner's feet {foot} are off the course {course}");

        // the ribbon: every tread vertex TreadLift over the ground under it (kerbs go down from the
        // tread); its edges stand 0.8 m to the side, so on a slope they sit up to ~0.7 m off it
        var arrays = _ribbon!.Mesh.SurfaceGetArrays(0);
        var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var xf = _ribbon.GlobalTransform;
        int tread = 0, off = 0;
        float worst = 0;
        for (int i = 0; i + 5 < verts.Length; i += 18)   // each segment: 6 tread vertices, then 12 kerb
            for (int k = 0; k < 6; k++)
            {
                var w = xf * verts[i + k];
                if (!_chunks.Ground.TryGetHeight(w, out float ground)) continue;
                float gap = w.Y - ground - TreadLift;
                tread++;
                worst = Mathf.Max(worst, Mathf.Abs(gap));
                if (Mathf.Abs(gap) > 1.2f) off++;
            }
        GD.Print($"[gpxcheck] {when}: {tread} tread vertices, {off} more than 1.2 m off the ground (worst {worst:F2} m), runner step max {_maxStep:F2} m");
        if (tread == 0) Fault($"{when}: no ribbon over loaded ground");
        else if (off > tread / 50) Fault($"{when}: {off} of {tread} ribbon vertices off the ground (worst {worst:F2} m)");
    }

    /// <summary>
    /// A straight course true east from LV95 (e, n) at a steady pace, one fix a second, elevation left to the drape:
    /// on a plane centred on its start (#1514), where a plane metre is a true one to 1e-8 over 1.5 km.
    /// </summary>
    private static GpxTrack Course(double e, double n)
    {
        int count = (int)(CourseM / PaceMs) + 1;
        var plane = MapProjection.At(GlobalPos.FromLv95(e, n, 0).Ecef);
        var points = new TrackPoint[count];
        for (int i = 0; i < count; i++) points[i] = new TrackPoint(i * PaceMs, 0, 0, i, i * PaceMs);
        return new GpxTrack { Name = "gpxcheck", Plane = plane, Points = points, HasTiming = true };
    }

    private void Fault(string what) => _faults.Add(what);

    private void Finish()
    {
        // a runner moves 20 m/s of course time here; a jump across a shift would be kilometres
        if (_maxStep > 5) Fault($"the runner jumped {_maxStep:F1} m in LV95 between two frames");
        if (_shifts < 2) Fault($"only {_shifts} of 2 origin shifts happened");
        foreach (var f in _faults.Take(12)) GD.Print($"[gpxcheck]   {f}");
        GD.Print(_faults.Count == 0 ? "[gpxcheck] RESULT: ok" : $"[gpxcheck] RESULT: FAILED {_faults.Count} faults");
        SetProcess(false);
        GetTree().Quit(_faults.Count == 0 ? 0 : 1);
    }
}
