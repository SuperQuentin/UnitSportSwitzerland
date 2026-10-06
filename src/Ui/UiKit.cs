using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The building blocks every menu is made of, so the screens read as one system: labels in the
/// type scale, big menu buttons that slide on hover, glass cards, setting rows, icon buttons.
/// Everything here is plain Godot controls styled by <see cref="UiTheme"/>.
/// </summary>
public static class UiKit
{
    public static Label Text(string text, int size = UiTheme.FontBody, Color? color = null, bool bold = false,
        HorizontalAlignment align = HorizontalAlignment.Left, bool wrap = false)
    {
        var l = new Label
        {
            Text = text,
            HorizontalAlignment = align,
            AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        if (size != UiTheme.FontBody) l.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) l.AddThemeColorOverride("font_color", c);
        if (bold) l.AddThemeFontOverride("font", UiTheme.Bold);
        return l;
    }

    /// <summary>A small uppercase section heading, letter-spaced.</summary>
    public static Label Section(string text)
    {
        var l = Text(text.ToUpperInvariant(), UiTheme.FontTiny, UiTheme.TextDim);
        l.AddThemeFontOverride("font", Spaced);
        return l;
    }

    private static FontVariation? _spaced;
    private static FontVariation Spaced => _spaced ??= new FontVariation { BaseFont = UiTheme.Bold, SpacingGlyph = 2 };

    public static VBoxContainer VBox(int separation = 8) { var v = new VBoxContainer(); v.AddThemeConstantOverride("separation", separation); return v; }
    public static HBoxContainer HBox(int separation = 8) { var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", separation); return h; }

    public static Control Spacer(float h = 0, float w = 0, bool expand = false) => new Control
    {
        CustomMinimumSize = new Vector2(w, h),
        SizeFlagsHorizontal = expand ? Control.SizeFlags.ExpandFill : Control.SizeFlags.Fill,
        SizeFlagsVertical = expand ? Control.SizeFlags.ExpandFill : Control.SizeFlags.Fill,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    public static HSeparator Line() => new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    public static MarginContainer Margin(Control child, int l, int t, int r, int b)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", l);
        m.AddThemeConstantOverride("margin_top", t);
        m.AddThemeConstantOverride("margin_right", r);
        m.AddThemeConstantOverride("margin_bottom", b);
        m.AddChild(child);
        return m;
    }

    /// <summary>A glass card: a panel with a lighter fill than the screen panel.</summary>
    public static PanelContainer Card(Control content, float alpha = 0.55f, int margin = 18)
    {
        var p = new PanelContainer();
        var s = UiTheme.Flat(new Color(0.10f, 0.115f, 0.14f, alpha), 12, margin, margin, new Color(1, 1, 1, 0.07f), 1);
        p.AddThemeStyleboxOverride("panel", s);
        p.AddChild(content);
        return p;
    }

    /// <summary>
    /// A big left-aligned menu entry: on hover or focus its label slides right and an amber bar
    /// fades in at its left edge. The slide is the stylebox's left margin, tweened, because a
    /// container owns its children's positions and would undo a moved label.
    /// </summary>
    public static Button MenuButton(string text, int fontSize = 20)
    {
        var b = new Button
        {
            Text = text,
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, 46),
            FocusMode = Control.FocusModeEnum.All,
        };
        b.AddThemeFontSizeOverride("font_size", fontSize);
        b.AddThemeFontOverride("font", UiTheme.Bold);
        var normal = UiTheme.Flat(new Color(1, 1, 1, 0), 8, 18, 8);
        var hover = UiTheme.Flat(new Color(1, 1, 1, 0.06f), 8, 18, 8);
        var pressed = UiTheme.Flat(new Color(1, 1, 1, 0.10f), 8, 18, 8);
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", pressed);
        b.AddThemeStyleboxOverride("hover_pressed", pressed);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeStyleboxOverride("disabled", normal);
        b.AddThemeColorOverride("font_color", UiTheme.Text);
        b.AddThemeColorOverride("font_focus_color", Colors.White);
        b.AddThemeColorOverride("font_hover_color", Colors.White);

        HoverSlide.Attach(b, new[] { normal, hover, pressed });
        return b;
    }

