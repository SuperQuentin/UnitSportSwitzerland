using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// Settings, in tabs: Video, Audio, Gameplay, Vehicles, Controls, Wheel, World, Performance, About. Every
/// control writes straight into <see cref="GameSettings.Current"/> and commits, so the world
/// re-applies itself live and the file is saved — there is no Apply button to forget. LB / RB
/// (or Q / E) change tab. One instance serves the title screen and the in-game pause menu.
/// </summary>
public partial class SettingsScreen : Screen
{
    private readonly List<Button> _tabs = new();
    private readonly List<Control> _pages = new();
    private int _current;

    public static SettingsScreen Create() => new() { Name = "Settings" };

    public override void _Ready()
    {
        var (body, _) = Framed("Settings", "Changes apply at once and are saved", new Vector2(960, 590));

        var tabRow = UiKit.HBox(4);
        body.AddChild(tabRow);
        var group = new ButtonGroup();
        var pages = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        body.AddChild(pages);

        var names = new List<string>();
        void Tab(string name, Action<VBoxContainer> fill)
        {
            names.Add(name);
            var b = new Button { Text = name, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 36) };
            var normal = UiTheme.Flat(new Color(0, 0, 0, 0), 6, 14, 6);
            var on = UiTheme.Flat(new Color(UiTheme.Amber, 0.12f), 6, 14, 6);
            on.BorderColor = UiTheme.Amber; on.BorderWidthBottom = 2;
            b.AddThemeStyleboxOverride("normal", normal);
            b.AddThemeStyleboxOverride("pressed", on);
            b.AddThemeStyleboxOverride("hover_pressed", on);
            b.AddThemeColorOverride("font_color", UiTheme.TextDim);
            b.AddThemeColorOverride("font_pressed_color", UiTheme.Amber);
            b.AddThemeColorOverride("font_hover_pressed_color", UiTheme.Amber);
            int index = _tabs.Count;
            b.Toggled += pressed => { if (pressed) Show(index); };
            tabRow.AddChild(b);
            _tabs.Add(b);

            var (scroll, rows) = UiKit.ScrollPage();
            scroll.SetAnchorsPreset(LayoutPreset.FullRect);
            scroll.Visible = false;
            pages.AddChild(scroll);
            _pages.Add(scroll);
            fill(rows);
        }

        var s = GameSettings.Current;

        Tab("Video", rows =>
        {
            if (!Platform.IsMobile) // a phone's window is the screen (#63)
            {
                UiKit.OptionRow(rows, "Window", new[] { "Windowed", "Borderless fullscreen", "Fullscreen" }, (int)s.WindowMode,
                    i => GameSettings.Current.WindowMode = (WindowMode)i, "F11 or Alt+Enter toggles it anywhere");
                SizeRow(rows, "Window size", WindowSizes(), s.WindowWidth, s.WindowHeight, "Keep current",
                    (w, h) => (GameSettings.Current.WindowWidth, GameSettings.Current.WindowHeight) = (w, h));
            }
            ScaleRow(rows, "3D resolution", s.RenderScale, v => GameSettings.Current.RenderScale = v);
            // Realistic+ needs Forward+ through a relaunch, which a phone cannot do (#63)
            var styles = Styles.StyleKit.MenuStyles.Where(v => Platform.CanSpawnProcesses || !Styles.StyleKit.NeedsForwardPlus(v)).ToArray();
            var styleOption = UiKit.OptionRow(rows, "Visual style", styles.Select(Styles.StyleKit.Label).ToArray(),
                Math.Max(0, Array.IndexOf(styles, s.VisualStyle)),
                i =>
                {
                    Styles.StyleKit.ChooseSetting(styles[i]);
                    if (Styles.StyleKit.NeedsForwardPlus(styles[i]) && !Styles.StyleKit.OnForwardPlus) AskForwardPlus(this, Shell);
                }, "Switches live; how the world looks, never what it does");
            for (int i = 0; i < styles.Length; i++)
                if (Styles.StyleKit.InDevelopment(styles[i]))
                {
                    styleOption.SetItemText(i, $"{Styles.StyleKit.Label(styles[i])} (in development)");
                    styleOption.SetItemDisabled(i, true);
                }
            UiKit.ToggleRow(rows, "VSync", s.VSync, on => GameSettings.Current.VSync = on);
            UiKit.ToggleRow(rows, "Distance fog", s.Fog, on => GameSettings.Current.Fog = on, "Off by default: the far horizon is the point");
            UiKit.ToggleRow(rows, "Speed lines", s.SpeedLines, on => GameSettings.Current.SpeedLines = on, "Streaks at the screen edge at speed");
            if (Platform.CanSpawnProcesses) // VR is a relaunch with OpenXR (#63)
            {
                rows.AddChild(UiKit.Spacer(6));
                rows.AddChild(UiKit.Section("Virtual reality"));
                VrRow(rows);
            }
        });

