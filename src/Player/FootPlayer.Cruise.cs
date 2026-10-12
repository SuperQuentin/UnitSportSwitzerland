using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

// The speed regulator (#494): Y on the keyboard, a held D-pad → on a pad (R stick → in VR, which
// XrPad sends as the same button), as an airliner's autopilot; a tap of D-pad → is still the lights.
public partial class FootPlayer
{
    /// <summary>The speed regulator of the car, truck, farm machine or motorbike being driven.</summary>
    public CruiseControl Cruise { get; } = new();

    /// <summary>A ride with a regulator: a car, a truck or farm machine, a motorbike.</summary>
    private bool HasCruise => _ride is Car or Truck or Motorbike && SeatIndex == 0 && !Npc;

    /// <summary>D-pad → held at the wheel since this long, s; negative when it is not held.</summary>
    private float _cruisePadFor = -1f;

    /// <summary>The regulator's switch, the key or a pad's held D-pad →. True when this event was one.</summary>
    private bool HandleCruiseInput(InputEvent e)
    {
        if (!HasCruise || e.IsEcho()) return false;
        if (e is InputEventJoypadButton jb && e.IsAction(PlayerInput.LightsToggle))
        {
            if (jb.Pressed) _cruisePadFor = 0f;
            else if (_cruisePadFor >= 0f)
            {
                // let go before the hold: the tap's own switch, the lights
                _cruisePadFor = -1f;
                ToggleLights();
            }
            return true;
        }
        if (!e.IsActionPressed(PlayerInput.Cruise)) return false;
        PressCruise();
        return true;
    }

    /// <summary>The regulator's switch pressed: on at this speed, a new speed, or off.</summary>
    public void PressCruise()
    {
        if (!HasCruise) return;
        bool on = Cruise.Press(ForwardSpeed);
        Announced?.Invoke(on ? $"Cruise {Cruise.SetSpeed * 3.6f:0} km/h" : "Cruise off", on);
    }

    /// <summary>
    /// Per physics step: a D-pad → held long enough works the regulator, and its throttle and brake
    /// go on top of the driver's pedals. Off out of the driver's seat, with the engine off, or braked.
    /// </summary>
    private RideInput CruiseStep(RideInput input, float dt)
    {
        if (!HasCruise || !EngineOn)
        {
            _cruisePadFor = -1f;
            if (Cruise.On) Cruise.Off();
            return input;
        }
        if (_cruisePadFor >= 0f && (_cruisePadFor += dt) >= PadHold)
        {
            _cruisePadFor = -1f;
            PressCruise();
        }
        if (!Cruise.On) return input;
        var (throttle, brake) = Cruise.Apply(input.Throttle, input.Brake, input.Handbrake, ForwardSpeed, dt);
        if (!Cruise.On) Announced?.Invoke("Cruise off", false);
        return input with { Throttle = throttle, Brake = brake };
    }

    /// <summary>The speed along the vehicle's heading, m/s: negative backing up.</summary>
    private float ForwardSpeed => _motion.Speed * Mathf.Cos(_motion.Slip);

    private void ToggleLights()
    {
        if (_ride is Car car) car.Headlights = !car.Headlights;
        else if (_ride is Truck truck) truck.Headlights = !truck.Headlights;
    }
}
