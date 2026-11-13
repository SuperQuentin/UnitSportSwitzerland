using System.Threading.Tasks;
using Godot;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// <c>--frame earth --spacelandcheck --chunks fixture:straight --traffic 0</c> (#1811): back into the body after a long
/// spectator flight. The body stands on the ground, the spectator camera takes over (as Shift+T does), climbs to
/// <see cref="TopM"/>, flies <see cref="AwayM"/> east through Space (hundreds of 20 km turning re-anchors), dips through
/// the 3 km / Space regime switch a few times, runs low (hundreds of 3 km turning re-anchors), comes down to 1 km and
/// goes back into the body (as T does). After that the camera drawn must be the body's, finite, orthonormal, with a
/// near plane under a metre and a far plane past it; the body finite and upright; and it stands on ground (the earth
/// world streams round the camera). A user flight ended on a dark screen, the light culler failing every frame
/// (<c>create_frustum_points</c>), and stayed so even after a teleport home. Prints <c>[spacelandcheck] RESULT</c>.
/// </summary>
public partial class SpaceLandingCheck : Node
{
    public static bool Requested => CmdArgs.Has("--spacelandcheck");

    private const double TopM = 1_500_000, AwayM = 8_000_000, LowM = 30_000, LandM = 1_000;
    // wall clock: the leaves stream on threads
    private const double TimeoutS = 300;

    private readonly System.Func<FootPlayer?> _local;
    private readonly System.Func<Camera3D?> _spectator;
    private readonly System.Action _toggle;
    private readonly IWorldGround _ground;
    private int _failed;
    private double _startS;

    public SpaceLandingCheck(System.Func<FootPlayer?> local, System.Func<Camera3D?> spectator, System.Action toggle, IWorldGround ground)
    {
        Name = "SpaceLandingCheck";
        _local = local;
        _spectator = spectator;
        _toggle = toggle;
        _ground = ground;
    }

    public override void _Ready()
    {
        _startS = RealClock.Now;
        _ = Run();
    }