        Tab("Audio", rows =>
        {
            UiKit.SliderRow(rows, "Master volume", 0, 1, 0.05, s.MasterVolume,
                v => GameSettings.Current.MasterVolume = (float)v, Percent);
            UiKit.SliderRow(rows, "Sound effects", 0, 1, 0.05, s.SfxVolume,
                v => GameSettings.Current.SfxVolume = (float)v, Percent);
            UiKit.SliderRow(rows, "Ambience", 0, 1, 0.05, s.AmbienceVolume,
                v => GameSettings.Current.AmbienceVolume = (float)v, Percent);
            UiKit.SliderRow(rows, "Music", 0, 1, 0.05, s.MusicVolume,
                v => GameSettings.Current.MusicVolume = (float)v, Percent);
            UiKit.OptionRow(rows, "Engine voice", new[] { "Realistic", "PS1 SPU", "NES 2A03", "C64 SID", "Genesis FM" }, (int)s.EngineVoice,
                i => GameSettings.Current.EngineVoice = (UnitSport.Audio.EngineVoice)i, "Which sound chip the engines are rendered as");
        });

        Tab("Gameplay", rows =>
        {
            UiKit.OptionRow(rows, "Movement", new[] { "Game (arcade)", "Simulation (real physics)" }, (int)s.RideProfile,
                i => GameSettings.Current.RideProfile = (RideProfile)i);
            UiKit.ToggleRow(rows, "Third-person view", s.ThirdPerson, on => GameSettings.Current.ThirdPerson = on,
                "Over the shoulder on foot, chase view mounted (V in game)");
            UiKit.SliderRow(rows, "Camera shake", 0, 1, 0.05, s.ScreenShake,
                v => GameSettings.Current.ScreenShake = (float)v, Percent);
            UiKit.ToggleRow(rows, "Pigeon flies tail first", s.TailFirstPigeon, on => GameSettings.Current.TailFirstPigeon = on,
                "The backwards bird of old, kept as an option; drawing only, every pigeon on your screen");
            UiKit.ToggleRow(rows, "Find servers on your network", s.LanDiscovery, on => GameSettings.Current.LanDiscovery = on,
                "Lists LAN servers on the Multiplayer screen");
            // the first-run tutorial (#517): now if a world is up (it shows when the menus close), else in the next one
            Button replay = null!;
            replay = UiKit.ActionRow(rows, "Tutorial", "Play again", () =>
            {
                GameSettings.Current.TutorialDone = false;
                GameSettings.SaveOnly(nameof(GameSettings.TutorialDone), false);
                // and each ride's mini tutorial again
                GameSettings.Current.VehicleIntrosSeen.Clear();
                GameSettings.SaveOnly(nameof(GameSettings.VehicleIntrosSeen), new System.Text.Json.Nodes.JsonArray());
                if (Shell.InWorld) Shell.World?.StartTutorial();
                replay.Text = Shell.InWorld ? "Playing" : "In the next world";
                replay.Disabled = true;
            }, "Look, walk, travel, the map and the fly camera, then each ride's controls the first time");
            if (Tutorial.Current != null) { replay.Text = "Playing"; replay.Disabled = true; }
        });