    /// <summary>An ordinary action button (Join, Save, Cancel…).</summary>
    public static Button Button(string text, bool primary = false, int minWidth = 0)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(minWidth, 38), FocusMode = Control.FocusModeEnum.All };
        if (primary)
        {
            b.AddThemeStyleboxOverride("normal", UiTheme.Flat(UiTheme.Amber, 8, 18, 8));
            b.AddThemeStyleboxOverride("hover", UiTheme.Flat(UiTheme.Amber.Lightened(0.12f), 8, 18, 8));
            b.AddThemeStyleboxOverride("pressed", UiTheme.Flat(UiTheme.Amber.Darkened(0.1f), 8, 18, 8));
            b.AddThemeStyleboxOverride("hover_pressed", UiTheme.Flat(UiTheme.Amber.Darkened(0.1f), 8, 18, 8));
            b.AddThemeStyleboxOverride("focus", UiTheme.Flat(new Color(0, 0, 0, 0), 8, 18, 8, Colors.White, 2));
            b.AddThemeStyleboxOverride("disabled", UiTheme.Flat(new Color(UiTheme.Amber, 0.25f), 8, 18, 8));
            var dark = new Color(0.07f, 0.07f, 0.08f);
            foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
                b.AddThemeColorOverride(c, dark);
            b.AddThemeColorOverride("font_disabled_color", new Color(dark, 0.6f));
            b.AddThemeFontOverride("font", UiTheme.Bold);
        }
        return b;
    }

    /// <summary>A square button that is only an icon (edit, remove, star…), with a tooltip.</summary>
    public static Button IconButton(Texture2D icon, string tooltip, int size = 34)
    {
        var b = new Button
        {
            Icon = icon,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(size, size),
            IconAlignment = HorizontalAlignment.Center,
            FocusMode = Control.FocusModeEnum.All,
            ExpandIcon = false,
        };
        var clear = UiTheme.Flat(new Color(1, 1, 1, 0), 8, 6, 6);
        b.AddThemeStyleboxOverride("normal", clear);
        b.AddThemeColorOverride("icon_normal_color", UiTheme.TextDim);
        b.AddThemeColorOverride("icon_hover_color", Colors.White);
        b.AddThemeColorOverride("icon_focus_color", Colors.White);
        b.AddThemeColorOverride("icon_pressed_color", UiTheme.Amber);
        return b;
    }

    // ---- setting rows ---------------------------------------------------------------------------

    private const float ControlWidth = 300;

    private static HBoxContainer Row(string name, string? hint = null)
    {
        var row = HBox(16);
        row.CustomMinimumSize = new Vector2(0, 40);
        var names = VBox(0);
        names.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        names.Alignment = BoxContainer.AlignmentMode.Center;
        names.AddChild(Text(name));
        if (hint != null) names.AddChild(Text(hint, UiTheme.FontTiny, UiTheme.TextFaint, wrap: true));
        row.AddChild(names);
        return row;
    }

    /// <summary>
    /// A slider with its value written beside it. The world re-applies only once the drag is let
    /// go, so dragging the render distance does not start forty ring evaluations; keyboard and
    /// pad steps (which never raise DragEnded) commit at once.
    /// </summary>
    public static HBoxContainer SliderRow(Container into, string name, double min, double max, double step,
        double value, Action<double> set, Func<double, string> describe, string? hint = null)
    {
        var row = Row(name, hint);
        var right = HBox(12);
        right.CustomMinimumSize = new Vector2(ControlWidth, 0);
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Value = value,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(0, 22),
            Scrollable = false,   // the wheel scrolls the page, it does not nudge the slider under the pointer
            FocusMode = Control.FocusModeEnum.All,
        };
        var valueLabel = Text(describe(value), UiTheme.FontSmall, UiTheme.TextDim);
        valueLabel.CustomMinimumSize = new Vector2(96, 0);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
        valueLabel.ClipText = true;
        valueLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        valueLabel.TooltipText = describe(value);
        valueLabel.MouseFilter = Control.MouseFilterEnum.Pass;
        bool dragging = false;
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += changed => { dragging = false; if (changed) Commit(slider.Value, set); };
        slider.ValueChanged += v =>
        {
            valueLabel.Text = describe(v);
            valueLabel.TooltipText = describe(v);
            if (!dragging) Commit(v, set);
            else
            {
                // heard while dragging: the volume sliders take the value at once (bus volumes are
                // cheap); everything else, and the save, waits for the release (#375)
                set(v);
                Audio.SfxBus.ApplyVolumes();
            }
        };
        right.AddChild(slider);
        right.AddChild(valueLabel);
        row.AddChild(right);
        into.AddChild(row);
        return row;
    }

    private static void Commit(double v, Action<double> set)
    {
        set(v);
        GameSettings.Current.Commit();
    }

    public static OptionButton OptionRow(Container into, string name, string[] items, int selected, Action<int> set, string? hint = null)
    {
        var row = Row(name, hint);
        var option = new OptionButton { CustomMinimumSize = new Vector2(ControlWidth, 36), FocusMode = Control.FocusModeEnum.All, FitToLongestItem = false };
        option.ClipText = true;
        foreach (string item in items) option.AddItem(item);
        option.Selected = selected;
        option.ItemSelected += i => { set((int)i); GameSettings.Current.Commit(); };
        row.AddChild(option);
        into.AddChild(row);
        return option;
    }

    public static CheckButton ToggleRow(Container into, string name, bool on, Action<bool> set, string? hint = null)
    {
        var row = Row(name, hint);
        var holder = HBox(0);
        holder.CustomMinimumSize = new Vector2(ControlWidth, 0);
        holder.Alignment = BoxContainer.AlignmentMode.End;
        var toggle = new CheckButton { ButtonPressed = on, FocusMode = Control.FocusModeEnum.All };
        toggle.Toggled += pressed => { set(pressed); GameSettings.Current.Commit(); };
        holder.AddChild(toggle);
        row.AddChild(holder);
        into.AddChild(row);
        return toggle;
    }

    public static Button ActionRow(Container into, string name, string buttonText, Action pressed, string? hint = null)
    {
        var row = Row(name, hint);
        var holder = HBox(0);
        holder.CustomMinimumSize = new Vector2(ControlWidth, 0);
        holder.Alignment = BoxContainer.AlignmentMode.End;
        var b = Button(buttonText);
        b.Pressed += pressed;
        holder.AddChild(b);
        row.AddChild(holder);
        into.AddChild(row);
        return b;
    }

    /// <summary>A coloured status dot (ping, online/offline).</summary>
    public static TextureRect StatusDot(Color color) => new()
    {
        Texture = UiTheme.Dot(color, 10),
        StretchMode = TextureRect.StretchModeEnum.KeepCentered,
        CustomMinimumSize = new Vector2(12, 12),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    /// <summary>A vertical scroll page with a gutter that keeps values clear of the scrollbar.</summary>
    public static (ScrollContainer Scroll, VBoxContainer Rows) ScrollPage(int separation = 4)
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FollowFocus = true,
        };
        var rows = VBox(separation);
        rows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(Margin(rows, 2, 2, 14, 8));
        ((Control)rows.GetParent()).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return (scroll, rows);
    }
}

