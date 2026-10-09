using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Shifting by hand (#290): a car's sequential and manual boxes (<see cref="Car.Gearbox"/>), and a
/// steering wheel's H-shifter and clutch pedal, for the cars and the trucks alike.
/// </summary>
public partial class FootPlayer
{
    /// <summary>The wheel's H-shifter lever as last seen, for the truck's and the car's box.</summary>
    private readonly HeldShifter _shifter = new();

    /// <summary>
    /// A gate the wheel's H-shifter is holding: the lever is polled (<see cref="HeldShifter"/>), so its
    /// press is not a key's and must not pick the gate a second time.
    /// </summary>
    private static bool ShifterEvent(InputEvent e) =>
        e is InputEventAction a && SteeringWheel.GateOf(a.Action) != int.MinValue && SteeringWheel.Holds(a.Action);

    /// <summary>
    /// The pad's shoulders shift a car that is not automatic, so they are not the trick and the boost
    /// there (F and Q still are). The VR grips replay as the shoulders and go with them.
    /// </summary>
    private bool ShouldersShift => _ride is Car { Gearbox: not CarGearbox.Automatic } && PlayerInput.LastDevice == InputDevice.Gamepad;

    /// <summary>The player's car box, ignition and clutch into the car before its step; and the H-shifter's lever.</summary>
    private void PrepareCar(Car car)
    {
        bool driver = !Npc && RideControls == null && SeatIndex == 0;
        float u = _motion.Speed * Mathf.Cos(_motion.Slip);
        car.SetGearbox(driver ? GameSettings.Current.CarGearbox : CarGearbox.Automatic, u);
        car.EngineRunning = EngineOn;
        car.ClutchHeld = driver && PlayerInput.HeldButton(PlayerInput.Clutch);
        car.ClutchFoot = driver ? PlayerInput.WheelPedal(PlayerInput.Clutch) : 0f;
        int? wheelLever = driver ? SteeringWheel.ShifterGate : null;
        // on the automatic the H-shifter is the selector: P R N D
        car.Selector = car.Gearbox == CarGearbox.Automatic && wheelLever is { } sel ? HeldShifter.Selector(sel) : DriveSelector.None;
        if (car.Gearbox == CarGearbox.Manual && wheelLever is { } lever
            && _shifter.Step(lever, car.Gear, car.ClutchPedal, out bool retry) is { } gate)
        {
            bool took = car.SelectGate(gate, u);
            _shifter.Took(took);
            if (retry && !took) car.Event = null;
        }
    }

    /// <summary>What the car's box decided that the driver hears about: a stall, a grind.</summary>
    private void AfterCarStep(Car car)
    {
        if (car.Stalled)
        {
            car.Stalled = false;
            EngineOn = false;
            EngineToggled?.Invoke(false);
        }
        AnnounceBox(car.Event);
        car.Event = null;
    }

    /// <summary>A gearbox's event, said to the driver.</summary>
    private void AnnounceBox(string? what)
    {
        switch (what)
        {
            case "stall": Announced?.Invoke("STALLED", false); break;
            case "grind": Announced?.Invoke(InputHints.Format("GRIND — clutch ({clutch}) first"), false); break;
            case "overrev": Announced?.Invoke("Too fast for that gear", false); break;
            case "nogear": Announced?.Invoke("No such gear", false); break;
            case "air": Announced?.Invoke("LOW AIR — SPRING BRAKES ON", false); break;
        }
    }

    /// <summary>A car's shift keys, in a box that is not automatic. True when this event was one of them.</summary>
    private bool HandleCarGearInput(InputEvent e, Car car)
    {
        if (car.Gearbox == CarGearbox.Automatic || SeatIndex != 0 || !e.IsPressed() || e.IsEcho()) return false;
        float u = _motion.Speed * Mathf.Cos(_motion.Slip);
        if (e.IsActionPressed(PlayerInput.ShiftUp)) { car.ShiftUp(u); return true; }
        if (e.IsActionPressed(PlayerInput.ShiftDown)) { car.ShiftDown(u); return true; }
        if (car.Gearbox != CarGearbox.Manual) return false;
        if (ShifterEvent(e)) return true;
        for (int g = 0; g < PlayerInput.Gates.Length; g++)
            if (e.IsActionPressed(PlayerInput.Gates[g])) { car.SelectGate(g + 1, u); return true; }
        if (e.IsActionPressed(PlayerInput.GearReverse)) { car.SelectGate(-1, u); return true; }
        if (e.IsActionPressed(PlayerInput.GearNeutral)) { car.SelectGate(0, u); return true; }
        return false;
    }
}