        Tab("Vehicles", rows =>
        {
            rows.AddChild(UiKit.Section("Wear"));
            UiKit.ToggleRow(rows, "Tyre wear (cars)", s.TyreWear, on => GameSettings.Current.TyreWear = on);
            UiKit.ToggleRow(rows, "Brake wear and fade (cars)", s.BrakeWear, on => GameSettings.Current.BrakeWear = on);
            UiKit.OptionRow(rows, "Truck gearbox", new[] { "Automatic", "Sequential", "Sequential + clutch", "H-pattern + splitter", "H-pattern (auto splitter)" },
                (int)s.HeavyGearbox, i => GameSettings.Current.HeavyGearbox = (Player.HeavyShift)i);
            UiKit.OptionRow(rows, "Airliner handling", new[] { "Arcade", "Light sim" }, (int)s.Airliner,
                i => GameSettings.Current.Airliner = (Player.AirlinerHandling)i,
                "Arcade: protected, wings level on their own, ready to taxi. Light sim: engine start, autopilot, trim, fuel");
            UiKit.ToggleRow(rows, "Get in buses and ships from outside", s.BoardWalkableFromOutside, on => GameSettings.Current.BoardWalkableFromOutside = on,
                "On: E beside a bus, a coach or the steamer puts you at its wheel. Off: walk aboard and take the wheel inside");
            rows.AddChild(UiKit.Spacer(6));
            rows.AddChild(UiKit.Section("Cockpit"));
            UiKit.ToggleRow(rows, "Show your own body", s.CockpitBody, on => GameSettings.Current.CockpitBody = on, "V cycles it too");
            UiKit.ToggleRow(rows, "Working mirrors", s.CockpitMirrors, on => GameSettings.Current.CockpitMirrors = on);
            UiKit.ToggleRow(rows, "Speed and gear on the HUD too", s.CockpitHud, on => GameSettings.Current.CockpitHud = on);
            UiKit.ToggleRow(rows, "Head moves with g-forces", s.CockpitHeadMotion, on => GameSettings.Current.CockpitHeadMotion = on);
            UiKit.SliderRow(rows, "Field of view", 50, 100, 1, s.CockpitFov,
                v => GameSettings.Current.CockpitFov = (float)v, v => $"{v:F0}°");
            UiKit.SliderRow(rows, "Seat height", -0.1, 0.1, 0.01, s.SeatHeight,
                v => GameSettings.Current.SeatHeight = (float)v, v => $"{v * 100:+0;-0;0} cm");
            UiKit.SliderRow(rows, "Seat forward", -0.15, 0.15, 0.01, s.SeatForward,
                v => GameSettings.Current.SeatForward = (float)v, v => $"{v * 100:+0;-0;0} cm");
        });

        Tab("Controls", rows =>
        {
            UiKit.SliderRow(rows, "Stick look speed", 0.2, 3, 0.1, s.StickSensitivity,
                v => GameSettings.Current.StickSensitivity = (float)v, v => $"{v:F1}x");
            if (Platform.IsMobile)
                UiKit.SliderRow(rows, "Touch look speed", 0.3, 4, 0.1, s.TouchLookSpeed,
                    v => GameSettings.Current.TouchLookSpeed = (float)v, v => $"{v:F1}x");
            UiKit.SliderRow(rows, "Stick deadzone", 0.05, 0.5, 0.01, s.StickDeadzone,
                v => GameSettings.Current.StickDeadzone = (float)v, v => $"{v * 100:F0} %");
            UiKit.ToggleRow(rows, "Invert look Y", s.InvertY, on => GameSettings.Current.InvertY = on);
            UiKit.ToggleRow(rows, "Controller vibration", s.Vibration, on => GameSettings.Current.Vibration = on);
            UiKit.ActionRow(rows, "Every key and button", "Show controls", () => Shell.ShowControls(),
                InputHints.Format("As bound on your keyboard and pad ({help})"));
        });

        // a steering wheel and its pedals (#68): its own tab, it is a page of bindings
        if (!Platform.IsMobile) // SDL, desktop only (#63)
            Tab("Wheel", rows => rows.AddChild(new WheelPanel { Name = "WheelPanel" }));

