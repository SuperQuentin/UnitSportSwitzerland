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
    public static int Check()
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
        PlayerInput.SetIgnoredJoypads(Array.Empty<int>());
        Expect(before > 0 && PadEvents(PlayerInput.MoveLeft, -1) == before, $"pad bindings restored ({before} before)");

        failures += SdlLoads() ? 0 : 1;
        GD.Print(failures == 0 ? "[wheel] RESULT: PASS" : $"[wheel] RESULT: FAILED ({failures})");
        return failures == 0 ? 0 : 1;
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

    private void Track(float v)
    {
        _min = _samples == 0 ? v : Mathf.Min(_min, v);
        _max = _samples == 0 ? v : Mathf.Max(_max, v);
        _samples++;
    }

    private void Finish(bool ok, string what)
    {
        _done = true;
        GD.Print($"[wheelwatch] {_role} RESULT: {(ok ? "PASS" : "FAIL")} ({what})");
        // A lingers so B, still watching, sees the car keep steering
        GetTree().CreateTimer(_role == "A" ? 15 : 1).Timeout += () => GetTree().Quit(ok ? 0 : 1);
    }
}
