using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

// An airliner's cockpit switches on a pad (#421): the D-pad's lights and speedbrake directions held
// work the autopilot and the parking brake, which had no button (in VR the right stick's → and ←,
// which XrPad sends as the same D-pad buttons, besides the pokes on the panel).
public partial class FootPlayer
{
    /// <summary>The pad direction held at an airliner's controls, and for how long, s.</summary>
    private string? _padHeld;
    private float _padHeldFor;

    /// <summary>A D-pad press held this long works the second switch instead of the first.</summary>
    private const float PadHold = 0.5f;

    /// <summary>
    /// D-pad → and ← at an airliner's controls (a pad's or VR's, never a key): a tap is the lights or the
    /// speedbrake when let go, a hold the autopilot or the parking brake. True when this event was one.
    /// </summary>
    private bool AirlinerPadSwitch(InputEvent e, Airliner airliner)
    {
        if (e is not InputEventJoypadButton jb || e.IsEcho() || SeatIndex != 0) return false;
        string? action = e.IsAction(PlayerInput.LightsToggle) ? PlayerInput.LightsToggle : e.IsAction(PlayerInput.Speedbrake) ? PlayerInput.Speedbrake : null;
        if (action == null) return false;
        if (jb.Pressed)
        {
            _padHeld = action;
            _padHeldFor = 0f;
        }
        else if (_padHeld == action)
        {
            // let go before the hold: the tap's own switch
            airliner.Command(action == PlayerInput.LightsToggle ? AirlinerCommand.Lights : AirlinerCommand.Speedbrake);
            _padHeld = null;
        }
        return true;
    }

    /// <summary>Per frame at an airliner's controls: a D-pad direction held long enough works its second switch.</summary>
    private void AirlinerPadHold(float dt, Airliner airliner)
    {
        if (_padHeld == null) return;
        _padHeldFor += dt;
        if (_padHeldFor < PadHold) return;
        if (_padHeld == PlayerInput.Speedbrake) airliner.Command(AirlinerCommand.ParkingBrake);
        else if (Airliner.Handling != AirlinerHandling.Sim) Announced?.Invoke("Autopilot: Light sim only (Settings)", false);
        else airliner.Command(AirlinerCommand.Autopilot);
        _padHeld = null;
    }
}