        Tab("World", rows =>
        {
            rows.AddChild(UiKit.Section("Time and traffic"));
            UiKit.SliderRow(rows, "Start time", 0, 23.5, 0.5, s.StartHour,
                v => GameSettings.Current.StartHour = (float)v, Clock);
            UiKit.SliderRow(rows, "Day length", 0, 120, 1, s.DayLengthMinutes,
                v => GameSettings.Current.DayLengthMinutes = (float)v,
                v => v <= 0 ? "stopped" : $"{v:F0} min / day");
            // online the server's world clock decides (#452); a server hosted from here starts from these
            rows.AddChild(UiKit.Text("Solo, and a server you host. On someone else's server, its clock decides.",
                UiTheme.FontTiny, UiTheme.TextDim, wrap: true));
            UiKit.SliderRow(rows, "Traffic", 0, 150, 5, s.TrafficCars,
                v => GameSettings.Current.TrafficCars = (int)v, v => v <= 0 ? "off" : $"{v:F0} cars");
            UiKit.ToggleRow(rows, "Trains", s.Trains, on => GameSettings.Current.Trains = on);
            UiKit.ToggleRow(rows, "Generated terrain", s.GeneratedFill, on => GameSettings.Current.GeneratedFill = on,
                "Fills ground with no survey data, blended into the real tiles");
            rows.AddChild(UiKit.Spacer(6));
            rows.AddChild(UiKit.Section("Occasions"));
            OccasionRows(rows);
        });

        Tab("Performance", rows =>
        {
            if (Platform.IsMobile)
            {
                var phone = UiKit.Button("Use phone defaults");
                phone.Pressed += () =>
                {
                    GameSettings.Current.UsePhoneDefaults();
                    GameSettings.Current.Commit();
                    Shell.Back();               // reopened, so the rows show the new values
                    Shell.Push(Create());
                };
                rows.AddChild(phone);
            }
            UiKit.SliderRow(rows, "Render distance", GameSettings.MinRings, GameSettings.MaxRings, 1,
                s.RenderDistanceRings, v => GameSettings.Current.RenderDistanceRings = (int)v, RingsText);
            UiKit.OptionRow(rows, "Detail", new[] { "Low", "Medium", "High" }, (int)s.Detail,
                i => GameSettings.Current.Detail = (DetailPreset)i);
            UiKit.SliderRow(rows, "Horizon", 0, GameSettings.MaxHorizonKm, 5, s.HorizonKm,
                v => GameSettings.Current.HorizonKm = (int)v, v => v <= 0 ? "off" : $"{v:F0} km");
            UiKit.SliderRow(rows, "Parallel tile builds", 0, GameSettings.MaxBuildsCap, 1, s.MaxConcurrentBuilds,
                v => GameSettings.Current.MaxConcurrentBuilds = (int)v,
                v => v <= 0 ? "auto" : $"{v:F0}", $"Auto is {System.Environment.ProcessorCount} with local terrain, 6 streaming");
            UiKit.SliderRow(rows, "Mesh commit budget", 1, 16, 1, s.CommitBudgetMs,
                v => GameSettings.Current.CommitBudgetMs = v, v => $"{v:F0} ms / frame");
            UiKit.OptionRow(rows, "Performance overlay (F3)", new[] { "Off", "FPS", "Detailed" }, (int)s.PerfOverlay,
                i => GameSettings.Current.PerfOverlay = (PerfOverlayMode)i);
            UiKit.ActionRow(rows, "Performance logs", "Open folder", PerfRecorder.OpenLogsFolder, "F4 records a session");
        });

        Tab("About", LicenseRows);
        _about = _tabs.Count - 1;

