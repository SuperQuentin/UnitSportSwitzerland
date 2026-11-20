using System;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--spectatorframecheck [all|flight|hover|strafe|strafehigh]</c> (#2053, headless, <c>--frame earth --chunks fixture:straight</c>,
/// <c>--fixed-fps 60</c>): the fly camera's frame blended by distance (<see cref="Player.SpectatorFrame"/>), nothing switching
/// between bodies.
/// <list type="bullet">
/// <item><c>flight</c>: from 1 km over Geneva up to 1,000 km, to 100 km over the Moon, out to 2 million km from the Earth and
/// back to 400 km over it, moved by the check at the fly camera's top speed by the nearest surface. Every frame the velocity
/// of the frame the camera rests in (what it moved from the anchor's centre less what the check moved it, carried across a
/// handover by the two bodies' own motion) and its view's turn
/// rate there are read: a step between frames past <see cref="StepShare"/> of that velocity (plus <see cref="StepFloor"/>), or
/// past <see cref="TurnStep"/> in the turn rate, fails.</item>
/// <item><c>hover</c>: stopped 1 km over Geneva (nothing drifts), 20,000 km over the Earth, 1,000 km over the Moon and
/// 60,000 km beyond it: the body held within a degree of where it was in the view over 10 s.</item>
/// <item><c>strafe</c>: right at full speed 2 km over the Moon, a circle (the height kept within a km over 180 km); 5,000 km
/// over it, a straight line (the distance from its centre that of a line, within 3 %).</item>
/// </list>
/// </summary>
public partial class SpectatorFrameCheck : Node
{
    public static bool Requested => CmdArgs.Has("--spectatorframecheck");

    /// <summary>The camera's velocity in the Sun's frame may change between frames by no more than its last change plus this share of the speed...</summary>
    public const double StepShare = 0.002;

    /// <summary>...plus this, m/s.</summary>
    public const double StepFloor = 2;

    /// <summary>The view's turn rate against the stars may change between frames by no more than this, rad/s.</summary>
    public const double TurnStep = 0.003;

    /// <summary>The frame's velocity is averaged over this many frames before its steps are judged.</summary>
    public const int Smoothing = 6;

    /// <summary>
    /// The measurement's own noise as a share of the camera's speed: 126 m/s at 30,000 km/s and 4.9 km/s at 400,000 km/s,
    /// the same on main before #2053 at the same places. The switches it is there to catch are larger by far: main's
    /// Earth-to-Moon handover stepped 1,998 m/s at 18,700 km/s (1e-4).
    /// </summary>
    public const double Rounding = 1.4e-5;

    private readonly WorldOrigin _origin;
    private int _failed;
    private ChatManager? _chat;

    public SpectatorFrameCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "SpectatorFrameCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[spectatorframe] {what}"));

    private void Expect(bool ok, string what)
    {
        Log((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failed++;
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await Frame();
    }

    private Player.SpectatorCamera? Camera() => GetViewport()?.GetCamera3D() as Player.SpectatorCamera;

    private async Task Run()
    {
        await Wait(3);
        _chat = GetTree().Root.FindChild(ChatManager.NodeName, recursive: true, owned: false) as ChatManager;
        if (_chat == null) { Finish("no chat"); return; }
        string which = CmdArgs.Value("--spectatorframecheck") is { Length: > 0 } v && !v.StartsWith("--", StringComparison.Ordinal) ? v : "all";
        try
        {
            if (which is "all" or "hover") await Hovers();
            if (which is "all" or "strafe") await StrafeLow();
            if (which is "all" or "strafe" or "strafehigh") await StrafeHigh();
            if (which is "all" or "flight") await Flight();
        }
        finally
        {
            Input.ActionRelease(PlayerInput.MoveRight);
        }
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    // ---- the camera in the Sun's frame

    /// <summary>
    /// The camera's place from the anchor's centre, ICRF axes, metres, and its view's axes there (columns), with the anchor
    /// and the moment. From the anchor, not the Sun: a body's local day turns its orbit with the camera's distance from it
    /// (#1992), so its place in the Sun's frame swings as the camera leaves it, a motion nobody sees.
    /// </summary>
    private (DVec3 Place, DMat3 View, BodyId Anchor, SkyMoment Moment)? Seen(Player.SpectatorCamera cam)
    {
        if (_origin.Frame.Axes is not { } axes) return null;
        // the moment the camera moved by (a body's local day turns with the camera's distance: another moment turns its frame)
        var m = cam.FrameMoment ?? SkyClock.Now;
        var r = Frames.Orientation(axes.Body, m);
        var e = axes.ToEcef(cam.GlobalPosition.X, cam.GlobalPosition.Y, cam.GlobalPosition.Z);
        var place = r.Apply(new DVec3(e.X, e.Y, e.Z));
        DVec3 Axis(Vector3 w)
        {
            var x = axes.X;
            var y = axes.Y;
            var z = axes.Z;
            return r.Apply(new DVec3(w.X * x.X + w.Y * y.X + w.Z * z.X, w.X * x.Y + w.Y * y.Y + w.Z * z.Y, w.X * x.Z + w.Y * y.Z + w.Z * z.Z));
        }
        var b = cam.GlobalBasis;
        return (place, new DMat3(Axis(b.X), Axis(b.Y), Axis(b.Z)), axes.Body, m);
    }

    /// <summary>The turn from one view to the next as a rate vector (axis × angle / dt), ICRF.</summary>
    private static DVec3 TurnRate(DMat3 a, DMat3 b, double dt)
    {
        // Q = B Aᵀ: its antisymmetric part
        DVec3 Q(DVec3 v) => b.Apply(a.ApplyInverse(v));
        var c0 = Q(new DVec3(1, 0, 0));
        var c1 = Q(new DVec3(0, 1, 0));
        var c2 = Q(new DVec3(0, 0, 1));
        var axis = new DVec3(c1.Z - c2.Y, c2.X - c0.Z, c0.Y - c1.X);
        double sin2 = axis.Length;
        if (!(sin2 > 1e-15)) return DVec3.Zero;
        double angle = Math.Atan2(sin2 / 2, (c0.X + c1.Y + c2.Z - 1) / 2);
        return axis / sin2 * (angle / dt);
    }

    /// <summary>A world displacement in the anchor's body-fixed axes now, with that body: turned into the ICRF at the camera's moment once it has moved.</summary>
    private (BodyId Body, DVec3 Fixed) BodyFixed(Vector3 w)
    {
        if (_origin.Frame.Axes is not { } axes) return (_origin.Body, DVec3.Zero);
        var x = axes.X;
        var y = axes.Y;
        var z = axes.Z;
        return (axes.Body, new DVec3(w.X * x.X + w.Y * y.X + w.Z * z.X, w.X * x.Y + w.Y * y.Y + w.Z * z.Y, w.X * x.Z + w.Y * y.Z + w.Z * z.Z));
    }

    /// <summary>A body's centre in world space now.</summary>
    private Vector3 Centre(BodyId body) => _origin.ToWorld(GlobalPos.On(body, new EcefPos(0, 0, 0)));

    /// <summary>A point <paramref name="offset"/> (ICRF, metres) from a body's centre, in world space now.</summary>
    private Vector3 FromCentre(BodyId body, DVec3 offset)
    {
        var p = Frames.Orientation(body, SkyClock.Now).ApplyInverse(offset);
        return _origin.ToWorld(GlobalPos.On(body, new EcefPos(p.X, p.Y, p.Z)));
    }

    private async Task<Player.SpectatorCamera?> Go(string go)
    {
        _chat!.Send(go);
        await Wait(1.5);
        if (Camera() is not { } cam) { Expect(false, $"no fly camera after {go}"); return null; }
        cam.Speed = Player.SpectatorSpeed.MaxSpeed;
        return cam;
    }

    private static void Look(Camera3D cam, Vector3 at)
    {
        var to = (at - cam.GlobalPosition).Normalized();
        var hint = Mathf.Abs(to.Dot(Vector3.Up)) > 0.95f ? Vector3.Forward : Vector3.Up;
        cam.GlobalBasis = Basis.LookingAt(to, hint);
    }

    // ---- hover

    private async Task Hovers()
    {
        // 1 km over Geneva: nothing drifts, the ground point under stays
        if (await Go("/goto earth 46.2 6.15 1") is { } cam)
        {
            await Wait(1);
            var start = _origin.ToGlobal(cam.GlobalPosition);
            double drift = 0;
            double t0 = GameClock.Now;
            while (GameClock.Now - t0 < 5) { await Frame(); drift = Math.Max(drift, cam.LastDrift); }
            double moved = start.DistanceTo(_origin.ToGlobal(cam.GlobalPosition));
            Expect(drift == 0 && moved < 0.05, string.Create(CultureInfo.InvariantCulture,
                $"hover 1 km over Geneva: nothing drifts (the frame's drift {drift:F3} m, moved {moved:F3} m in 5 s; up {cam.UpWeight:F2}, spin {cam.SpinWeight:F2})"));
        }
        await Hold("hover 20,000 km over the Earth", "/goto earth 0 0 20000", BodyId.Earth);
        await Hold("hover 1,000 km over the Moon", "/goto moon 0 0 1000", BodyId.Moon);
        // beyond the Moon from the Earth: the Moon's frame a share of the way, the Earth's the rest
        var (lat, lon) = Under(BodyId.Moon, BodyId.Earth);
        await Hold("hover 60,000 km beyond the Moon", string.Create(CultureInfo.InvariantCulture, $"/goto moon {-lat:F3} {lon + 180:F3} 58263"), BodyId.Moon);
    }

    private static (double Lat, double Lon) Under(BodyId body, BodyId target)
    {
        var p = Frames.Convert(target, new DVec3(0, 0, 0), body, SkyClock.Now);
        var g = CelestialBody.Of(body).Shape.ToGeodetic(new EcefPos(p.X, p.Y, p.Z));
        return (g.LatDeg, g.LonDeg);
    }

    /// <summary>Stopped, looking at a body's centre: how far it moves in the view over 10 s.</summary>
    private async Task Hold(string name, string go, BodyId body)
    {
        if (await Go(go) is not { } cam) return;
        await Wait(1);
        Look(cam, Centre(body));
        await Frame();
        double worst = 0;
        double t0 = GameClock.Now;
        while (GameClock.Now - t0 < 10)
        {
            await Frame();
            var to = (Centre(body) - cam.GlobalPosition).Normalized();
            worst = Math.Max(worst, Mathf.RadToDeg((-cam.GlobalBasis.Z).AngleTo(to)));
        }
        Expect(worst < 1, string.Create(CultureInfo.InvariantCulture,
            $"{name}: the body held in view ({worst:F3}° off at worst in 10 s; up {cam.UpWeight:F2}, spin {cam.SpinWeight:F2}, the anchor on {_origin.Body})"));
    }

    // ---- strafe

    private async Task StrafeLow()
    {
        // low over the Moon: a circle
        if (await Go("/goto moon 0 0 2") is { } low)
        {
            await Wait(1);
            double h0 = Height(low), worst = 0, moved = 0;
            Input.ActionPress(PlayerInput.MoveRight);
            double t0 = GameClock.Now;
            while (moved < 180_000 && GameClock.Now - t0 < 120)
            {
                await Frame();
                moved += low.FlownSpeed * GetProcessDeltaTime();
                worst = Math.Max(worst, Math.Abs(Height(low) - h0));
            }
            Input.ActionRelease(PlayerInput.MoveRight);
            Expect(worst < 1_000 && moved >= 180_000, string.Create(CultureInfo.InvariantCulture,
                $"strafe 2 km over the Moon: a circle ({moved / 1000:F0} km flown, the height at most {worst:F0} m off; a line would rise {moved * moved / 2 / CelestialBody.Moon.Shape.A:F0} m)"));
        }
    }

    private async Task StrafeHigh()
    {
        // high over the Moon: a line
        if (await Go("/goto moon 0 0 5000") is { } high)
        {
            await Wait(1);
            // the Moon in view (a film of it shows the line against the circle)
            Look(high, Centre(BodyId.Moon));
            await Wait(0.5);
            var moon = CelestialBody.Moon;
            double d0 = (Centre(BodyId.Moon) - high.GlobalPosition).Length(), moved = 0;
            Input.ActionPress(PlayerInput.MoveRight);
            double t0 = GameClock.Now;
            while (GameClock.Now - t0 < 4)
            {
                await Frame();
                moved += high.FlownSpeed * GetProcessDeltaTime();
            }
            Input.ActionRelease(PlayerInput.MoveRight);
            await Frame();
            double d = (Centre(BodyId.Moon) - high.GlobalPosition).Length();
            double line = Math.Sqrt(d0 * d0 + moved * moved);
            Expect(Math.Abs(d - line) < 0.03 * line, string.Create(CultureInfo.InvariantCulture,
                $"strafe 5,000 km over the Moon: a line ({moved / 1000:F0} km flown from {d0 / 1000:F0} km off its centre: now {d / 1000:F0} km, a line's {line / 1000:F0} km, a circle's {d0 / 1000:F0} km; up {high.UpWeight:F2})"));
        }
    }

    private double Height(Camera3D cam) => _origin.AltitudeOf(cam.GlobalPosition);

    // ---- flight

    private async Task Flight()
    {
        // from 1 km over the point under the Moon: straight up at it, through the co-rotation's and up's fades, into its sphere
        var (lat, lon) = Under(BodyId.Earth, BodyId.Moon);
        if (await Go(string.Create(CultureInfo.InvariantCulture, $"/goto earth {lat:F3} {lon:F3} 1")) is not { } cam) return;
        await Wait(1);
        var steps = new FlightSteps();
        await Leg(cam, steps, "moon", () => Centre(BodyId.Moon), () => CelestialBody.Moon.Shape.A + 100_000, 1_000);
        // out of the Moon's sphere and the Earth's, sideways to the line between them, 2 million km
        var moonDir = Frames.Offset(BodyId.Earth, BodyId.Moon, SkyClock.Now);
        var side = moonDir.Cross(new DVec3(0, 0, 1));
        side /= side.Length;
        await Leg(cam, steps, "out", () => FromCentre(BodyId.Moon, side * 2e9), () => 0, 10_000_000);
        await Leg(cam, steps, "home", () => Centre(BodyId.Earth), () => CelestialBody.Earth.Shape.A + 400_000, 1_000);
        Log(string.Create(CultureInfo.InvariantCulture,
            $"flight: {steps.Frames} frames; the worst step in velocity {steps.WorstStep:F1} m/s ({steps.WorstStepAt}), in the turn rate {steps.WorstTurn:F5} rad/s ({steps.WorstTurnAt}); {steps.Handovers} handover(s)"));
        Expect(steps.Handovers >= 4, $"flight: through the spheres ({steps.Handovers} handovers)");
        Expect(steps.StepFailures == 0, string.Create(CultureInfo.InvariantCulture, $"flight: no step in the camera's velocity ({steps.StepFailures} frames past the bar, worst {steps.WorstStep:F1} m/s)"));
        Expect(steps.TurnFailures == 0, string.Create(CultureInfo.InvariantCulture, $"flight: no step in the view's turn ({steps.TurnFailures} frames past {TurnStep} rad/s, worst {steps.WorstTurn:F5})"));
    }

    private sealed class FlightSteps
    {
        public int Frames, StepFailures, TurnFailures, Handovers;
        public double WorstStep, WorstTurn;
        public string WorstStepAt = "", WorstTurnAt = "";
    }

    /// <summary>
    /// The camera moved toward a target at the top speed by the nearest surface until within <paramref name="stopAt"/> of
    /// it (or <paramref name="stopFloor"/> metres), its velocity and view read every frame: the first frames of a leg (its
    /// start from where the last stopped) are not judged.
    /// </summary>
    private async Task Leg(Player.SpectatorCamera cam, FlightSteps steps, string name, Func<Vector3> target, Func<double> stopAt, double stopFloor)
    {
        DVec3? lastPlace = null, lastV = null, lastDv = null, lastRate = null, commandedLast = null;
        var recent = new System.Collections.Generic.Queue<DVec3>();
        BodyId? lastAnchor = null;
        SkyMoment? lastMoment = null;
        DMat3? lastView = null;
        var body = _origin.Body;
        int frames = 0;
        double t0 = GameClock.Now;
        while (GameClock.Now - t0 < 300)
        {
            float dt = (float)GetProcessDeltaTime();
            if (_origin.Body != body) { steps.Handovers++; body = _origin.Body; }
            var to = target() - cam.GlobalPosition;
            double left = to.Length() - stopAt();
            if (left < stopFloor) break;
            double surface = Player.SurfaceReach.Of(_origin.Frame, cam.GlobalPosition, _origin.AltitudeOf(cam.GlobalPosition));
            double step = Math.Min(Player.SpectatorSpeed.SurfaceCap(surface) * dt, left - stopFloor * 0.5);
            var command = to.Normalized() * (float)step;
            // the move as applied: far from the world's origin a float rounds the place by tens of metres
            var from = cam.GlobalPosition;
            cam.GlobalPosition += command;
            var commanded = BodyFixed(cam.GlobalPosition - from);
            await Frame();
            // turned with the place by the frame's own turn this frame (the body's spin undone), then into the ICRF at its moment
            var (spinAxis, spinAngle) = cam.LastUnspin;
            var fixedMove = commanded.Body == _origin.Body && spinAngle != 0 ? Player.SpectatorFrame.Turn(commanded.Fixed, spinAxis, spinAngle) : commanded.Fixed;
            commandedLast = Frames.Orientation(commanded.Body, cam.FrameMoment ?? SkyClock.Now).Apply(fixedMove);
            frames++;
            if (Seen(cam) is not { } seen) continue;
            if (lastPlace is { } before && lastAnchor is { } was && was != seen.Anchor && lastMoment is { } then)
            {
                // a handover: the last place and velocities carried from the old anchor's centre to the new one's (near a
                // sphere's edge, where the local day has faded out)
                var shift = Frames.Offset(seen.Anchor, was, then);
                var relative = (Frames.Offset(seen.Anchor, was, seen.Moment) - shift) / dt;
                lastPlace = before + shift;
                var carried = new System.Collections.Generic.Queue<DVec3>();
                foreach (var r in recent) carried.Enqueue(r + relative);
                recent = carried;
                if (lastV is { } lvOld) lastV = lvOld + relative;
            }
            if (lastPlace is { } lp && lastView is { } lv)
            {
                // the frame's own motion: what the camera moved less what the check moved it (its speed follows the nearest
                // surface, with a kink where the nearest body changes: the check's, not the frame's)
                var raw = (seen.Place - lp - (commandedLast ?? DVec3.Zero)) / dt;
                // averaged over a few frames: the Earth's angle steps a little unevenly frame to frame (the game's clock,
                // ±70 m/s of its 28 km/s co-rotation on the ground), a switch of frames shows through any average
                recent.Enqueue(raw);
                if (recent.Count > Smoothing) recent.Dequeue();
                var v = DVec3.Zero;
                foreach (var r in recent) v += r;
                v /= recent.Count;
                var rate = TurnRate(lv, seen.View, dt);
                if (lastV is { } lvv)
                {
                    var dv = v - lvv;
                    if (lastDv is { } ldv && frames > Smoothing + 4)
                    {
                        // plus the measurement's own noise, a share of the camera's speed
                        double flown = (commandedLast ?? DVec3.Zero).Length / dt;
                        double jump = (dv - ldv).Length, bar = StepShare * v.Length + StepFloor + Rounding * flown;
                        if (jump > bar) steps.StepFailures++;
                        if (jump > bar && steps.StepFailures <= 5) Log(string.Create(CultureInfo.InvariantCulture, $"step {jump:F1} m/s over {bar:F1} ({name}, {GameClock.Now - t0:F2} s in, the frame {v.Length / 1000:F2} km/s, flown {flown / 1000:F0} km/s, the anchor on {_origin.Body}, up {cam.UpWeight:F2}, spin {cam.SpinWeight:F2})"));
                        if (jump / bar > steps.WorstStep / (StepShare * Math.Max(v.Length, 1) + StepFloor))
                        {
                            steps.WorstStep = jump;
                            steps.WorstStepAt = string.Create(CultureInfo.InvariantCulture, $"{name}, {GameClock.Now - t0:F1} s in, {v.Length / 1000:F1} km/s, the anchor on {_origin.Body}, up {cam.UpWeight:F2}, spin {cam.SpinWeight:F2}");
                        }
                    }
                    lastDv = dv;
                }
                if (lastRate is { } lr && frames > 4)
                {
                    double turn = (rate - lr).Length;
                    if (turn > TurnStep) steps.TurnFailures++;
                    if (turn > steps.WorstTurn)
                    {
                        steps.WorstTurn = turn;
                        steps.WorstTurnAt = string.Create(CultureInfo.InvariantCulture, $"{name}, {GameClock.Now - t0:F1} s in, the anchor on {_origin.Body}, up {cam.UpWeight:F2}, spin {cam.SpinWeight:F2}");
                    }
                }
                lastRate = rate;
                lastV = v;
            }
            lastPlace = seen.Place;
            lastView = seen.View;
            lastAnchor = seen.Anchor;
            lastMoment = seen.Moment;
            commandedLast = null;
            steps.Frames++;
        }
        Log(string.Create(CultureInfo.InvariantCulture, $"leg {name}: {frames} frames, {GameClock.Now - t0:F1} s, ends on {_origin.Body} {_origin.AltitudeOf(cam.GlobalPosition) / 1000:F0} km up"));
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[spectatorframe] RESULT: ok");
        else GD.PrintErr($"[spectatorframe] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
