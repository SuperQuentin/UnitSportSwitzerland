using Godot;

namespace UnitSport.Core;

/// <summary>
/// The column of key hints at the bottom right: what the buttons do <i>here</i>. "[E] Get in
/// Helicopter" beside a parked one, "[R] Travel" on foot, "[T] Walk" in the fly camera — never the
/// whole manual, which is <see cref="ControlsHelp"/> on F1, named on the last line so it can be
/// found.
///
/// <para>
/// It knows nothing of the game. <see cref="Source"/> is asked a few times a second for the
/// (action, text) pairs that apply now, and each is printed with that action's binding for the
/// device in hand (<see cref="InputHints"/>), so the column changes the moment a pad is touched.
/// Hidden while a menu or text field holds the input (<see cref="UiFocus"/>).
/// </para>
/// </summary>
public partial class PromptBar : CanvasLayer
{
    /// <summary>What applies right now: an action name and what it does. Resolved every refresh.</summary>
    public Func<IEnumerable<(string Action, string Text)>>? Source { get; set; }

    private VBoxContainer _lines = null!;
    private double _timer;

    public static PromptBar Create() => new() { Name = "PromptBar" };

    public override void _Ready()
    {
        Layer = 11;   // over the feel HUD and the loot/gather prompts, under the inventory and menus
        _lines = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Begin,
            Alignment = BoxContainer.AlignmentMode.End,
        };
        _lines.AddThemeConstantOverride("separation", 2);
        _lines.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        // above the vehicle readout (engine, damage), which owns the corner itself
        _lines.OffsetRight = -18;
        _lines.OffsetBottom = -64;
        AddChild(_lines);
        PlayerInput.DeviceChanged += Rebuild;
    }

    public override void _ExitTree() => PlayerInput.DeviceChanged -= Rebuild;

    public override void _Process(double delta)
    {
        _timer -= delta;
        if (_timer > 0) return;
        _timer = 0.2;
        Rebuild();
    }

    private string _shown = "";

    private void Rebuild()
    {
        var rows = UiFocus.TextEntryActive || Source == null
            ? new List<(string, string)>()
            : Source().ToList();

        string key = string.Join("|", rows.Select(r => InputHints.Tag(r.Item1) + r.Item2));
        if (key == _shown) return;
        _shown = key;

        foreach (var child in _lines.GetChildren()) child.QueueFree();
        for (int i = 0; i < rows.Count; i++)
        {
            var (action, text) = rows[i];
            bool last = i == rows.Count - 1 && action == PlayerInput.Help;
            _lines.AddChild(Row(action, text, last));
        }
    }

    private static Control Row(string action, string text, bool dim)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 8);

        var label = Outlined(text, dim ? 13 : 15, dim ? new Color(0.72f, 0.75f, 0.8f) : Colors.White);
        row.AddChild(label);

        // the key as a small keycap, so it reads as a key and not as part of the sentence
        var cap = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.82f),
            BorderColor = new Color(0.98f, 0.72f, 0.10f, dim ? 0.45f : 0.9f),
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 1, ContentMarginBottom = 1,
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        cap.AddThemeStyleboxOverride("panel", style);
        cap.AddChild(Outlined(InputHints.Label(action), dim ? 12 : 14, new Color(1f, 0.86f, 0.45f)));
        row.AddChild(cap);
        return row;
    }

    private static Label Outlined(string text, int size, Color color)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        label.AddThemeConstantOverride("outline_size", 5);
        return label;
    }
}
