using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Working a telehandler (#614). <c>dig_mode</c> (C, pad B, the VR dash button) toggles work mode,
/// as on the loader; in it the right stick and the arrows lift the boom (Y) and tilt the forks (X),
/// and the gear paddles run it out and in (Shift / Ctrl, RB / LB, as the forklift's mast), each
/// running while held and stopping where let go. It keeps driving in work mode. The steering mode
/// is <c>roof_toggle</c>, in <see cref="_UnhandledInput"/>.
/// </summary>
public partial class FootPlayer
{
    private void WorkBoom(Telehandler boom, float dt)
    {
        if (SeatIndex != 0 || Npc)
        {
            boom.Levers = default;
            boom.Work(dt);
            return;
        }
        if (Input.IsActionJustPressed(PlayerInput.DigMode)) boom.Working = !boom.Working;
        boom.Levers = (
            PlayerInput.Strength(PlayerInput.ArmBoomUp) - PlayerInput.Strength(PlayerInput.ArmBoomDown),
            PlayerInput.Strength(PlayerInput.ShiftUp) - PlayerInput.Strength(PlayerInput.ShiftDown),
            // curling tilts the forks back, as it rolls a bucket back
            PlayerInput.Strength(PlayerInput.ArmBucketCurl) - PlayerInput.Strength(PlayerInput.ArmBucketDump));
        boom.Work(dt);
        // its forks lift pallets as a forklift's do (#615): no button, the boom is the interaction
        Items.PalletService.Instance?.Tend(this, boom);
    }
}
