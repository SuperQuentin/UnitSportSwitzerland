using Godot;

namespace UnitSport.Core;

/// <summary>
/// The settings panel behind the main menu's "Settings" button: render distance, detail,
/// horizon, fog, window mode/size, 3D resolution, and the streaming knobs. Every control writes straight into
/// <see cref="GameSettings.Current"/> and commits, so the world re-applies itself live and
/// the file is saved — there is no Apply button to forget.
/// </summary>
public partial class SettingsMenu : PanelContainer
{
    public event Action? BackRequested;

    private Label _ringsValue = null!;
    private Label _horizonValue = null!;

    public static SettingsMenu Create() => new() { Name = "SettingsMenu" };

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(580, 0);
        Visible = false;
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.95f),
            ContentMarginLeft = 24, ContentMarginRight = 24,
            ContentMarginTop = 20, ContentMarginBottom = 20,
        };
        style.SetCornerRadiusAll(6);
        AddThemeStyleboxOverride("panel", style);

        // the panel is taller than the 648 px layout, so it scrolls instead of losing Back
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, GameSettings.BaseHeight - 60),
        };
        AddChild(scroll);
        var gutter = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        gutter.AddThemeConstantOverride("margin_right", 16); // keeps values clear of the scrollbar
        scroll.AddChild(gutter);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        gutter.AddChild(rows);

        var title = new Label { Text = "Settings" };
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        rows.AddChild(title);
        rows.AddChild(new HSeparator());

        var s = GameSettings.Current;

        Section(rows, "World");
        _ringsValue = SliderRow(rows, "Render distance", GameSettings.MinRings, GameSettings.MaxRings, 1,
            s.RenderDistanceRings, v => { GameSettings.Current.RenderDistanceRings = (int)v; },
            RingsText);

        OptionRow(rows, "Detail", new[] { "Low", "Medium", "High" }, (int)s.Detail,
            i => GameSettings.Current.Detail = (DetailPreset)i);

        _horizonValue = SliderRow(rows, "Horizon", 0, GameSettings.MaxHorizonKm, 5,
            s.HorizonKm, v => { GameSettings.Current.HorizonKm = (int)v; },
            v => v <= 0 ? "off" : $"{v:F0} km (100 m lattice)");

        ToggleRow(rows, "Distance fog", s.Fog, on => GameSettings.Current.Fog = on);

        Section(rows, "Display");
        OptionRow(rows, "Window", new[] { "Windowed", "Borderless fullscreen", "Fullscreen" }, (int)s.WindowMode,
            i => GameSettings.Current.WindowMode = (WindowMode)i);

        SizeRow(rows, "Window size", WindowSizes(), s.WindowWidth, s.WindowHeight, "Keep current",
            (w, h) => (GameSettings.Current.WindowWidth, GameSettings.Current.WindowHeight) = (w, h));

        ScaleRow(rows, "3D resolution", s.RenderScale, v => GameSettings.Current.RenderScale = v);

        Section(rows, "Time of day");
        SliderRow(rows, "Start time", 0, 23.5, 0.5, s.StartHour,
            v => GameSettings.Current.StartHour = (float)v, v => $"{(int)v:00}:{(int)(v % 1 * 60):00}");
        SliderRow(rows, "Day length", 0, 120, 1, s.DayLengthMinutes,
            v => GameSettings.Current.DayLengthMinutes = (float)v,
            v => v <= 0 ? "stopped" : $"{v:F0} min per day");

        SliderRow(rows, "Traffic", 0, 150, 5, s.TrafficCars,
            v => GameSettings.Current.TrafficCars = (int)v, v => v <= 0 ? "off" : $"{v:F0} cars");
        ToggleRow(rows, "Trains", s.Trains, on => GameSettings.Current.Trains = on);

        Section(rows, "Feel");
        OptionRow(rows, "Movement", new[] { "Game (arcade)", "Simulation (real physics)" }, (int)s.RideProfile,
            i => GameSettings.Current.RideProfile = (RideProfile)i);
        SliderRow(rows, "Sound effects", 0, 1, 0.05, s.SfxVolume,
            v => GameSettings.Current.SfxVolume = (float)v, v => v <= 0 ? "off" : $"{v * 100:F0} %");
        SliderRow(rows, "Ambience", 0, 1, 0.05, s.AmbienceVolume,
            v => GameSettings.Current.AmbienceVolume = (float)v, v => v <= 0 ? "off" : $"{v * 100:F0} %");
        OptionRow(rows, "Engine voice", new[] { "Realistic", "PS1 SPU", "NES 2A03", "C64 SID", "Genesis FM" }, (int)s.EngineVoice,
            i => GameSettings.Current.EngineVoice = (UnitSport.Audio.EngineVoice)i);
        SliderRow(rows, "Camera shake", 0, 1, 0.05, s.ScreenShake,
            v => GameSettings.Current.ScreenShake = (float)v, v => v <= 0 ? "off" : $"{v * 100:F0} %");
        ToggleRow(rows, "Speed lines", s.SpeedLines, on => GameSettings.Current.SpeedLines = on);

        Section(rows, "Controls");
        SliderRow(rows, "Stick look speed", 0.2, 3, 0.1, s.StickSensitivity,
            v => GameSettings.Current.StickSensitivity = (float)v, v => $"{v:F1}x");
        SliderRow(rows, "Stick deadzone", 0.05, 0.5, 0.01, s.StickDeadzone,
            v => GameSettings.Current.StickDeadzone = (float)v, v => $"{v * 100:F0} %");
        ToggleRow(rows, "Invert look Y", s.InvertY, on => GameSettings.Current.InvertY = on);
        ToggleRow(rows, "Controller vibration", s.Vibration, on => GameSettings.Current.Vibration = on);

        Section(rows, "Performance");
        SliderRow(rows, "Parallel tile builds", 0, GameSettings.MaxBuildsCap, 1, s.MaxConcurrentBuilds,
            v => GameSettings.Current.MaxConcurrentBuilds = (int)v,
            v => v <= 0 ? $"auto ({System.Environment.ProcessorCount} local, 6 streaming)" : $"{v:F0}");

        SliderRow(rows, "Mesh commit budget", 1, 16, 1, s.CommitBudgetMs,
            v => GameSettings.Current.CommitBudgetMs = v,
            v => $"{v:F0} ms / frame");

        ToggleRow(rows, "VSync", s.VSync, on => GameSettings.Current.VSync = on);

        OptionRow(rows, "Performance overlay (F3)", new[] { "Off", "FPS", "Detailed" }, (int)s.PerfOverlay,
            i => GameSettings.Current.PerfOverlay = (PerfOverlayMode)i);

        var logs = new HBoxContainer();
        logs.AddChild(new Label { Text = "Performance logs (F4 records)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var openLogs = new Button { Text = "Open folder" };
        openLogs.Pressed += PerfRecorder.OpenLogsFolder;
        logs.AddChild(openLogs);
        rows.AddChild(logs);

        rows.AddChild(new HSeparator());
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(0, 30) };
        back.Pressed += () => BackRequested?.Invoke();
        rows.AddChild(back);

        var hint = new Label { Text = "Changes apply immediately and are saved to user://settings.json" };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(hint);
    }

    /// <summary>
    /// 3D render scales offered as the resolution they produce. The low end is the PS1 look
    /// pushed further; 75% is the tuned default; above 100% supersamples.
    /// </summary>
    private static readonly float[] RenderScales = { 0.25f, 0.35f, 0.5f, 0.625f, 0.75f, 0.875f, 1f, 1.25f, 1.5f, 2f };

    private static void ScaleRow(Container into, string name, float current, Action<float> set)
    {
        var scales = RenderScales.ToList();
        int index = scales.FindIndex(v => Math.Abs(v - current) < 0.001f);
        if (index < 0) { scales.Add(current); scales.Sort(); index = scales.IndexOf(current); }

        var labels = scales.Select(v =>
            $"{Math.Round(GameSettings.BaseWidth * v)} x {Math.Round(GameSettings.BaseHeight * v)}  ({v * 100:F0} %)")
            .ToArray();
        OptionRow(into, name, labels, index, i => set(scales[i]));
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
    /// A dropdown of WxH sizes. A saved size that is not a preset (hand-edited file,
    /// <c>--resolution</c>) is shown as its own entry rather than silently replaced.
    /// </summary>
    private static void SizeRow(Container into, string name, Vector2I[] presets, int w, int h,
        string? none, Action<int, int> set)
    {
        var sizes = new List<Vector2I>();
        if (none != null) sizes.Add(Vector2I.Zero);
        sizes.AddRange(presets);
        var current = new Vector2I(w, h);
        if (!sizes.Contains(current)) sizes.Add(current);

        var labels = sizes.Select(v => v == Vector2I.Zero ? none! : $"{v.X} x {v.Y}").ToArray();
        OptionRow(into, name, labels, sizes.IndexOf(current), i => set(sizes[i].X, sizes[i].Y));
    }

    private static string RingsText(double v)
    {
        int n = (int)v;
        int tiles = (2 * n + 1) * (2 * n + 1);
        return $"{n} tiles ≈ {n} km, {tiles} tiles loaded";
    }

    private static void Section(Container into, string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", new Color(0.62f, 0.66f, 0.72f));
        into.AddChild(label);
    }

    private static Label SliderRow(Container into, string name, double min, double max, double step,
        double value, Action<double> set, Func<double, string> describe)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);

        var head = new HBoxContainer();
        head.AddChild(new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var valueLabel = new Label { Text = describe(value) };
        valueLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.59f, 0.65f));
        head.AddChild(valueLabel);
        box.AddChild(head);

        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Value = value,
            CustomMinimumSize = new Vector2(0, 20),
        };
        // the label tracks the drag; the world only re-applies once the mouse is let go, so a
        // slow drag across the render-distance slider does not start forty ring evaluations
        bool dragging = false;
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += changed => { dragging = false; if (changed) Commit(slider.Value, set); };
        // keyboard and click-to-set changes never raise DragEnded, so they commit here
        slider.ValueChanged += v => { valueLabel.Text = describe(v); if (!dragging) Commit(v, set); };
        box.AddChild(slider);

        into.AddChild(box);
        return valueLabel;
    }

    private static void Commit(double v, Action<double> set)
    {
        set(v);
        GameSettings.Current.Commit();
    }

    private static void OptionRow(Container into, string name, string[] items, int selected, Action<int> set)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var option = new OptionButton();
        foreach (string item in items) option.AddItem(item);
        option.Selected = selected;
        option.ItemSelected += i => { set((int)i); GameSettings.Current.Commit(); };
        row.AddChild(option);
        into.AddChild(row);
    }

    private static void ToggleRow(Container into, string name, bool on, Action<bool> set)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var toggle = new CheckButton { ButtonPressed = on };
        toggle.Toggled += pressed => { set(pressed); GameSettings.Current.Commit(); };
        row.AddChild(toggle);
        into.AddChild(row);
    }
}
