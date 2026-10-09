using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Working a compact roller (#614): <c>dig_mode</c> (C, pad B, the VR console's red button), the
/// excavator's and the loader's work toggle, sets the drums vibrating or stops them. It drives the
/// same either way.
/// </summary>
public partial class FootPlayer
{
    private void WorkDrums(CompactRoller roller)
    {
        // only the driver sets it, and a scripted one through the same binding (XrPad.Press)
        if (SeatIndex != 0 || Npc) return;
        if (Input.IsActionJustPressed(PlayerInput.DigMode)) roller.Vibrating = !roller.Vibrating;
    }
}
