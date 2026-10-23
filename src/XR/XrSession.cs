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
    /// The right hand as a stick (#437), x right, y down like a stick's: how far it moved, across
    /// the view, since <see cref="ZeroHandAim"/>, 0.15 m being full. For radial menus aimed by hand.
    /// </summary>
    public static Vector2 HandAim { get; internal set; }

    internal static bool HandAimZeroAsked;

    /// <summary>
    /// The hand that holds the item, in the world: the right controller's aim pose, -Z along the
    /// pointer. Null outside VR and when that hand is not tracked. A gun points and fires along it.
    /// </summary>
    public static Transform3D? ItemHand => Active ? Rig?.AimHand : null;

    /// <summary>Makes where the right hand is now the centre of <see cref="HandAim"/>.</summary>
    public static void ZeroHandAim() => HandAimZeroAsked = true;

    /// <summary>
    /// Makes this client a VR client if OpenXR came up. Returns false (and changes nothing) when
    /// it did not; prints how to start it when <c>--vr</c> asked for it.
    /// </summary>
    public static bool TryStart(Node root)
    {
        bool asked = CmdArgs.Has("--vr");
        // the whole VR path without a headset, drawn on the monitor: the rig follows the game's
        // camera from an untracked head, the UI goes on the panel, the pad bridge runs idle
        Simulated = CmdArgs.Has("--xrsim");
        if (Simulated)
        {
            GD.Print("[xr] simulated: no headset, the rig's view on the monitor");
            // a load test is paced like the headset: no desktop vsync holding it to the monitor
            if (LoadTest) DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            return Begin(root);
        }

        var xr = XRServer.FindInterface("OpenXR");
        if (xr == null || !xr.IsInitialized())
        {
            if (Platform.IsStandaloneVr)
                GD.PushError("[xr] OpenXR did not start on this headset: the APK must be the Quest export (tools/quest.sh)");
            else if (asked)
                GD.PrintErr("[xr] OpenXR is not running. Start the headset runtime (Quest Link) and launch with "
                    + "`--xr-mode on --rendering-driver vulkan`, see docs/notes/xr/setup.md");
            return false;
        }

        // The headset gets its own viewport (XrRig); the window stays the game's, for the monitor
        // view (XrMonitor). The headset paces the frames: a desktop vsync on top only adds a wait.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        // Meta's fixed foveation on a standalone headset (#1112): the Mobile renderer draws it with
        // a fragment density map; over Link the headset viewport's VRS does it (XrRig.ApplyQuality)
        if (Platform.IsStandaloneVr && xr is OpenXRInterface fov && fov.IsFoveationSupported())
        {
            fov.FoveationLevel = CmdArgs.Int("--foveation") ?? (GameSettings.Current.VrFoveation ? 3 : 0);
            fov.FoveationDynamic = true;
            GD.Print($"[xr] foveation level {fov.FoveationLevel}, dynamic");
        }
        // A standalone headset's clocks (#1112): left to the runtime they fell to levels 2 and 3 whenever the GPU
        // waited on the main thread, slowing that thread, the one holding the frame back. Sustained high keeps
        // them up; "--perflevel low|high|boost" (sustained low, sustained high, boost) to measure.
        if (Platform.IsStandaloneVr && xr is OpenXRInterface perf)
        {
            var level = CmdArgs.Value("--perflevel") switch
            {
                "low" => OpenXRInterface.PerfSettingsLevel.SustainedLow,
                "boost" => OpenXRInterface.PerfSettingsLevel.Boost,
                _ => OpenXRInterface.PerfSettingsLevel.SustainedHigh,
            };
            void Apply()
            {
                perf.SetCpuLevel(level);
                perf.SetGpuLevel(level);
                GD.Print($"[xr] performance level {level}");
            }
            Apply();
            // and again once the session runs, as the refresh rate: one asked before may be dropped
            perf.SessionBegun += Apply;
        }
        // "--vrmult 0.85": the eye buffers' size against the runtime's recommendation (a frame-time check)
        if (xr is OpenXRInterface mult && CmdArgs.Double("--vrmult") is double m)
        {
            mult.RenderTargetSizeMultiplier = m;
            GD.Print($"[xr] render target x{m:F2}");
        }
        GD.Print($"[xr] OpenXR on, {xr.GetName()}");
        return Begin(root);
    }

    /// <summary>
    /// Drawn only in the headset: the comfort vignette, the UI panel and its pointer. The monitor's
    /// cameras leave it out (a clip-space vignette would cover any camera that saw it).
    /// </summary>
    public const uint HeadsetOnlyLayer = 1u << 14;

    /// <summary>
    /// The player's own figure while seen from its eye (<c>Avatar.OwnBody.Layer</c>, #833): on foot,
    /// a cockpit's head. The eye's cameras leave it out (the headset, the desktop camera, the
    /// monitor's first-person view, door portals); mirrors and the monitor's third-person camera keep it.
    /// </summary>
    public const uint SpectatorOnlyLayer = 1u << 15;

    /// <summary>True with <c>--xrsim</c>: VR mode with no headset, for checks on a desktop.</summary>
    public static bool Simulated { get; private set; }

    /// <summary>
    /// <c>--xrsim --xrload</c>: the simulated headset is drawn at a Quest 2's pixel count and the
    /// monitor as in a real headset session, and the rig reports frame and GPU times, for
    /// frame-time checks without a headset (docs/notes/xr/air-link.md).
    /// </summary>
    public static bool LoadTest => Simulated && CmdArgs.Has("--xrload");

    /// <summary>
    /// <c>--xrstats</c>, or a load test: the rig reports the frame and the headset's GPU time every 5 s,
    /// and a real headset's eye size (#1036).
    /// </summary>
    public static bool Stats => LoadTest || CmdArgs.Has("--xrstats");

    /// <summary>
    /// The headset's refresh rate from <see cref="GameSettings.VrRefreshHz"/> (the nearest the
    /// runtime offers at or under it, else its lowest), and physics ticking at that rate. Physics
    /// at 60 under a 72 Hz headset moved the body, and the eye with it, on five frames in six (two
    /// in three at 90): the whole view juddered while moving, at full frame rate (#1036). Called at
    /// start and on a settings change; <c>--xrsim</c> takes the setting as the headset's rate.
    /// </summary>
    public static void ApplyRefresh()
    {
        if (!Active) return;
        float want = GameSettings.Current.VrRefreshHz, hz = want;
        if (!Simulated && XRServer.FindInterface("OpenXR") is OpenXRInterface openxr)
        {
            if (!_watchingRate)
            {
                // the runtime may refuse or delay a rate (the Link app's own choice): follow what it runs at
                _watchingRate = true;
                openxr.RefreshRateChanged += r => TickAt((float)r);
                // a rate asked before the session runs is dropped: a Quest then runs its system rate
                // (90 there, #1112), so ask again once it has begun
                openxr.SessionBegun += ApplyRefresh;
            }
            hz = VrRefresh.Pick(openxr.GetAvailableDisplayRefreshRates().Select(r => r.AsSingle()), want)
                ?? openxr.DisplayRefreshRate;
            if (hz > 0f && Math.Abs(openxr.DisplayRefreshRate - hz) > 0.5f) openxr.DisplayRefreshRate = hz;
        }
        TickAt(hz);
    }

    private static bool _watchingRate;

    /// <summary>Whether Application SpaceWarp runs (#1112): the game renders at half the headset's rate.</summary>
    public static bool SpaceWarp { get; private set; }

    private static readonly StringName SpaceWarpName = "OpenXRFbSpaceWarpExtension", SetSpaceWarp = "set_space_warp_enabled",
        SpaceWarpOn = "is_enabled", SkipSpaceWarp = "skip_space_warp_frame";

    /// <summary>
    /// Meta's Application SpaceWarp on a standalone headset, as <see cref="GameSettings.VrSpaceWarp"/> says (#1112): the
    /// vendors plugin's extension, on in the Quest build's project settings (tools/quest.sh), switched here. The game
    /// then renders every other frame with motion vectors and the runtime makes the one between.
    /// </summary>
    public static void ApplySpaceWarp()
    {
        if (!Active || Simulated || !Engine.HasSingleton(SpaceWarpName)) return;
        var ext = Engine.GetSingleton(SpaceWarpName);
        // only asked for on the command line: a settings file from the builds that offered it may still say on
        bool want = Platform.IsStandaloneVr && CmdArgs.Has("--spacewarp") && GameSettings.Current.VrSpaceWarp;
        ext.Call(SetSpaceWarp, want);
        bool on = ext.Call(SpaceWarpOn).AsBool();
        if (on != SpaceWarp) GD.Print($"[xr] SpaceWarp {(on ? "on" : "off")}");
        SpaceWarp = on;
    }

    /// <summary>
    /// No frame made between this one and the last (#1112): the view jumped (a teleport, a snap turn, the world's
    /// origin moved), and a frame made from motion across the jump would smear.
    /// </summary>
    public static void SkipSpaceWarpFrame()
    {
        if (SpaceWarp) Engine.GetSingleton(SpaceWarpName).Call(SkipSpaceWarp);
    }

    /// <summary>Physics at the headset's <paramref name="hz"/>: one tick a frame.</summary>
    private static void TickAt(float hz)
    {
        int ticks = Mathf.RoundToInt(hz);
        if (ticks <= 0 || ticks == Engine.PhysicsTicksPerSecond) return;
        GameClock.SetTickRate(ticks);
        GD.Print($"[xr] headset at {hz:F0} Hz, physics at {ticks} ticks a second");
    }

    private static bool Begin(Node root)
    {
        Active = true;
        ApplyRefresh();
        ApplySpaceWarp();
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
        // Vulkan is the well-trodden OpenXR path on Windows (the project default is d3d12). The method
        // has to be named too: a driver alone on the command line starts Godot on Forward+, not the
        // project's Mobile, which cost a Quest 2 most of its frame (#1036); VR has no Forward+ effects anyway
        args.AddRange(vr ? new[] { "--xr-mode", "on", "--rendering-driver", "vulkan", "--rendering-method", "mobile" } : new[] { "--xr-mode", "off" });
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
