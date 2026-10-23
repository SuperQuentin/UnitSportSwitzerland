using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;
using UnitSport.XR;

namespace UnitSport.Player;

/// <summary>
/// <c>--ebikecheck --world flat --systems physics,ui</c> (#1167): the e-bike's remote and battery on the
/// <b>real binding</b> (<c>engine_toggle</c>, pressed through <see cref="XrPad.Press"/> as the pad, the VR
/// stick and the VR poke all send it):
/// <list type="bullet">
/// <item>a fresh e-bike is full and in Tour; the binding steps Sport, Turbo, Off, Eco, Tour;</item>
/// <item>riding in Turbo the battery drops by exactly the motor's watts over its efficiency;</item>
/// <item>Off adds nothing and takes nothing; a flat battery stops the help;</item>
/// <item>parked (the state through the network's dictionary) and taken again, the mode and the charge
/// are the ones left, and the parked copy is built with them; a fresh one is full again;</item>
/// <item>the low frame has the same remote; the VR poke sits on the drawn remote, in reach at the left
/// grip; F1 and the touch More sheet call the binding the assist level.</item>
/// </list>
/// </summary>
public partial class EBikeCheck : Node
{
    public static bool Requested => CmdArgs.Has("--ebikecheck");

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private bool _started;

    public EBikeCheck(WorldOrigin origin) => _origin = origin;

    private void Expect(bool ok, string what)
    {
        GD.Print($"[ebike] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private async Task Tap(string action)
    {
        XrPad.Press(action, true);
        await Frames(2);
        XrPad.Press(action, false);
        await Frames(2);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_started) return;
        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);
        if (!TestWorld.TryGround(null, at, out float ground)) return;
        _started = true;
        // its origin, for the parked state's LV95 position
        _player = new FootPlayer { Name = "Probe", Origin = _origin };
        AddChild(_player);
        _player.GlobalPosition = new Vector3(at.X, ground + 1.0f, at.Z);
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
        _ = Guarded();
    }

    /// <summary>A throw in the run is a failure with its message, not a silent hang until the timeout.</summary>
    private async Task Guarded()
    {
        try { await Run(); }
        catch (System.Exception e)
        {
            Expect(false, $"threw {e.GetType().Name}: {e.Message}");
            Finish();
        }
    }

    /// <summary>
    /// Rides <paramref name="seconds"/> of game time at <paramref name="throttle"/>, adding up what the
    /// motor's watts should have drawn frame by frame (Wh).
    /// </summary>
    private async Task<(float Drawn, float MaxMotor)> Ride(FootPlayer me, Bicycle bike, float throttle, double seconds)
    {
        me.RideControls = () => new RideInput(throttle, 0f, 0f, false);
        float dt = 1f / Engine.PhysicsTicksPerSecond, drawn = 0f, max = 0f;
        int frames = (int)(seconds * Engine.PhysicsTicksPerSecond);
        for (int i = 0; i < frames; i++)
        {
            await Frames(1);
            drawn += BikePhysics.BatteryDrawWh(bike.MotorWatts, dt);
            max = Mathf.Max(max, bike.MotorWatts);
        }
        return (drawn, max);
    }

    private async Task Stop(FootPlayer me)
    {
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        for (int i = 0; i < 600 && me.GroundSpeed > 0.2f; i++) await Frames(1);
    }

