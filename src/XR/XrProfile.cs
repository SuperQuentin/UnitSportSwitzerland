using Godot;
using UnitSport.Core;

namespace UnitSport.XR;

/// <summary>
/// Which VR controllers are in the player's hands (#435), from the OpenXR interaction profile the
/// runtime bound, so prompts can name their buttons. <c>--xrprofile index|vive|wmr|quest</c> forces
/// one, for checks without that hardware.
/// </summary>
public static class XrProfile
{
    private static readonly StringName[] Hands = { "left_hand", "right_hand" };

    public static XrController Controller { get; private set; } = XrController.Quest;

    /// <summary>The interaction profile path last seen, for the log.</summary>
    public static string Path { get; private set; } = "";

    private static bool _forced;

    /// <summary>Starts following the runtime's profile; the rig calls it once it is in the tree.</summary>
    internal static void Watch()
    {
        if (CmdArgs.Value("--xrprofile") is { } forced)
        {
            _forced = true;
            Set(XrControlNames.FromPath(forced), forced);
            return;
        }
        XRServer.Singleton.TrackerAdded += OnTrackerAdded;
        foreach (var hand in Hands) Hook(XRServer.GetTracker(hand));
    }

    internal static void Unwatch()
    {
        if (_forced) return;
        XRServer.Singleton.TrackerAdded -= OnTrackerAdded;
        foreach (var hand in Hands)
            if (XRServer.GetTracker(hand) is XRPositionalTracker t) t.ProfileChanged -= OnProfileChanged;
    }

    private static void OnTrackerAdded(StringName name, long type)
    {
        if (Array.IndexOf(Hands, name) >= 0) Hook(XRServer.GetTracker(name));
    }

    private static void Hook(XRTracker? tracker)
    {
        if (tracker is not XRPositionalTracker t) return;
        t.ProfileChanged -= OnProfileChanged;
        t.ProfileChanged += OnProfileChanged;
        OnProfileChanged(t.Profile);
    }

    private static void OnProfileChanged(string profile)
    {
        // an empty profile while a hand is not bound yet says nothing about the hardware
        if (!string.IsNullOrEmpty(profile)) Set(XrControlNames.FromPath(profile), profile);
    }

    private static void Set(XrController controller, string path)
    {
        bool changed = controller != Controller;
        Controller = controller;
        if (path != Path) GD.Print($"[xr] controllers: {path} → {controller}");
        Path = path;
        if (changed) PlayerInput.HintsChanged();
    }

    /// <summary>What <paramref name="control"/> is called on the current controller; null when it has none.</summary>
    /// <remarks>Left-handed (#439), the controllers swap, so a control is named by the hand it is really on.</remarks>
    public static string? Name(XrControl control) =>
        XrControlNames.Name(GameSettings.Current.VrLeftHanded ? XrControlNames.Mirror(control) : control, Controller);
}
