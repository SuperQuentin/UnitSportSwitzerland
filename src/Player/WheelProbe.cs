using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Steering wheel checks (issue #68).
///
/// <para>
/// <c>--wheelcheck</c>, headless, pure numbers: every car in both profiles steers from a wheel angle
/// straight through its steering ratio (none of the rack easing, speed-scaled lock or counter-steer
/// assist, even in a slide), reaches its lock stop at half its lock-to-lock, and still eases from
/// the keys; the range stretch, the pedal read-out, the pad bindings taken off an ignored joypad and
/// put back; and that the SDL3 native library loads.
/// </para>
///
/// <para>
/// <c>--wheelwatch A|B</c> with <c>--connect</c>, on loopback: A (with <c>--fakewheel</c>) gets into
/// a car and checks its road wheels follow the swept wheel; B must see A's car steer both ways
/// through the replicated pose. RESULT line on each.
/// </para>
/// </summary>
public partial class WheelProbe : Node
{
    public static bool CheckRequested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--wheelcheck") >= 0;

    /// <summary>
    /// <c>--ffbcheck</c>, in a window with a real wheel and hands off it: pushes it right, then left,
    /// at 30% for half a second each, and reads back which way it turned — whether this device needs
    /// <see cref="WheelSettings.FfbInvert"/>. RESULT line.
    /// </summary>
    public static bool ForceCheckRequested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--ffbcheck") >= 0;

    public static string? WatchRole
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--wheelwatch");
            return i < 0 ? null : i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "A";
        }
    }

    // ---------------------------------------------------------------------------------------
    // --wheelcheck
    // ---------------------------------------------------------------------------------------

    /// <summary>Runs the numbers; call after <see cref="PlayerInput.Install"/> so the actions exist.</summary>
    public static int Check(Node root)
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) { failures++; GD.Print($"[wheel] FAIL {what}"); }
        }

        const float Dt = 1f / 60f;
        var profileWas = GameSettings.Current.RideProfile;
        foreach (var profile in new[] { RideProfile.Sim, RideProfile.Game })
        {
            GameSettings.Current.RideProfile = profile;
            foreach (var spec in CarCatalog.All)
            {
                // sliding at 90 km/h, 17° of drift angle: where the Game assist and the lock scaling act
                var car = new Car(spec);
                float angle = Mathf.DegToRad(30f);
                var m = new RideMotion { Speed = 25f, Slip = 0.3f, YawRate = 0.4f };
                car.Step(new RideInput(0.5f, 0f, 0f, false, WheelAngle: angle), new RideGround(true, 0f), Dt, ref m);
                float want = Mathf.Clamp(-angle / car.Spec.SteerRatio, -spec.MaxSteer, spec.MaxSteer);
                Expect(Mathf.Abs(car.SteerAngle - want) < 1e-5f,
                    $"{profile} {spec.Label}: 30° of wheel gave {Mathf.RadToDeg(car.SteerAngle):F2}° of road wheel, want {Mathf.RadToDeg(want):F2}°");
                // the cockpit turns its wheel by SteerAngle·SteerRatio: it must show the real wheel's angle
                float shown = car.SteerAngle * car.Spec.SteerRatio;
                Expect(Mathf.Abs(shown + angle) < 1e-4f, $"{profile} {spec.Label}: cockpit wheel shows {shown:F3} rad for {angle:F3}");

                var lockM = new RideMotion { Speed = 5f };
                car.Step(new RideInput(0f, 0f, 0f, false, WheelAngle: -car.WheelLock * 0.5f), new RideGround(true, 0f), Dt, ref lockM);
                Expect(Mathf.Abs(car.SteerAngle - spec.MaxSteer) < 1e-5f, $"{profile} {spec.Label}: full left lock gave {car.SteerAngle:F3}");

                var keys = new Car(spec);
                var km = new RideMotion { Speed = 5f };
                keys.Step(new RideInput(0f, 0f, 1f, false), new RideGround(true, 0f), Dt, ref km);
                Expect(keys.SteerAngle < 0f && Mathf.Abs(keys.SteerAngle) < spec.MaxSteer * 0.2f,
                    $"{profile} {spec.Label}: a key press should still wind the rack on, got {keys.SteerAngle:F3}");
            }
        }
        GameSettings.Current.RideProfile = profileWas;

        // trucks and buses steer through the cab's ratio, to their own stop
        foreach (var heavy in HeavyCatalog.All)
        {
            if (Rideable.Create(heavy.Kind) is not Truck truck) { Expect(false, $"{heavy.Label}: no truck"); continue; }
            float angle = Mathf.DegToRad(200f);
            var m = new RideMotion { Speed = 15f };
            truck.Step(new RideInput(0f, 0f, 0f, false, WheelAngle: angle), new RideGround(true, 0f), Dt, ref m);
            float want = Mathf.Clamp(-angle / Avatar.HeavyCockpit.SteerRatio, -heavy.MaxSteer, heavy.MaxSteer);
            Expect(Mathf.Abs(truck.SteerAngle - want) < 1e-5f,
                $"{heavy.Label}: 200° of wheel gave {Mathf.RadToDeg(truck.SteerAngle):F2}°, want {Mathf.RadToDeg(want):F2}°");
            truck.Step(new RideInput(0f, 0f, 0f, false, WheelAngle: truck.WheelLock * 0.5f), new RideGround(true, 0f), Dt, ref m);
            Expect(Mathf.Abs(truck.SteerAngle + heavy.MaxSteer) < 1e-5f, $"{heavy.Label}: full right lock gave {truck.SteerAngle:F3}");
        }
        GD.Print($"[wheel] {HeavyCatalog.All[0].Label}: {Mathf.RadToDeg(((Truck)Rideable.Create(HeavyCatalog.All[0].Kind)!).WheelLock):F0}° lock to lock");
        var ae86 = new Car(CarCatalog.All[0]);
        GD.Print($"[wheel] {CarCatalog.All[0].Label}: {CarCatalog.All[0].LockTurns * 360f:F0}° lock to lock, ratio {ae86.Spec.SteerRatio:F1}:1");

        // a 900° wheel in a 1260° car is stretched to reach the lock; a 1080° one in a 900° car is 1:1
        float r900 = Mathf.DegToRad(900f), r1260 = Mathf.DegToRad(1260f), r1080 = Mathf.DegToRad(1080f);
        Expect(Mathf.IsEqualApprox(SteeringWheel.GameAngle(r900 / 2, r900, r1260), r1260 / 2), "range stretch");
        Expect(Mathf.IsEqualApprox(SteeringWheel.GameAngle(0.5f, r1080, r900), 0.5f), "1:1 inside range");

        var pedal = new WheelAxis { Axis = 1, From = 1f, To = -1f };
        Expect(pedal.Read(1f) == 0f && pedal.Read(-1f) == 1f && Mathf.Abs(pedal.Read(0f) - 0.5f) < 0.01f, "pedal resting at +1");
        var gasHalf = new WheelAxis { Axis = 1, From = 0f, To = -1f };
        var brakeHalf = new WheelAxis { Axis = 1, From = 0f, To = 1f };
        Expect(gasHalf.Read(-1f) == 1f && gasHalf.Read(1f) == 0f && brakeHalf.Read(1f) == 1f && brakeHalf.Read(-1f) == 0f,
            "combined gas/brake axis");

        // the wheel's Godot device leaves every pad binding, and comes back
        int before = PadEvents(PlayerInput.MoveLeft, -1);
        PlayerInput.SetIgnoredJoypads(new[] { 0 });
        Expect(PadEvents(PlayerInput.MoveLeft, -1) == 0 && PadEvents("ui_accept", -1) == 0, "pad bindings kept every device while a wheel is ignored");
        // loading a world installs the input again: the wheel must stay out of the pad bindings
        PlayerInput.Install(root);
        Expect(PadEvents(PlayerInput.MoveLeft, -1) == 0 && PadEvents(PlayerInput.Throttle, -1) == 0,
            "a second Install gave the ignored wheel back to the pad bindings");
        PlayerInput.SetIgnoredJoypads(Array.Empty<int>());
        Expect(before > 0 && PadEvents(PlayerInput.MoveLeft, -1) == before, $"pad bindings restored ({before} before)");

        // a statement, not `failures += Forces(...)`: that read failures before Forces added to it
        Forces(Expect);
        failures += SdlLoads() ? 0 : 1;
        GD.Print(failures == 0 ? "[wheel] RESULT: PASS" : $"[wheel] RESULT: FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Force feedback's numbers: the vehicles' feel and the soft lock. Failures go through <paramref name="expect"/>.</summary>
    private static void Forces(Action<bool, string> expect)
    {
        const float Dt = 1f / 60f;
        // a car held in a steady right-hand bend; returns its last feel
        WheelFeel Hold(CarSpec spec, float wheelDeg, float speed, Audio.Surface surface = Audio.Surface.Asphalt, float seconds = 2f)
        {
            var car = new Car(spec);
            var m = new RideMotion { Speed = speed };
            for (float t = 0; t < seconds; t += Dt)
            {
                m.Speed = speed;   // held at speed: the bend, not the drag, is measured
                car.Step(new RideInput(0.3f, 0f, 0f, false, WheelAngle: Mathf.DegToRad(wheelDeg)), new RideGround(true, 0f, surface), Dt, ref m);
            }
            return car.Feel;
        }

        var profileWas = GameSettings.Current.RideProfile;
        GameSettings.Current.RideProfile = RideProfile.Sim;
        foreach (var spec in CarCatalog.All)
        {
            var straight = Hold(spec, 0f, 20f);
            expect(Mathf.Abs(straight.Torque) < 0.02f, $"{spec.Label}: straight ahead the wheel pulls {straight.Torque:F3}");
            var bend = Hold(spec, 30f, 20f);
            expect(bend.Torque < -0.05f, $"{spec.Label}: steered right the wheel should pull back left, got {bend.Torque:F3}");
            var ice = Hold(spec, 30f, 20f, Audio.Surface.Ice);
            expect(Mathf.Abs(ice.Torque) < Mathf.Abs(bend.Torque), $"{spec.Label}: ice {ice.Torque:F3} should be lighter than tarmac {bend.Torque:F3}");
        }
        var ae86 = CarCatalog.All[0];
        var gravel = Hold(ae86, 0f, 20f, Audio.Surface.Gravel);
        var tarmac = Hold(ae86, 0f, 20f);
        expect(gravel.Road > 0.1f && tarmac.Road == 0f, $"road: gravel {gravel.Road:F2} against tarmac {tarmac.Road:F2}");
        var (idle, idleHz) = WheelFeel.EngineFrom(900f, 0f);
        var (high, highHz) = WheelFeel.EngineFrom(7000f, 0.95f);
        expect(idle > 0f && high > idle && highHz > idleHz && highHz <= 60f,
            $"engine: {idle:F2} at {idleHz:F0} Hz idling, {high:F2} at {highHz:F0} Hz near the redline");
        var parked86 = Hold(ae86, 0f, 0f, seconds: 0.1f);
        var parkedFd = Hold(CarCatalog.All[1], 0f, 0f, seconds: 0.1f);
        expect(parked86.Weight > parkedFd.Weight && parkedFd.Weight > 0f, $"parked: unassisted AE86 {parked86.Weight:F2}, FD3S {parkedFd.Weight:F2}");
        GD.Print($"[wheel] AE86 at 72 km/h, 30° right: torque {Hold(ae86, 30f, 20f).Torque:F2}; on ice {Hold(ae86, 30f, 20f, Audio.Surface.Ice).Torque:F2}; "
            + $"road gravel {gravel.Road:F2} / tarmac {tarmac.Road:F2}; parked weight {parked86.Weight:F2}");
        // the parked and crawling feel, for reading: aligning torque by speed and wheel angle
        foreach (float v in new[] { 0f, 0.5f, 1f, 2f, 4f, 8f })
        {
            var line = new System.Text.StringBuilder($"[wheel] AE86 torque at {v,3:F1} m/s:");
            foreach (float deg in new[] { 45f, 90f, 180f, 360f, 540f, 620f })
                line.Append($"  {deg:F0}° {Hold(ae86, deg, v, seconds: 1f).Torque,5:F2}");
            GD.Print(line.ToString());
        }
        GameSettings.Current.RideProfile = profileWas;

        // the tyre curve through the trail: past the peak the wheel goes light
        float Aligned(float alpha) => WheelFeel.Aligning(1000f * Mathf.Sin(1.45f * Mathf.Atan(14f * alpha)), alpha, 1000f, 20f);
        expect(Aligned(0.1f) > Aligned(0.3f) && Aligned(0.3f) > 0f, $"light past the peak: {Aligned(0.1f):F2} at 0.1 rad, {Aligned(0.3f):F2} at 0.3");

        foreach (var heavy in HeavyCatalog.All)
        {
            if (Rideable.Create(heavy.Kind) is not Truck truck) continue;
            var m = new RideMotion { Speed = 12f };
            for (float t = 0; t < 2f; t += Dt)
            {
                m.Speed = 12f;
                truck.Step(new RideInput(0.3f, 0f, 0f, false, WheelAngle: Mathf.DegToRad(200f)), new RideGround(true, 0f), Dt, ref m);
            }
            expect(truck.Feel.Torque < -0.02f, $"{heavy.Label}: steered right the wheel should pull back left, got {truck.Feel.Torque:F3}");
        }

        float r900 = Mathf.DegToRad(900f), r1260 = Mathf.DegToRad(1260f), r1080 = Mathf.DegToRad(1080f);
        expect(float.IsPositiveInfinity(SteeringWheel.SoftLockAt(r1260, r900)), "no soft lock when the range is stretched over the lock");
        float at = SteeringWheel.SoftLockAt(r900, r1080);
        expect(Mathf.IsEqualApprox(at, r900 / 2), "soft lock at the vehicle's lock");
        expect(SteeringWheel.SoftLock(at - 0.01f, at) == 0f && SteeringWheel.SoftLock(at + 0.2f, at) < -0.5f
            && SteeringWheel.SoftLock(-at - 0.5f, at) == 1f, "soft lock pushes back toward centre");
        var s = new WheelSettings { RangeDeg = 1080f };
        var into = SteeringWheel.Compose(default, r900, at + 0.1f, 3f, s);
        var back = SteeringWheel.Compose(default, r900, at + 0.1f, -3f, s);
        var deep = SteeringWheel.Compose(default, r900, at + 0.5f, 0f, s);
        expect(into.Constant < back.Constant && deep.Constant <= -0.99f && into.Damper > 0.6f,
            $"soft lock damps the rim: {into.Constant:F2} going in, {back.Constant:F2} coming back, damper {into.Damper:F2}");
    }

    private static int PadEvents(string action, int device) =>
        InputMap.ActionGetEvents(action).Count(e => e is InputEventJoypadButton or InputEventJoypadMotion && e.Device == device);

    private static bool SdlLoads()
    {
        try
        {
            if (!SDL.SDL3.SDL_Init(SDL.SDL_InitFlags.SDL_INIT_JOYSTICK))
            {
                GD.Print($"[wheel] FAIL SDL_Init: {SDL.SDL3.SDL_GetError()}");
                return false;
            }
            using var ids = SDL.SDL3.SDL_GetJoysticks();
            GD.Print($"[wheel] SDL3 loaded, {ids?.Count ?? 0} joysticks");
            SDL.SDL3.SDL_QuitSubSystem(SDL.SDL_InitFlags.SDL_INIT_JOYSTICK);
            return true;
        }
        catch (Exception e)
        {
            GD.Print($"[wheel] FAIL SDL3 did not load: {e.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // --wheelwatch A|B
    // ---------------------------------------------------------------------------------------

    private const double DriveSeconds = 20, WatchSeconds = 90;
    private readonly string _role = WatchRole ?? "A";
    private double _time, _driven, _report;
    private bool _mounted, _done;
    private float _worstError, _min, _max;
    private int _samples;

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _time += delta;
        if (ForceCheckRequested) { PushTest(); return; }
        if (_role == "A") Drive(delta);
        else Watch();
        if (!_done && _time > WatchSeconds + 30) Finish(false, "timed out");
    }

    private FootPlayer? Local()
    {
        if (Multiplayer.MultiplayerPeer is not { } peer
            || peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected) return null;
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p.IsMultiplayerAuthority() && p.Name == Multiplayer.GetUniqueId().ToString())
                return p;
        return null;
    }

    private void Drive(double delta)
    {
        var me = Local();
        if (me == null) return;
        if (!_mounted)
        {
            if (!SteeringWheel.Simulated) { Finish(false, "A needs --fakewheel"); return; }
            if (!me.IsOnFloor()) return;
            _mounted = me.SetRide((RideKind)CarCatalog.First);
            GD.Print(_mounted ? $"[wheelwatch] A in {me.Vehicle?.Label}" : "[wheelwatch] A: mount refused");
            return;
        }
        if (me.Vehicle is not Car car) { Finish(false, "A is not in a car"); return; }

        _driven += delta;
        // what the last step was given, so a frame hitch is not counted as steering lag
        float fed = me.LastRideInput.WheelAngle;
        if (float.IsNaN(fed)) { Finish(false, "the car was not given the wheel's angle"); return; }
        float want = Mathf.Clamp(-fed / car.Spec.SteerRatio, -car.Spec.MaxSteer, car.Spec.MaxSteer);
        if (_driven > 0.5)
        {
            _worstError = Mathf.Max(_worstError, Mathf.Abs(car.SteerAngle - want));
            Track(car.SteerAngle);
        }
        if ((_report += delta) >= 1.0)
        {
            _report = 0;
            GD.Print($"[wheelwatch] A t={_driven,4:F1}s wheel {Mathf.RadToDeg(SteeringWheel.Angle),6:F1}°  road {Mathf.RadToDeg(car.SteerAngle),6:F2}°"
                + $"  want {Mathf.RadToDeg(want),6:F2}°  v={me.RideSpeed * 3.6f,5:F1} km/h");
        }
        if (_driven >= DriveSeconds)
        {
            bool ok = _worstError < Mathf.DegToRad(0.05f) && _max > 0.1f && _min < -0.1f;
            Finish(ok, $"worst error {Mathf.RadToDeg(_worstError):F3}°, road wheel {Mathf.RadToDeg(_min):F1}°..{Mathf.RadToDeg(_max):F1}°");
        }
    }

    private void Watch()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
        {
            if (node is not FootPlayer p || p.IsMultiplayerAuthority() || CarCatalog.For(p.Ride) == null) continue;
            Track(p.Anim.X);
            if ((_report += 1.0 / Engine.PhysicsTicksPerSecond) >= 1.0)
            {
                _report = 0;
                GD.Print($"[wheelwatch] B sees {p.Name} in {p.Ride}: road wheel {Mathf.RadToDeg(p.Anim.X),6:F2}°");
            }
        }
        if (_samples > 0 && _max > 0.1f && _min < -0.1f)
            Finish(true, $"remote road wheel {Mathf.RadToDeg(_min):F1}°..{Mathf.RadToDeg(_max):F1}° over {_samples} frames");
        else if (_time > WatchSeconds)
            Finish(false, _samples == 0 ? "never saw a remote car" : $"remote road wheel only {Mathf.RadToDeg(_min):F1}°..{Mathf.RadToDeg(_max):F1}°");
    }

    private int _pushStage;
    private double _stageAt;
    /// <summary>The wheel's angle before and after each push: right, then left.</summary>
    private float _beforeRight, _afterRight, _beforeLeft;

    /// <summary>--ffbcheck: wait for the forces, push right, rest, push left, judge.</summary>
    private void PushTest()
    {
        if (_pushStage == 0)
        {
            if (!SteeringWheel.HasForceFeedback)
            {
                if (_time > 15) Finish(false, SteeringWheel.DeviceName == null ? "no wheel" : $"{SteeringWheel.DeviceName} has no force feedback SDL can open");
                return;
            }
            // give the wheel a moment after its forces open before reading where it rests
            if (_stageAt == 0) _stageAt = _time + 1.0;
            if (_time < _stageAt) return;
            _beforeRight = SteeringWheel.Angle;
            SteeringWheel.Test(0.3f, 0.5f);
            (_pushStage, _stageAt) = (1, _time + 0.6);
        }
        else if (_pushStage == 1 && _time >= _stageAt)
        {
            _afterRight = SteeringWheel.Angle;
            (_pushStage, _stageAt) = (2, _time + 0.6);
        }
        else if (_pushStage == 2 && _time >= _stageAt)
        {
            _beforeLeft = SteeringWheel.Angle;
            SteeringWheel.Test(-0.3f, 0.5f);
            (_pushStage, _stageAt) = (3, _time + 0.6);
        }
        else if (_pushStage == 3 && _time >= _stageAt)
        {
            float rightDeg = Mathf.RadToDeg(_afterRight - _beforeRight);
            float leftDeg = Mathf.RadToDeg(SteeringWheel.Angle - _beforeLeft);
            bool moved = Mathf.Abs(rightDeg) > 2f && Mathf.Abs(leftDeg) > 2f;
            bool sense = rightDeg > 0f && leftDeg < 0f;
            GD.Print($"[ffbcheck] {SteeringWheel.DeviceName}: push right turned it {rightDeg:+0.0;-0.0}°, push left {leftDeg:+0.0;-0.0}° "
                + $"(invert {(GameSettings.Current.Wheel.FfbInvert ? "on" : "off")})");
            if (!moved || !sense)
            {
                Finish(false, !moved ? "the wheel hardly moved: hands on it, or forces too weak"
                    : "forces push the wrong way: switch on Invert force");
                return;
            }
            // soft lock: a vehicle whose lock is 60° either side of where the wheel now rests
            (_pushStage, _stageAt, _lockCentre) = (4, _time + 0.5, SteeringWheel.Angle);
        }
        else if (_pushStage >= 4)
            SoftLockStage();
    }

    private float _lockCentre;
    private double _traceAt;
    private const float SoftHalfDeg = 60f;

    /// <summary>
    /// --ffbcheck, second part: a vehicle with its lock 60° either side of centre is "driven" (no
    /// tyre forces), then the wheel pushed right at 35% for 1.2 s — it must stop near the lock, not
    /// run on as it did free. Centred first, so the lock is measured from the middle of the wheel.
    /// </summary>
    private void SoftLockStage()
    {
        float half = Mathf.DegToRad(SoftHalfDeg);
        if (_time >= _traceAt)
        {
            _traceAt = _time + 0.1;
            GD.Print($"[ffbcheck]   stage {_pushStage} t={_time:F1}s wheel {Mathf.RadToDeg(SteeringWheel.Angle),7:F1}°  force {SteeringWheel.LastForces.Constant,6:F2}");
        }
        if (_pushStage == 4)
        {
            // back to the middle: a soft lock is measured from centre, wherever the first pushes left the rim
            SteeringWheel.Drive(default, 2f * half);
            if (Mathf.Abs(SteeringWheel.Angle) > Mathf.DegToRad(5f) && _time < _stageAt + 3)
            {
                SteeringWheel.Test(-Mathf.Sign(SteeringWheel.Angle) * 0.18f, 0.05f);
                return;
            }
            (_pushStage, _stageAt) = (5, _time + 0.4);
        }
        else if (_pushStage == 5)
        {
            SteeringWheel.Drive(default, 2f * half);
            if (_time < _stageAt) return;
            SteeringWheel.Test(0.35f, 1.2f);
            (_pushStage, _stageAt) = (6, _time + 1.2);
        }
        else if (_pushStage == 6)
        {
            SteeringWheel.Drive(default, 2f * half);
            if (_time < _stageAt) return;
            float deg = Mathf.RadToDeg(SteeringWheel.Angle);
            GD.Print($"[ffbcheck] soft lock at {SoftHalfDeg:F0}° (range {GameSettings.Current.Wheel.RangeDeg:F0}°): a 35% push held at {deg:+0.0;-0.0}°");
            bool held = deg > SoftHalfDeg - 15f && deg < SoftHalfDeg + 20f;
            Finish(held, held ? "forces push the way they should, and the soft lock holds"
                : deg <= SoftHalfDeg - 15f ? "the push did not reach the lock" : "the soft lock did not hold the wheel");
        }
    }

    private void Track(float v)
    {
        _min = _samples == 0 ? v : Mathf.Min(_min, v);
        _max = _samples == 0 ? v : Mathf.Max(_max, v);
        _samples++;
    }

    private void Finish(bool ok, string what)
    {
        _done = true;
        GD.Print(ForceCheckRequested ? $"[ffbcheck] RESULT: {(ok ? "PASS" : "FAIL")} ({what})"
            : $"[wheelwatch] {_role} RESULT: {(ok ? "PASS" : "FAIL")} ({what})");
        // A lingers so B, still watching, sees the car keep steering
        GetTree().CreateTimer(_role == "A" && !ForceCheckRequested ? 15 : 1).Timeout += () => GetTree().Quit(ok ? 0 : 1);
    }
}
