using Godot;
using UnitSport.Core;

namespace UnitSport.Movie;

/// <summary>
/// <c>--movieshotcheck --world flat</c> (#776): shots that follow an actor, checked against the puppet the
/// stage really draws. An actor walks north, then turns west and walks on; a Lead shot must stay 6 m in
/// front of where the puppet visibly faces, a Chase 7 m behind, and a Mount must agree with the Lead's
/// heading frame, at times sought both ways. Proves the rigs' yaw and LV95 conventions are the puppets'.
/// </summary>
public partial class MovieShotProbe : Node
{
    public static bool Requested() => CmdArgs.Has("--movieshotcheck");

    private readonly WorldOrigin _origin;
    private MovieProject? _project;
    private MovieStage? _stage;
    private readonly double[] _times = { 2, 8, 4.5, 9.5, 1, 7 };
    private int _seek = -1, _settle, _failures;

    public MovieShotProbe(WorldOrigin origin) => _origin = origin;

    public override void _Ready()
    {
        var (e0, n0) = SpawnPoint.ParseTarget();
        // north at 2 m/s for 5 s, then a quarter turn left (west) and west at 2 m/s for 5 s
        var ring = new ReplayRing(301, ActorIo.Names.Length);
        for (int k = 0; k <= 300; k++)
        {
            double t = k / Channels.Rate;
            var s = new ActorState(ActorIo.Names.Length) { Alt = 500 };
            if (t <= 5) { s.E = e0; s.N = n0 + 2 * t; s.F[Channels.Yaw] = 0; }
            else { s.E = e0 - 2 * (t - 5); s.N = n0 + 10; s.F[Channels.Yaw] = MathF.PI / 2; }
            s.F[Channels.PoseLen] = 8;
            s.F[Channels.Pose + 3] = 1;
            ring.Append(t, s);
        }
        _project = new MovieProject(ActorIo.Names);
        int lane = _project.LaneFor("Walker", "Walker", 1);
        _project.AddTrack(lane, ring.Slice(double.NegativeInfinity, out _)!, 0);
        var lead = _project.Camera;
        lead.Rig = ShotRig.Lead; lead.Target = lane; lead.Smoothing = 0;
        var chase = _project.AddCamera();
        chase.Rig = ShotRig.Chase; chase.Target = lane; chase.Smoothing = 0;
        var mount = _project.AddCamera();
        mount.Rig = ShotRig.Mount; mount.Target = lane;
        _stage = new MovieStage(_project, _origin);
        AddChild(_stage);
        _seek = 0;
        _stage.Seek(_times[0]);
    }

    public override void _Process(double delta)
    {
        if (_seek < 0 || _stage == null || _project == null) return;
        if (++_settle < 3) return;
        _settle = 0;
        Check(_times[_seek]);
        if (++_seek >= _times.Length) { Finish(); return; }
        _stage.Seek(_times[_seek]);
    }

    private void Check(double t)
    {
        var puppet = _stage!.Puppet(0);
        if (puppet == null) { Fail($"t {t}: no puppet"); return; }
        var feet = puppet.GlobalPosition;
        // where the puppet faces, as drawn
        var forward = -puppet.GlobalBasis.Z;
        forward.Y = 0;
        forward = forward.Normalized();
        Vector3 At(int cam)
        {
            _project!.ShotPose(cam, t, _stage.Mount, out var pose, out _);
            return _origin.ToWorld(new GlobalPos(pose.E, pose.N, pose.Alt));
        }
        var lead = At(0) - feet;
        var chase = At(1) - feet;
        var mount = At(2) - feet;
        float ahead = new Vector3(lead.X, 0, lead.Z).Dot(forward), behind = -new Vector3(chase.X, 0, chase.Z).Dot(forward);
        float side = Math.Abs(new Vector3(lead.X, 0, lead.Z).Cross(forward).Y);
        // a mount just over the actor, 0.4 m back of where it faces
        float mountBack = -new Vector3(mount.X, 0, mount.Z).Dot(forward);
        bool ok = Math.Abs(ahead - 6) < 0.05f && Math.Abs(behind - 7) < 0.05f && side < 0.05f && Math.Abs(mountBack - 0.4f) < 0.05f;
        GD.Print($"[movieshotcheck] t {t,4:F1}: lead {ahead:F2} m ahead ({side:F2} aside), chase {behind:F2} m behind, mount {mountBack:F2} m back "
            + $"{(ok ? "ok" : "WRONG")}");
        if (!ok) Fail($"t {t}: the rigs do not hang where the puppet faces");
    }

    private void Fail(string why)
    {
        _failures++;
        GD.Print($"[movieshotcheck] FAIL: {why}");
    }

    private void Finish()
    {
        _seek = -1;
        GD.Print(_failures == 0 ? "[movieshotcheck] RESULT: ok" : $"[movieshotcheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
