using Godot;

namespace UnitSport.Core;

/// <summary>
/// The settings panel behind the main menu's "Settings" button: render distance, detail,
/// horizon, fog, and the streaming knobs. Every control writes straight into
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
        CustomMinimumSize = new Vector2(560, 0);
        Visible = false;
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.95f),
            ContentMarginLeft = 24, ContentMarginRight = 24,
            ContentMarginTop = 20, ContentMarginBottom = 20,
        };
        style.SetCornerRadiusAll(6);
        AddThemeStyleboxOverride("panel", style);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        AddChild(rows);

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

        Section(rows, "Performance");
        SliderRow(rows, "Parallel tile builds", 0, GameSettings.MaxBuildsCap, 1, s.MaxConcurrentBuilds,
            v => GameSettings.Current.MaxConcurrentBuilds = (int)v,
            v => v <= 0 ? $"auto ({System.Environment.ProcessorCount} local, 6 streaming)" : $"{v:F0}");

        SliderRow(rows, "Mesh commit budget", 1, 16, 1, s.CommitBudgetMs,
            v => GameSettings.Current.CommitBudgetMs = v,
            v => $"{v:F0} ms / frame");

        SliderRow(rows, "3D render scale", 0.35, 1.0, 0.05, s.RenderScale,
            v => GameSettings.Current.RenderScale = (float)v,
            v => $"{v * 100:F0} %");

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