        Show(StartTab(names));
    }

    /// <summary>"--settings wheel" opens on that tab, for screenshotting it.</summary>
    private static int StartTab(List<string> names)
    {
        return CmdArgs.Value("--settings") is { } tab
            ? Math.Max(0, names.FindIndex(n => n.Equals(tab, StringComparison.OrdinalIgnoreCase)))
            : 0;
    }

    private int _about;

    /// <summary>The About tab, the licenses and data sources (<c>--licenses</c>, for screenshotting it).</summary>
    public void ShowLicenses() => Show(_about);

    /// <summary>
    /// Licenses and data sources: every entry of <see cref="Licenses.All"/> (what the game is built
    /// from and credits, the OpenStreetMap overlay's ODbL among them), then what Godot itself
    /// requires, its licence text and the components built into it.
    /// </summary>
    private static void LicenseRows(VBoxContainer rows)
    {
        foreach (var e in Licenses.All)
        {
            rows.AddChild(UiKit.Section(e.Name));
            rows.AddChild(UiKit.Text(e.Attribution, UiTheme.FontSmall, wrap: true));
            rows.AddChild(UiKit.Text($"{e.UsedFor}. Licence: {e.Licence}.", UiTheme.FontTiny, UiTheme.TextDim, wrap: true));
            // LinkButton only takes focus for screen readers by default; the pad needs it too
            var link = new LinkButton { Text = e.Url, Uri = e.Url, Underline = LinkButton.UnderlineMode.OnHover, FocusMode = FocusModeEnum.All };
            link.AddThemeFontSizeOverride("font_size", UiTheme.FontTiny);
            rows.AddChild(link);
        }

        rows.AddChild(UiKit.Section("Godot Engine licence text"));
        rows.AddChild(UiKit.Text(Engine.GetLicenseText(), UiTheme.FontTiny, UiTheme.TextDim, wrap: true));
        rows.AddChild(UiKit.Section("Third-party components in Godot Engine"));
        var parts = new List<string>();
        foreach (var info in Engine.GetCopyrightInfo())
        {
            var licences = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var part in info["parts"].AsGodotArray<Godot.Collections.Dictionary>())
                licences.Add(part["license"].AsString());
            parts.Add($"{info["name"].AsString()} ({string.Join(", ", licences)})");
        }
        rows.AddChild(UiKit.Text(string.Join("; ", parts), UiTheme.FontTiny, UiTheme.TextDim, wrap: true));
        rows.AddChild(UiKit.Text("Full texts: " + string.Join(", ", Engine.GetLicenseInfo().Keys.Select(k => k.AsString())),
            UiTheme.FontTiny, UiTheme.TextDim, wrap: true));
    }

    /// <summary>
    /// One row per occasion. "Off" hides its look (a server can lock it on); loot and the hunt
    /// follow the server whatever this says.
    /// </summary>
    internal static void OccasionRows(Container rows)
    {
        var s = GameSettings.Current;
        var occasions = (IEnumerable<Occasions.OccasionEntry>?)Occasions.OccasionManager.Instance?.Known
            ?? Occasions.OccasionConfig.Load();
        foreach (var entry in occasions)
        {
            string id = entry.Id;
            var current = s.OccasionPreferences.TryGetValue(id, out var p) ? p : Occasions.OccasionPreference.Auto;
            UiKit.OptionRow(rows, Occasions.OccasionRegistry.Get(id).Title,
                new[] { "Auto (calendar)", "Off", "Always" }, (int)current,
                i => GameSettings.Current.OccasionPreferences[id] = (Occasions.OccasionPreference)i);
        }
    }

    private void Show(int index)
    {
        _current = index;
        for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
        if (!_tabs[index].ButtonPressed) _tabs[index].SetPressedNoSignal(true);
    }

    public override void OnShown() => _tabs[_current].CallDeferred(Control.MethodName.GrabFocus);

    /// <summary>LB / RB and Q / E step through the tabs.</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree() || !e.IsPressed() || e.IsEcho() || Modal.Current != null) return;
        int step = e switch
        {
            InputEventJoypadButton { ButtonIndex: JoyButton.LeftShoulder } => -1,
            InputEventJoypadButton { ButtonIndex: JoyButton.RightShoulder } => 1,
            InputEventKey { PhysicalKeycode: Key.Q } when GetViewport().GuiGetFocusOwner() is not LineEdit => -1,
            InputEventKey { PhysicalKeycode: Key.E } when GetViewport().GuiGetFocusOwner() is not LineEdit => 1,
            _ => 0,
        };
        if (step == 0) return;
        int next = (_current + step + _tabs.Count) % _tabs.Count;
        Show(next);
        _tabs[next].GrabFocus();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>VR mode (#186): saved, and applied by starting the game again.</summary>
    private void VrRow(Container rows)
    {
        CheckButton toggle = null!;
        toggle = UiKit.ToggleRow(rows, "VR mode", XR.XrSession.Active, on =>
        {
            if (on == XR.XrSession.Active) return;
            AskVr(this, Shell, on, () => toggle.SetPressedNoSignal(!on));
        }, "Meta Quest over Link (OpenXR). Changing it restarts the game");
        UiKit.OptionRow(rows, "Monitor view in VR", Enum.GetValues<XR.MonitorView>().Select(XR.XrMonitor.Label).ToArray(),
            (int)GameSettings.Current.VrMonitor, i => GameSettings.Current.VrMonitor = (XR.MonitorView)i,
            "What the computer screen shows while you play in the headset (F8 cycles it)");
        // the headset's picture (#244, docs/notes/xr/air-link.md)
        int[] samples = { 0, 2, 4, 8 };
        UiKit.OptionRow(rows, "VR anti-aliasing", new[] { "Off", "MSAA 2x", "MSAA 4x", "MSAA 8x" },
            Math.Max(0, Array.IndexOf(samples, GameSettings.Current.VrMsaa)), i => GameSettings.Current.VrMsaa = samples[i],
            "Steady edges stream cleanly over Air Link");
        float[] scales = { 0.5f, 0.625f, 0.75f, 0.875f, 1f, 1.25f, 1.5f };
        int scale = Array.FindIndex(scales, v => Math.Abs(v - GameSettings.Current.VrRenderScale) < 0.001f);
        UiKit.OptionRow(rows, "VR resolution", scales.Select(v => $"{v * 100:F0} %").ToArray(), scale < 0 ? 4 : scale,
            i => GameSettings.Current.VrRenderScale = scales[i], "Of the eye size the headset asks for. Lower it if the picture stutters");
        UiKit.ToggleRow(rows, "VR foveated rendering", GameSettings.Current.VrFoveation, on => GameSettings.Current.VrFoveation = on,
            "Coarser shading towards the edge of each eye (GPUs with variable rate shading)");
        // comfort (#439, docs/notes/xr/rig.md)
        int[] snaps = { 0, 15, 30, 45 };
        UiKit.OptionRow(rows, "VR turning", new[] { "Smooth", "Snap 15°", "Snap 30°", "Snap 45°" },
            Math.Max(0, Array.IndexOf(snaps, GameSettings.Current.VrSnapDegrees)), i => GameSettings.Current.VrSnapDegrees = snaps[i],
            "The right stick on foot. Snapping is easier on the stomach");
        UiKit.OptionRow(rows, "VR walking", new[] { "Stick", "Teleport" }, GameSettings.Current.VrTeleport ? 1 : 0,
            i => GameSettings.Current.VrTeleport = i == 1, "Teleport: push the stick forward, aim the arc, let go. No faster than walking");
        float[] vignettes = { 0f, 0.5f, 1f };
        int vig = Array.FindIndex(vignettes, v => Math.Abs(v - GameSettings.Current.VrVignette) < 0.01f);
        UiKit.OptionRow(rows, "VR comfort vignette", new[] { "Off", "Light", "Full" }, vig < 0 ? 2 : vig,
            i => GameSettings.Current.VrVignette = vignettes[i], "Narrows the view while the world moves and you do not");
        UiKit.ToggleRow(rows, "VR left-handed", GameSettings.Current.VrLeftHanded, on => GameSettings.Current.VrLeftHanded = on,
            "Swap the hands: the right controller moves, the left one uses and turns");
    }

    /// <summary>
    /// Asks before restarting into VR or out of it, then saves the choice and relaunches. Shared
    /// by the Settings toggle and the title screen's entry.
    /// </summary>
    internal static void AskVr(Control host, GameShell shell, bool on, Action? cancel = null)
    {
        string message = on
            ? "The game restarts with the headset. Put on the Quest with Link connected and Meta set as the OpenXR runtime."
            : "The game restarts on the screen.";
        if (shell.InWorld) message += " You will leave the current world.";
        Modal.Confirm(host, on ? "Play in VR?" : "Leave VR?", message, "Restart", () =>
        {
            GameSettings.Current.VrMode = on;
            GameSettings.Current.Commit();
            if (XR.XrSession.Relaunch(on, asked: true)) shell.Quit();
            else cancel?.Invoke();
        }, cancel);
    }

    /// <summary>
    /// Realistic+ chosen on the Mobile renderer: offers the restart onto Forward+. Declined, it
    /// draws as Realistic− until the next start, which relaunches (<see cref="Styles.RendererRelaunch"/>).
    /// </summary>
    private static void AskForwardPlus(Control host, GameShell shell)
    {
        string message = "Realistic+ needs the Forward+ renderer, which the game picks when it starts. "
            + "Until then it looks like Realistic−.";
        if (shell.InWorld) message += " You will leave the current world.";
        Modal.Confirm(host, "Restart for Realistic+?", message, "Restart", () =>
        {
            GameSettings.Current.Commit();
            if (Styles.RendererRelaunch.Relaunch()) shell.Quit();
        });
    }

    internal static string Percent(double v) => v <= 0 ? "off" : $"{v * 100:F0} %";
    internal static string Clock(double v) => $"{(int)v:00}:{(int)(v % 1 * 60):00}";

    private static string RingsText(double v)
    {
        int n = (int)v;
        return $"{n} km · {(2 * n + 1) * (2 * n + 1)} tiles";
    }

    /// <summary>
    /// 3D render scales, offered as the resolution they produce in the window as it is when the
    /// screen opens (<see cref="DisplaySettings.EffectiveScale"/>): 100% is native, or 1152x648 in
    /// PS1; above 100% supersamples.
    /// </summary>
    private static readonly float[] RenderScales = { 0.25f, 0.35f, 0.5f, 0.625f, 0.75f, 0.875f, 1f, 1.25f, 1.5f, 2f };

    private void ScaleRow(Container into, string name, float current, Action<float> set)
    {
        var scales = RenderScales.ToList();
        int index = scales.FindIndex(v => Math.Abs(v - current) < 0.001f);
        if (index < 0) { scales.Add(current); scales.Sort(); index = scales.IndexOf(current); }
        var window = GetTree().Root.Size;
        var labels = scales.Select(v =>
        {
            float e = DisplaySettings.EffectiveScale(v, window);
            return $"{Math.Round(window.X * e)} x {Math.Round(window.Y * e)}  ({v * 100:F0} %)";
        }).ToArray();
        string hint = Styles.StyleKit.Style == Styles.VisualStyle.Ps1
            ? "PS1 keeps its low resolution on any screen; lower is chunkier"
            : "100 % is the window's own resolution; lower is chunkier and faster";
        UiKit.OptionRow(into, name, labels, index, i => set(scales[i]), hint);
    }

    /// <summary>Common window sizes that fit on the screen the window is on.</summary>
    private static Vector2I[] WindowSizes()
    {
        var screen = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen());
        Vector2I[] all =
        {
            new(1152, 648), new(1280, 720), new(1366, 768), new(1600, 900),
            new(1920, 1080), new(2560, 1440), new(3840, 2160),
        };
        return all.Where(v => v.X <= screen.X && v.Y <= screen.Y).ToArray();
    }

    /// <summary>
    /// A dropdown of WxH sizes. A saved size that is not a preset (hand-edited file) is shown as
    /// its own entry rather than silently replaced.
    /// </summary>
    private static void SizeRow(Container into, string name, Vector2I[] presets, int w, int h,
        string none, Action<int, int> set)
    {
        var sizes = new List<Vector2I> { Vector2I.Zero };
        sizes.AddRange(presets);
        var current = new Vector2I(w, h);
        if (!sizes.Contains(current)) sizes.Add(current);
        var labels = sizes.Select(v => v == Vector2I.Zero ? none : $"{v.X} x {v.Y}").ToArray();
        UiKit.OptionRow(into, name, labels, sizes.IndexOf(current), i => set(sizes[i].X, sizes[i].Y));
    }
}
