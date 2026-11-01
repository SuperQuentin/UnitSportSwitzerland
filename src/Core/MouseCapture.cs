using Godot;

namespace UnitSport.Core;

/// <summary>
/// The one way the game grabs the mouse. <c>--nocapture</c> turns it off, and so do the probes
/// <c>ClientWorld</c> lists (<see cref="Disabled"/>), and a test's window never touches the pointer
/// at all (<see cref="TestWindow"/>, #1378): an automated check running in a window must not take the
/// pointer from whoever is using the machine meanwhile.
///
/// <para>
/// <see cref="Mode"/> is the pointer mode as the game set it, read and written instead of
/// <c>Input.MouseMode</c> everywhere (a unit test holds that): the game reads it as "a menu is open"
/// or "in game". On Android, Godot drops every mode change once a finger has touched the screen
/// (<c>DisplayServerAndroid._mouse_update_mode</c> returns early unless the last pointer was a
/// mouse), so <c>Input.MouseMode</c> stayed Visible from the menus and the touch controls never
/// armed (#1020). Rule note: docs/notes/core/touch-controls.md.
/// </para>
/// </summary>
public static class MouseCapture
{
    /// <summary>
    /// A test's run (#1378): its user:// holds test_window.cfg (tools/lib/guard.sh, guard_quiet_godot), the
    /// settings override that also keeps its window from taking focus and silent. The game's mode is kept
    /// as it asked, so "in game" reads as in game (the flag's ghost, the build tool), but the real pointer
    /// is never captured, whatever the probe; a player's user:// never has the file.
    /// </summary>
    public static readonly bool TestWindow = ProjectSettings.GetSetting("unitsport/test_window", false).AsBool();

    public static bool Disabled { get; set; } = CmdArgs.Has("--nocapture");

    private static readonly bool Android = OS.GetName() == "Android";
    private static Input.MouseModeEnum _mode = Input.MouseModeEnum.Visible;

    /// <summary>The pointer mode the game asked for: Android's own answer is not to be trusted, a test window's never applied.</summary>
    public static Input.MouseModeEnum Mode
    {
        get => Android || TestWindow ? _mode : Input.MouseMode;
        set
        {
            _mode = value;
            if (!TestWindow) Input.MouseMode = value;
        }
    }

    /// <summary>
    /// A test's window lets the real mouse through to whatever is under it (#1378): the game reads itself as
    /// "in game" with the pointer free, so a person's mouse passing over the window turned its camera and
    /// moved the flag probe's aim off the flag. A passthrough region off the window: none of it takes the
    /// mouse, clicks included; a probe's own events (Input.ParseInputEvent) never come from the OS.
    /// </summary>
    public static void PassTestWindowThrough()
    {
        if (!TestWindow || DisplayServer.GetName() == "headless") return;
        DisplayServer.WindowSetMousePassthrough(new[] { new Vector2(-10, -10), new Vector2(-9, -10), new Vector2(-10, -9) });
    }

    public static void Capture()
    {
        if (Disabled) return;
        // a phone, or a desktop --mobile run that keeps the pointer to play the finger
        // (TouchControls, #63): confined still says "in game", as captured does
        Mode = Platform.IsTouch ? Input.MouseModeEnum.Confined : Input.MouseModeEnum.Captured;
    }
}