    private static void Log(string what) => GD.Print($"[spacelandcheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (!ok) _failed++;
        Log($"{(ok ? "ok  " : "FAIL")} {what}");
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Wait(double seconds)
    {
        double until = RealClock.Now + seconds;
        while (RealClock.Now < until) await Frame();
    }

    private static WorldOrigin Origin => OriginShifter.Instance!.Origin;
    private static double AltitudeOf(Vector3 p) => Origin.AltitudeOf(p);

    /// <summary>One step of the flight: <paramref name="east"/>, <paramref name="north"/>, <paramref name="up"/> metres at the camera.</summary>
    private static void Step(Camera3D cam, double east, double north, double up)
    {
        var at = Origin.ToGlobal(cam.GlobalPosition).Moved(east, north, up);
        cam.GlobalPosition = Origin.ToWorld(at);
    }

    private async Task Climb(Camera3D cam, double toM, double perFrameM)
    {
        while (RealClock.Now - _startS < TimeoutS)
        {
            double alt = AltitudeOf(cam.GlobalPosition);
            if (System.Math.Abs(alt - toM) < 1) return;
            Step(cam, 0, 0, System.Math.Clamp(toM - alt, -perFrameM, perFrameM));
            await Frame();
        }
    }

    private async Task Cruise(Camera3D cam, double distanceM, double perFrameM)
    {
        for (double flown = 0; flown < distanceM && RealClock.Now - _startS < TimeoutS; flown += perFrameM)
        {
            Step(cam, perFrameM, perFrameM * 0.15, 0);
            await Frame();
        }
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800 && RealClock.Now - _startS < TimeoutS; i++)
        {
            me = _local();
            if (me != null && me.Camera.Current && me.IsOnFloor()) break;
            // a probe run starts on the fly camera: into the body
            if (i % 50 == 25 && (me == null || !me.Camera.Current)) _toggle();
            await Wait(0.1);
        }
        if (me == null || !me.IsOnFloor() || _spectator() is not { } cam) { Finish("no body on the ground or no spectator camera"); return; }
        Log($"on foot at {me.GlobalPosition}, near {me.Camera.Near:F2} far {me.Camera.Far:F0}");

        _toggle();
        await Wait(0.3);
        Expect(cam.Current, "into the spectator camera");
        int shifts0 = OriginShifter.Instance!.ShiftCount;

        await Climb(cam, TopM, 6_000);
        Log($"up at {AltitudeOf(cam.GlobalPosition) / 1000:F0} km, near {cam.Near:F1} far {cam.Far:F0}, {OriginShifter.Instance.ShiftCount - shifts0} shifts");
        await Cruise(cam, AwayM, 19_000);
        Log($"away: {Origin.ToGlobal(cam.GlobalPosition).Geodetic}, {OriginShifter.Instance.ShiftCount - shifts0} shifts");
        await Climb(cam, LowM, 6_000);
        // through the 3 km / Space regime switch and back, a few times
        for (int i = 0; i < 4; i++)
        {
            await Climb(cam, 75_000, 2_000);
            await Climb(cam, 40_000, 2_000);
        }
        await Climb(cam, LowM, 2_000);
        await Cruise(cam, 900_000, 2_900);
        // T pressed in Space, as the user did: the body waits for the ground under the camera, known only once the
        // descent is back in the 3 km regime, and steps in on that frame
        await Climb(cam, 70_000, 2_000);
        Log($"T at {AltitudeOf(cam.GlobalPosition) / 1000:F1} km: {Origin.ToGlobal(cam.GlobalPosition).Geodetic}, {OriginShifter.Instance.ShiftCount - shifts0} shifts");
        _toggle();
        while (!me.Camera.Current && AltitudeOf(cam.GlobalPosition) > LandM + 1 && RealClock.Now - _startS < TimeoutS)
        {
            Step(cam, 0, 0, -System.Math.Min(1_500, AltitudeOf(cam.GlobalPosition) - LandM));
            await Frame();
        }
        Log($"into the body with the camera at {AltitudeOf(cam.GlobalPosition) / 1000:F1} km, {OriginShifter.Instance.ShiftCount - shifts0} shifts");
        // a few frames for the body to place its camera, then watch it a while
        for (int i = 0; i < 5; i++) await Frame();
        string? firstBad = null;
        bool grounded = false;
        double until = RealClock.Now + 20;
        while (RealClock.Now < until)
        {
            var current = GetViewport().GetCamera3D();
            string? bad = current != me.Camera ? $"the camera drawn is {current?.Name ?? "none"}, not the body's" : Bad(me.Camera, me);
            if (bad != null) { firstBad ??= bad; }
            if (me.IsOnFloor() || _ground.TryGetSurface(me.GlobalPosition, out _)) grounded = true;
            if (grounded && RealClock.Now > until - 15) break;
            await Frame();
        }
        Log($"after: body {me.GlobalTransform}, camera {me.Camera.GlobalTransform}, near {me.Camera.Near} far {me.Camera.Far} fov {me.Camera.Fov}");
        Expect(firstBad == null, $"the body's camera draws a sane frustum{(firstBad == null ? "" : ": " + firstBad)}");
        Expect(grounded, $"ground under the body ({me.GlobalPosition}, on floor {me.IsOnFloor()})");
        Finish(null);
    }

    /// <summary>What is wrong with the camera, or the body that holds it, for the renderer; null when nothing.</summary>
    private static string? Bad(Camera3D cam, FootPlayer body)
    {
        var t = cam.GlobalTransform;
        if (!t.IsFinite()) return $"camera transform not finite: {t}";
        if (!Orthonormal(t.Basis)) return $"camera basis not orthonormal: {t.Basis}";
        if (!(cam.Near > 0 && cam.Near <= 1f)) return $"near plane {cam.Near}";
        if (!(cam.Far > cam.Near * 10 && float.IsFinite(cam.Far))) return $"far plane {cam.Far} (near {cam.Near})";
        if (!float.IsFinite(cam.Fov) || cam.Fov <= 1 || cam.Fov >= 179) return $"fov {cam.Fov}";
        var b = body.GlobalTransform;
        if (!b.IsFinite()) return $"body transform not finite: {b}";
        if (!Orthonormal(b.Basis)) return $"body basis not orthonormal: {b.Basis}";
        if (!body.Velocity.IsFinite()) return $"body velocity {body.Velocity}";
        return null;
    }

    private static bool Orthonormal(Basis m)
    {
        const float tol = 1e-3f;
        return Mathf.Abs(m.Column0.Length() - 1) < tol && Mathf.Abs(m.Column1.Length() - 1) < tol && Mathf.Abs(m.Column2.Length() - 1) < tol
            && Mathf.Abs(m.Column0.Dot(m.Column1)) < tol && Mathf.Abs(m.Column1.Dot(m.Column2)) < tol && Mathf.Abs(m.Column0.Dot(m.Column2)) < tol;
    }

    private void Finish(string? error)
    {
        if (error != null) { _failed++; Log($"FAIL {error}"); }
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failed})");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