/// <summary>
/// The hover slide of a <see cref="UiKit.MenuButton"/>: a tweened left content margin and an
/// amber accent bar, in on hover or focus, out when neither holds.
/// </summary>
public static class HoverSlide
{
    private const float Rest = 18, Slid = 30, Seconds = 0.14f;

    public static void Attach(Button b, StyleBoxFlat[] boxes)
    {
        var bar = new ColorRect
        {
            Color = UiTheme.Amber,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
        };
        bar.AnchorTop = 0.22f; bar.AnchorBottom = 0.78f;
        bar.OffsetLeft = 0; bar.OffsetRight = 3;
        b.AddChild(bar);

        Tween? tween = null;
        bool hovered = false, focused = false;
        void Update()
        {
            bool on = (hovered || focused) && !b.Disabled;
            tween?.Kill();
            tween = b.CreateTween().SetIgnoreTimeScale(true).SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            float from = boxes[0].ContentMarginLeft;
            tween.TweenMethod(Callable.From<float>(v =>
            {
                foreach (var s in boxes) s.ContentMarginLeft = v;
            }), from, on ? Slid : Rest, Seconds);
            tween.TweenProperty(bar, "modulate:a", on ? 1f : 0f, Seconds);
        }
        b.MouseEntered += () => { hovered = true; Update(); };
        b.MouseExited += () => { hovered = false; Update(); };
        b.FocusEntered += () => { focused = true; Update(); };
        b.FocusExited += () => { focused = false; Update(); };
    }
}
