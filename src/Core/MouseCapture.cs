using Godot;

namespace UnitSport.Core;

/// <summary>
/// The one way the game grabs the mouse. <c>--nocapture</c> turns it off, and so does any probe or
/// tool run (<c>ClientWorld</c> sets <see cref="Disabled"/>): an automated check running in a
/// window must not take the pointer from whoever is using the machine meanwhile.
/// </summary>
public static class MouseCapture
{
    public static bool Disabled { get; set; } = CmdArgs.Has("--nocapture");

    public static void Capture()
    {
        if (Disabled) return;
        // a desktop --mobile run keeps the pointer, which plays the finger (TouchControls, #63);
        // confined still says "in game", as captured does
        Input.MouseMode = Platform.IsMobile && !OS.HasFeature("mobile") ? Input.MouseModeEnum.Confined : Input.MouseModeEnum.Captured;
    }
}
