using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Working an excavator (#611). <c>dig_mode</c> (C, pad B, the VR cab's button) toggles between
/// driving and digging. Digging, the tracks hold and the arm's levers run its joints while they are
/// held, stopping where they are let go, as a hydraulic arm does.
///
/// <para>
/// The levers are named actions bound to the two sticks in the ISO pattern (left: slew and stick,
/// right: boom and bucket), to WASD and the arrows on the keyboard, and in VR to the cab's two
/// joysticks (<c>XrCabControls</c> "excavator"). The right stick is the camera's everywhere else, so
/// <see cref="ApplyStickLook"/> leaves it alone while digging; the mouse still looks round.
/// </para>
/// </summary>
public partial class FootPlayer
{
    /// <summary>After the ride's step: dig mode, and the arm toward wherever its levers are taking it.</summary>
    private void WorkArm(Excavator ex, float dt)
    {
        // only the driver works it, and a scripted one through the same bindings (XrPad.Press)
        if (SeatIndex != 0 || Npc)
        {
            ex.Levers = default;
            ex.Work(ref _motion, dt);
            return;
        }
        if (Input.IsActionJustPressed(PlayerInput.DigMode)) ex.Digging = !ex.Digging;
        ex.Levers = (
            PlayerInput.Strength(PlayerInput.ArmSlewLeft) - PlayerInput.Strength(PlayerInput.ArmSlewRight),
            PlayerInput.Strength(PlayerInput.ArmStickOut) - PlayerInput.Strength(PlayerInput.ArmStickIn),
            PlayerInput.Strength(PlayerInput.ArmBoomUp) - PlayerInput.Strength(PlayerInput.ArmBoomDown),
            // the bucket's angle grows as it opens: curling it in is the negative way
            PlayerInput.Strength(PlayerInput.ArmBucketDump) - PlayerInput.Strength(PlayerInput.ArmBucketCurl));
        ex.Work(ref _motion, dt);
    }
}
