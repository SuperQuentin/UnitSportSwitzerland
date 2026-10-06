using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Working a wheel loader (#612). <c>dig_mode</c> (C, pad B, the VR cab's button) toggles work mode,
/// as on the excavator; in it the right stick and the arrows raise the arm (Y) and tilt the bucket
/// (X), each running while held and stopping where let go. Unlike the excavator it keeps driving in
/// work mode — a loader fills its bucket by driving into the heap — so only the right stick changes
/// meaning, and the camera keeps the mouse (<see cref="ApplyStickLook"/>).
/// </summary>
public partial class FootPlayer
{
    private void WorkBucket(WheelLoader loader, float dt)
    {
        if (SeatIndex != 0 || Npc)
        {
            loader.Levers = default;
            loader.Work(dt);
            return;
        }
        if (Input.IsActionJustPressed(PlayerInput.DigMode)) loader.Working = !loader.Working;
        loader.Levers = (
            PlayerInput.Strength(PlayerInput.ArmBoomUp) - PlayerInput.Strength(PlayerInput.ArmBoomDown),
            // the bucket's angle grows as it rolls back: curling it in is the positive way here
            PlayerInput.Strength(PlayerInput.ArmBucketCurl) - PlayerInput.Strength(PlayerInput.ArmBucketDump));
        loader.Work(dt);
    }
}
