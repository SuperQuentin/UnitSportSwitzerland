using Godot;
using UnitSport.Core;

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
    /// The steering wheel's angle while a VR hand holds it (#243), radians, + right, as a real
    /// wheel's <see cref="Player.RideInput.WheelAngle"/>. NaN with no hand on it: the sticks steer.
    /// </summary>
    public static float WheelAngle { get; internal set; } = float.NaN;

    /// <summary>
    /// Makes this client a VR client if OpenXR came up. Returns false (and changes nothing) when
    /// it did not; prints how to start it when <c>--vr</c> asked for it.
    /// </summary>
    public static bool TryStart(Node root)
    {
        var args = CmdArgs.All;
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

        // The headset gets its own viewport (XrRig); the window stays the game's, for the monitor
        // view (XrMonitor). The headset paces the frames: a desktop vsync on top only adds a wait.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

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
            // (OpenXR's own foveation is for the Compatibility renderer only: the headset viewport
            // gets variable rate shading instead, XrRig.ApplyQuality)
            GD.Print($"[xr] OpenXR on, {xr.GetName()} at {openxr.DisplayRefreshRate:F0} Hz");
        }

        return Begin(root);
    }

    /// <summary>
    /// Drawn only in the headset: the comfort vignette, the UI panel and its pointer. The monitor's
    /// cameras leave it out (a clip-space vignette would cover any camera that saw it).
    /// </summary>
    public const uint HeadsetOnlyLayer = 1u << 14;

    /// <summary>
    /// Drawn only for the monitor's third-person camera: the VR player's own body on foot, which
    /// first person otherwise does not draw at all. The headset and first-person views leave it out.
    /// </summary>
    public const uint SpectatorOnlyLayer = 1u << 15;

    /// <summary>True with <c>--xrsim</c>: VR mode with no headset, for checks on a desktop.</summary>
    public static bool Simulated { get; private set; }

    private static bool Begin(Node root)
    {
        Active = true;
        // the PS1 vertex snap and dither off (common/retro.gdshaderinc): the window's view too,
        // which in VR is a spectator's
        RenderingServer.GlobalShaderParameterSet("xr_smooth", true);
        Rig = new XrRig { Name = "XrRig" };
        // deferred: called from a node's _Ready, while the root is still adding its children
        root.CallDeferred(Node.MethodName.AddChild, Rig);
        return true;
    }

    /// <summary>
    /// Starts the game again, with OpenXR (<paramref name="vr"/>) or without: OpenXR can only
    /// come up with the engine. The user arguments carry over (minus <c>--vr</c> / <c>--xrsim</c>);
    /// the caller quits this process when it returns true. <paramref name="asked"/>: the player
    /// just chose VR, so a headset that does not answer is worth saying (<c>--vr-asked</c>); a
    /// launch that only follows the saved setting falls back to the screen without a word.
    /// </summary>
    public static bool Relaunch(bool vr, bool asked = false)
    {
        var args = new List<string>();
        // Vulkan is the well-trodden OpenXR path on Windows (the project default is d3d12)
        args.AddRange(vr ? new[] { "--xr-mode", "on", "--rendering-driver", "vulkan" } : new[] { "--xr-mode", "off" });
        // run from the editor binary (`godot --path .`): it has to be told the project again
        if (OS.HasFeature("editor")) args.AddRange(new[] { "--path", ProjectSettings.GlobalizePath("res://") });
        args.Add("--");
        args.AddRange(CmdArgs.All.Where(a => a is not "--vr" and not "--xrsim" and not AskedFlag));
        if (vr) args.Add("--vr");
        if (vr && asked) args.Add(AskedFlag);

        int pid = OS.CreateProcess(OS.GetExecutablePath(), args.ToArray());
        GD.Print($"[xr] relaunching {(vr ? "in VR" : "on the screen")}: pid {pid}");
        if (pid <= 0) GD.PushError("[xr] could not start the game again");
        return pid > 0;
    }

    /// <summary>On a relaunch into VR the player chose just now (<see cref="Relaunch"/>).</summary>
    public const string AskedFlag = "--vr-asked";

    /// <summary>Rumble both hands; <see cref="Core.PlayerInput.Rumble"/> routes here in VR.</summary>
    public static void Rumble(float weak, float strong, float seconds) =>
        Rig?.Rumble(Mathf.Max(weak, strong), seconds, both: true);
}
