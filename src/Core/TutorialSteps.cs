namespace UnitSport.Core;

/// <summary>What a tutorial step waits for the player to do.</summary>
public enum TutorialGoal { Look, Walk, RunJump, Ride, Camera, Map, Fly, Done }

/// <summary>
/// One step of the first-run tutorial (#517). The texts carry <c>{action}</c> placeholders that
/// <see cref="InputHints"/> fills for the device in hand; <see cref="Pad"/> and <see cref="Vr"/>
/// replace <see cref="Keys"/> where a pad or the headset needs other words (a stick, your head).
/// </summary>
public sealed record TutorialStep(TutorialGoal Goal, string Title, string Keys, string? Pad = null, string? Vr = null);

/// <summary>
/// What the player did since the current step began, fed by <see cref="Tutorial"/> every frame.
/// Plain data, so the rules in <see cref="TutorialSteps.Met"/> are unit-tested without Godot.
/// </summary>
public struct TutorialSignals
{
    public float LookedDegrees;
    public float WalkedMeters;
    public bool Jumped, Ran, Rode, CameraToggled, MapOpened, Flew, HelpOpened;
    public float Seconds;
}

/// <summary>
/// The first-run tutorial's steps, in order, and when each is done. Each step ends when the player
/// does the thing, never on a "Next" click: the point is that they have done it once.
/// </summary>
public static class TutorialSteps
{
    /// <summary>Degrees of looking about that count as "looked around".</summary>
    public const float LookDegrees = 120;
    /// <summary>Metres walked on foot that count as "walked".</summary>
    public const float WalkMeters = 12;
    /// <summary>A step only the keyboard can do (the map) moves on by itself after this long.</summary>
    public const float OptionalSeconds = 14;
    /// <summary>The last card stays this long unless F1 is opened.</summary>
    public const float DoneSeconds = 12;

    private static readonly TutorialStep[] Steps =
    {
        new(TutorialGoal.Look, "Welcome to Switzerland",
            "This is the real country, mountain by mountain. Move the mouse to look around.",
            Pad: "This is the real country, mountain by mountain. Look around with {look_right}.",
            Vr: "This is the real country, mountain by mountain. Turn your head to look around."),
        new(TutorialGoal.Walk, "Walk",
            "Walk with {move_forward} {move_left} {move_back} {move_right}.",
            Pad: "Walk with {move_forward}."),
        new(TutorialGoal.RunJump, "Run and jump",
            "Hold {sprint} to run, press {jump} to jump. Walls and ledges can be climbed.",
            Pad: "Press {sprint} to run, {jump} to jump. Walls and ledges can be climbed."),
        new(TutorialGoal.Ride, "Travel",
            "{ride_menu} opens the travel menu: bikes, skis, cars, motorbikes, trucks, boats and aircraft. Pick one and Ride.",
            Pad: "{interact_mount} with nothing in front of you opens the travel menu: bikes, skis, cars, motorbikes, trucks, boats and aircraft. Pick one and Ride."),
        new(TutorialGoal.Camera, "Camera",
            "{camera_toggle} switches between first and third person, on foot and at the wheel. {interact_mount} gets out."),
        new(TutorialGoal.Map, "Go anywhere",
            "{teleport} opens the map: type a town, a peak or a lake and jump there."),
        new(TutorialGoal.Fly, "Fly over it",
            "{toggle_mode} lifts you into the fly camera: {fly_up} / {fly_down} climb and sink. {toggle_mode} again lands you where you look."),
        new(TutorialGoal.Done, "That's the basics",
            "{help} lists every control, {menu} opens the menu. Enjoy the country!"),
    };

    /// <summary>
    /// The steps that apply: no travel menu where vehicles cannot be spawned (online without
    /// admin, a Battle Royale), no map or fly camera in a match.
    /// </summary>
    public static IReadOnlyList<TutorialStep> For(bool canSpawnVehicles, bool inMatch) =>
        Steps.Where(s => s.Goal switch
        {
            TutorialGoal.Ride => canSpawnVehicles,
            TutorialGoal.Map or TutorialGoal.Fly => !inMatch,
            _ => true,
        }).ToArray();

    /// <summary>The text for the device in hand: the VR words, else the pad words, else the keyboard's.</summary>
    public static string Body(TutorialStep step, bool pad, bool vr) =>
        (vr ? step.Vr ?? step.Pad : pad ? step.Pad : null) ?? step.Keys;

    /// <summary>Has the player done what <paramref name="goal"/> asks, since the step began?</summary>
    public static bool Met(TutorialGoal goal, in TutorialSignals s) => goal switch
    {
        TutorialGoal.Look => s.LookedDegrees >= LookDegrees,
        TutorialGoal.Walk => s.WalkedMeters >= WalkMeters,
        TutorialGoal.RunJump => s.Ran && s.Jumped,
        TutorialGoal.Ride => s.Rode,
        TutorialGoal.Camera => s.CameraToggled,
        // a pad cannot type a place name: the map step lets itself be read, then goes
        TutorialGoal.Map => s.MapOpened || s.Seconds >= OptionalSeconds,
        TutorialGoal.Fly => s.Flew,
        TutorialGoal.Done => s.HelpOpened || s.Seconds >= DoneSeconds,
        _ => true,
    };

    /// <summary>How far along the step is, 0..1, for the card's bar.</summary>
    public static float Progress(TutorialGoal goal, in TutorialSignals s) => goal switch
    {
        TutorialGoal.Look => Math.Clamp(s.LookedDegrees / LookDegrees, 0, 1),
        TutorialGoal.Walk => Math.Clamp(s.WalkedMeters / WalkMeters, 0, 1),
        TutorialGoal.RunJump => (s.Ran ? 0.5f : 0) + (s.Jumped ? 0.5f : 0),
        TutorialGoal.Done => Math.Clamp(s.Seconds / DoneSeconds, 0, 1),
        _ => Met(goal, s) ? 1 : 0,
    };
}
