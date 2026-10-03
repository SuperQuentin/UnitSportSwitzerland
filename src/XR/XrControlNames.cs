namespace UnitSport.XR;

// Plain C#, no Godot: the names are unit tested (tests/UnitSportSwitzerland.Tests/XrControlNamesTests.cs).

/// <summary>A physical input on the VR controllers, as <c>XrPad</c> uses it.</summary>
public enum XrControl
{
    LeftStick, RightStick, LeftStickClick, RightStickClick, RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
    LeftTrigger, RightTrigger, LeftGrip, RightGrip,
    A, B, X, Y, Menu, MenuHold,
}

/// <summary>The controller families whose inputs are named differently.</summary>
public enum XrController
{
    /// <summary>Meta Touch (Quest), and the default: Pico, HP Reverb G2 and unknown controllers share its layout.</summary>
    Quest,
    /// <summary>Valve Index: A and B on both hands, no X and Y.</summary>
    Index,
    /// <summary>HTC Vive wands: a trackpad instead of a stick, no face buttons.</summary>
    Vive,
    /// <summary>Windows Mixed Reality: a stick and a touchpad, no face buttons.</summary>
    Wmr,
}

/// <summary>What the VR controls are called on each controller family (#435), and which family a profile is.</summary>
public static class XrControlNames
{
    /// <summary>The family of an OpenXR interaction profile path (or a bare <c>--xrprofile</c> name).</summary>
    public static XrController FromPath(string path)
    {
        string p = path.ToLowerInvariant();
        // the Vive Cosmos and Focus 3 controllers have sticks and A B X Y: Quest names suit them
        if (p.Contains("valve/index_controller") || p == "index") return XrController.Index;
        if (p.Contains("htc/vive_controller") || p == "vive") return XrController.Vive;
        if (p.Contains("microsoft/motion_controller") || p == "wmr") return XrController.Wmr;
        return XrController.Quest;
    }

    public static string? Name(XrControl control, XrController controller) => control switch
    {
        XrControl.LeftTrigger => "L trigger",
        XrControl.RightTrigger => "R trigger",
        XrControl.LeftGrip => "L grip",
        XrControl.RightGrip => "R grip",
        XrControl.Menu => "Menu",
        XrControl.MenuHold => "Hold Menu",
        _ => controller switch
        {
            XrController.Index => control switch
            {
                XrControl.A => "R A",
                XrControl.B => "R B",
                XrControl.X => "L A",
                XrControl.Y => "L B",
                _ => Stick(control, "stick"),
            },
            // no face buttons on these: an action bound only there has no VR control to name
            XrController.Vive => IsFace(control) ? null : Stick(control, "trackpad"),
            XrController.Wmr => IsFace(control) ? null : Stick(control, "stick"),
            _ => control switch
            {
                XrControl.A => "A",
                XrControl.B => "B",
                XrControl.X => "X",
                XrControl.Y => "Y",
                _ => Stick(control, "stick"),
            },
        },
    };

    private static bool IsFace(XrControl control) => control is XrControl.A or XrControl.B or XrControl.X or XrControl.Y;

    private static string? Stick(XrControl control, string stick) => control switch
    {
        XrControl.LeftStick => $"L {stick}",
        XrControl.RightStick => $"R {stick}",
        XrControl.LeftStickClick => $"L {stick} click",
        XrControl.RightStickClick => $"R {stick} click",
        XrControl.RightStickUp => $"R {stick} ↑",
        XrControl.RightStickDown => $"R {stick} ↓",
        XrControl.RightStickLeft => $"R {stick} ←",
        XrControl.RightStickRight => $"R {stick} →",
        _ => null,
    };
}
