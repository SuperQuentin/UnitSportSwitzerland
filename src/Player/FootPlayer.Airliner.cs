using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

// An airliner's levers and switches (#414): pressed keys handed to the aircraft for its next step.
public partial class FootPlayer
{
    /// <summary>Keys that mean something only at an airliner's controls. True when this event was one of them.</summary>
    private bool HandleAirlinerInput(InputEvent e, Airliner airliner)
    {
        if (!e.IsPressed() || e.IsEcho() || SeatIndex != 0) return false;
        if (e.IsActionPressed(PlayerInput.FlapsDown)) { airliner.Command(AirlinerCommand.FlapsDown); return true; }
        if (e.IsActionPressed(PlayerInput.FlapsUp)) { airliner.Command(AirlinerCommand.FlapsUp); return true; }
        if (e.IsActionPressed(PlayerInput.Speedbrake)) { airliner.Command(AirlinerCommand.Speedbrake); return true; }
        if (e.IsActionPressed(PlayerInput.ParkingBrake)) { airliner.Command(AirlinerCommand.ParkingBrake); return true; }
        if (e.IsActionPressed(PlayerInput.LightsToggle)) { airliner.Command(AirlinerCommand.Lights); return true; }
        // G: the gear lever. On the ground it is locked down (weight on the wheels); the doors are #416's
        if (e.IsActionPressed(PlayerInput.CarDoor))
        {
            if (airliner.State.OnGround && airliner.State.GearDown) Announced?.Invoke("Gear locked down on the ground", false);
            else airliner.Command(AirlinerCommand.Gear);
            return true;
        }
        return false;
    }
}
