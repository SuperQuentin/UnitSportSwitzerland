using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Working a forklift's mast (#583). The forks run while a paddle is held and stop where they are
/// let go, which is how a real one behaves and is also why the control is a hold and not a press.
///
/// <para>
/// The paddles are <c>shift_up</c> / <c>shift_down</c>, free on a machine with no gearbox and
/// already bound on all three devices: <b>Shift / Ctrl</b> on the keyboard, <b>RB / LB</b> on a pad,
/// and in VR either of those through <c>XrPad</c>'s grips or, properly, the cab lever
/// <c>XrCabControls</c> builds for the "forklift" context. Nothing new was bound for it.
/// </para>
/// </summary>
public partial class FootPlayer
{
    /// <summary>After the ride's step: the mast toward wherever the paddles are taking it.</summary>
    private void WorkMast(Forklift fork)
    {
        // only the driver works it, and never a scripted one: a check drives it through TargetLift
        if (SeatIndex != 0 || Npc)
            return;
        bool up = PlayerInput.Held(PlayerInput.ShiftUp), down = PlayerInput.Held(PlayerInput.ShiftDown);
        // held: run to the stop, which the mast's own rate takes its time reaching. Let go (or both
        // held): stop where the forks are now, as a real mast does — no drift on to a target.
        fork.TargetLift = up == down ? fork.Lift
            : up ? ForkliftLayout.MaxLift : ForkliftLayout.MinLift;
    }
}
