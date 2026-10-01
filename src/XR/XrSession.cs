using Godot;

namespace UnitSport.XR;

/// <summary>
/// The VR mode (#186): OpenXR, aimed at a Quest 2 over Link / Air Link.
///
/// <para>
/// Opt-in at launch. Godot's own <c>--xr-mode on</c> switch makes the engine bring OpenXR up;
/// when it did, <see cref="TryStart"/> turns this client into a VR client. The project setting
/// stays off, so a flat client, the dedicated server and the probes never touch the runtime.
/// </para>
///
/// <para>
/// Nothing in gameplay learns a second input path or a second camera. The controllers are
/// replayed as a virtual gamepad (<see cref="XrPad"/>), so every action <see cref="Core.PlayerInput"/>
/// already knows works; the headset follows whichever camera the game made current
/// (<see cref="XrRig"/>); the screen-space UI is drawn on a panel in the world (<see cref="XrUi"/>).
/// What gameplay does read from here is small and named: <see cref="Active"/>, and the ski body
/// input below.
/// </para>
/// </summary>
public static class XrSession
{
    /// <summary>True once the headset is the view. Never true on a server or a flat client.</summary>
    public static bool Active { get; private set; }

    /// <summary>The rig, while <see cref="Active"/>.</summary>
    public static XrRig? Rig { get; private set; }

    /// <summary>
    /// The camera the game wants to look through (a player's, the spectator's, a GPX shot). The
    /// headset's own camera is the one current on the viewport, so code that asked "which camera
    /// is current" to find the viewer asks this instead.
    /// </summary>
    public static Camera3D? Anchor => Rig?.Anchor;

    // --- skiing by body (#186): written by the rig every frame, zero when not on skis ---
    /// <summary>Lean left/right with the head, −1..1, added to the stick's steer.</summary>
    public static float SkiSteer { get; internal set; }
    /// <summary>Pole push, 0..1: a stab and pull back with a hand low down. Taken as throttle.</summary>
    public static float SkiPole { get; internal set; }
    /// <summary>Crouched well below the calibrated head height: the tuck.</summary>
    public static bool SkiTuck { get; internal set; }

    /// <summary>
    /// Makes this client a VR client if OpenXR came up. Returns false (and changes nothing) when
    /// it did not; prints how to start it when <c>--vr</c> asked for it.
    /// </summary>
    public static bool TryStart(Node root)
    {
        var args = OS.GetCmdlineUserArgs();
        bool asked = System.Array.IndexOf(args, "--vr") >= 0;
        // the whole VR path without a headset, drawn on the monitor: the rig follows the game's
        // camera from an untracked head, the UI goes on the panel, the pad bridge runs idle
        Simulated = System.Array.IndexOf(args, "--xrsim") >= 0;
        if (Simulated)
        {
            GD.Print("[xr] simulated: no headset, the rig's view on the monitor");
            return Begin(root);
        }

        var xr = XRServer.FindInterface("OpenXR");
        if (xr == null || !xr.IsInitialized())
        {
            if (asked)
                GD.PrintErr("[xr] OpenXR is not running. Start the headset runtime (Quest Link) and launch with "
                    + "`--xr-mode on --rendering-driver vulkan`, see docs/notes/xr/setup.md");
            return false;
        }

        var viewport = root.GetViewport();
        viewport.UseXR = true;
        // the headset paces the frames; a desktop vsync on top of it only adds a wait
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        // render scale is a flat-screen setting: the runtime picks the eye resolution
        viewport.Scaling3DScale = 1f;

        if (xr is OpenXRInterface openxr)
        {
            // 90 Hz when the runtime offers it (Quest 2 over Link does), else its best under that
            float best = 0f;
            foreach (var r in openxr.GetAvailableDisplayRefreshRates())
            {
                float hz = r.AsSingle();
                if (hz <= 90.5f && hz > best) best = hz;
            }
            if (best > 0f) openxr.DisplayRefreshRate = best;
            // only some runtimes honour it (standalone Quest); harmless over Link
            openxr.FoveationLevel = 2;
            openxr.FoveationDynamic = true;
            GD.Print($"[xr] OpenXR on, {xr.GetName()} at {openxr.DisplayRefreshRate:F0} Hz");
        }

        return Begin(root);
    }

    /// <summary>True with <c>--xrsim</c>: VR mode with no headset, for checks on a desktop.</summary>
    public static bool Simulated { get; private set; }

    private static bool Begin(Node root)
    {
        Active = true;
        Rig = new XrRig { Name = "XrRig" };
        // deferred: called from a node's _Ready, while the root is still adding its children
        root.CallDeferred(Node.MethodName.AddChild, Rig);
        return true;
    }

    /// <summary>
    /// Starts the game again, with OpenXR (<paramref name="vr"/>) or without: OpenXR can only
    /// come up with the engine. The user arguments carry over (minus <c>--vr</c> / <c>--xrsim</c>);
    /// the caller quits this process when it returns true.
    /// </summary>
    public static bool Relaunch(bool vr)
    {
        var args = new List<string>();
        // Vulkan is the well-trodden OpenXR path on Windows (the project default is d3d12)
        args.AddRange(vr ? new[] { "--xr-mode", "on", "--rendering-driver", "vulkan" } : new[] { "--xr-mode", "off" });
        // run from the editor binary (`godot --path .`): it has to be told the project again
        if (OS.HasFeature("editor")) args.AddRange(new[] { "--path", ProjectSettings.GlobalizePath("res://") });
        args.Add("--");
        args.AddRange(OS.GetCmdlineUserArgs().Where(a => a is not "--vr" and not "--xrsim"));
        if (vr) args.Add("--vr");

        int pid = OS.CreateProcess(OS.GetExecutablePath(), args.ToArray());
        GD.Print($"[xr] relaunching {(vr ? "in VR" : "on the screen")}: pid {pid}");
        if (pid <= 0) GD.PushError("[xr] could not start the game again");
        return pid > 0;
    }

    /// <summary>Rumble both hands; <see cref="Core.PlayerInput.Rumble"/> routes here in VR.</summary>
    public static void Rumble(float weak, float strong, float seconds) =>
        Rig?.Rumble(Mathf.Max(weak, strong), seconds, both: true);
}