    private async Task Run()
    {
        var me = _player!;
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await Frames(1);

        // ---- a fresh e-bike, and the remote's button --------------------------------------------
        Expect(me.SetRide(RideKind.EBike) && me.Vehicle is Bicycle { Spec.Assist: not null }, "mounted the e-bike");
        if (me.Vehicle is not Bicycle bike || bike.Spec.Assist is not { } motor) { Finish(); return; }
        Expect(bike.Level == AssistLevel.Tour && Mathf.IsEqualApprox(bike.Charge, 1f), $"fresh: Tour and full ({bike.Level}, {bike.Charge:P0})");
        var steps = new[] { AssistLevel.Sport, AssistLevel.Turbo, AssistLevel.Off, AssistLevel.Eco, AssistLevel.Tour };
        bool stepped = true;
        foreach (var want in steps)
        {
            await Tap(PlayerInput.EngineToggle);
            stepped &= bike.Level == want;
        }
        Expect(stepped && me.EngineOn, $"engine_toggle steps Sport, Turbo, Off, Eco, Tour (now {bike.Level}), no engine to switch");

        // ---- Turbo drains it by the motor's watts ----------------------------------------------
        await Tap(PlayerInput.EngineToggle);
        await Tap(PlayerInput.EngineToggle);
        Expect(bike.Level == AssistLevel.Turbo, "two more presses: Turbo");
        float before = bike.BatteryWh;
        var (drawn, max) = await Ride(me, bike, 1f, 20.0);
        float used = before - bike.BatteryWh;
        Expect(max > 300f && used > 0.5f, $"20 s of Turbo from a standstill: the motor up to {max:F0} W, {used:F2} Wh used");
        Expect(Mathf.Abs(used - drawn) < 0.03f * drawn + 0.01f,
            $"the battery fell by the motor's watts over {ElectricAssist.Efficiency:P0} ({used:F3} Wh, expected {drawn:F3})");
        await Stop(me);

        // ---- Off: no help, no drain ------------------------------------------------------------
        await Tap(PlayerInput.EngineToggle);
        Expect(bike.Level == AssistLevel.Off, "Turbo round to Off");
        before = bike.BatteryWh;
        var (_, offMax) = await Ride(me, bike, 1f, 6.0);
        Expect(offMax == 0f && bike.BatteryWh == before, $"Off: the motor adds {offMax:F0} W and the battery keeps {bike.BatteryWh:F2} Wh");
        await Stop(me);

        // ---- a flat battery stops the help -----------------------------------------------------
        bike.Level = AssistLevel.Turbo;
        bike.BatteryWh = 0.02f;
        await Ride(me, bike, 1f, 6.0);
        Expect(bike.BatteryWh == 0f && bike.MotorWatts == 0f, $"flat: {bike.BatteryWh:F3} Wh left, the motor adds {bike.MotorWatts:F0} W");
        await Stop(me);

        // ---- parked and taken again ------------------------------------------------------------
        bike.Level = AssistLevel.Sport;
        bike.BatteryWh = 0.42f * motor.BatteryWh;
        var state = VehicleState.FromDict(me.CaptureVehicle(false).ToDict());
        Expect(state.CreateRide() is Bicycle { Level: AssistLevel.Sport } parked && Mathf.Abs(parked.Charge - 0.42f) < 0.001f,
            $"the parked state builds an e-bike in Sport at 42% (flags {state.Flags}, load {state.Load:F3})");
        me.SetRide(RideKind.OnFoot);
        await Frames(10);
        me.TakeVehicle(state, 0);
        await Frames(10);
        Expect(me.Vehicle is Bicycle { Level: AssistLevel.Sport } again && Mathf.Abs(again.Charge - 0.42f) < 0.001f,
            $"taken again: {(me.Vehicle as Bicycle)?.Level}, {(me.Vehicle as Bicycle)?.Charge:P1}");
        me.SetRide(RideKind.OnFoot);
        await Frames(5);
        Expect(me.SetRide(RideKind.EBike) && me.Vehicle is Bicycle { Level: AssistLevel.Tour, Charge: 1f },
            "a fresh one from the picker is full and in Tour again");

        // ---- the low frame, the VR poke and the help texts --------------------------------------
        me.SetRide(RideKind.OnFoot);
        await Frames(5);
        Expect(me.SetRide(RideKind.EBikeOpen) && me.Vehicle is Bicycle { Spec.Assist: not null } open, "mounted the low frame");
        await Tap(PlayerInput.EngineToggle);
        Expect(me.Vehicle is Bicycle { Level: AssistLevel.Sport }, "its remote steps too");
        if (me.Vehicle is Bicycle low)
        {
            var poke = XrCabControls.EBikeRemote(low);
            Expect(poke.X < -0.1f && poke.Y < -0.2f && poke.Z < 0f && poke.Length() is > 0.35f and < 0.8f,
                $"the VR poke is at the left grip, ahead and below the eyes, in reach ({poke.X:F2}, {poke.Y:F2}, {poke.Z:F2}; {poke.Length():F2} m)");
        }
        string? touch = TouchMore.LabelFor(new TouchState(TouchMode.Ride, TouchFlags.None, nameof(Bicycle)), PlayerInput.EngineToggle);
        Expect(touch != null && touch.StartsWith("Assist level"), $"the touch More sheet names it \"{touch}\"");
        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0
            ? "[ebike] RESULT: ok — the remote steps the modes and the battery drains, stops and parks"
            : $"[ebike] RESULT: FAILED {_failures} check(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
