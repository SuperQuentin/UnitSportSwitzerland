using Godot;

namespace UnitSport.Core;

/// <summary>
/// The one way the game grabs the mouse. <c>--nocapture</c> turns it off, and so does any probe or
/// tool run (<c>ClientWorld</c> sets <see cref="Disabled"/>): an automated check running in a
/// window must not take the pointer from whoever is using the machine meanwhile.
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
    public static bool Disabled { get; set; } = CmdArgs.Has("--nocapture");

    private static readonly bool Android = OS.GetName() == "Android";
    private static Input.MouseModeEnum _mode = Input.MouseModeEnum.Visible;

    /// <summary>The pointer mode the game asked for: Android's own answer is not to be trusted.</summary>
    public static Input.MouseModeEnum Mode
    {
        get => Android ? _mode : Input.MouseMode;
        set
        {
            _mode = value;
            Input.MouseMode = value;
        }
    }

    public static void Capture()
    {
        if (Disabled) return;
        // a phone, or a desktop --mobile run that keeps the pointer to play the finger
        // (TouchControls, #63): confined still says "in game", as captured does
        Mode = Platform.IsMobile ? Input.MouseModeEnum.Confined : Input.MouseModeEnum.Captured;
    }
}
