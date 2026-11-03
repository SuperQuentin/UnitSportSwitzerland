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
        // a role colour becomes a variation, so the theme above (the HUD's or a menu skin's, #1135) colours it
        if (color is { } c)
        {
            if (Variation(c) is { } v) l.ThemeTypeVariation = v;
            else if (c != UiTheme.Text) l.AddThemeColorOverride("font_color", c);
        }
        if (bold) Bold(l);
        return l;
    }

    /// <summary>The theme variation standing for one of <see cref="UiTheme"/>'s role colours, or null.</summary>
    public static string? Variation(Color c) =>
        c == UiTheme.TextDim ? Skins.SkinTheme.Dim
        : c == UiTheme.TextFaint ? Skins.SkinTheme.Faint
        : c == UiTheme.Amber ? Skins.SkinTheme.Accent
        : c == UiTheme.Good ? Skins.SkinTheme.Good
        : c == UiTheme.Warn ? Skins.SkinTheme.Warn
        : c == UiTheme.Bad ? Skins.SkinTheme.Bad
        : null;

    /// <summary>The bold face of the theme a control ends up under (the skin's rounded font, or the HUD's).</summary>
    public static void Bold(Control c)
    {
        c.AddThemeFontOverride("font", UiTheme.Bold);
        c.Ready += () =>
        {
            if (c.HasThemeFont("bold_font", "Label")) c.AddThemeFontOverride("font", c.GetThemeFont("bold_font", "Label"));
        };
    }

    /// <summary>A big title in its theme's display face (#1135): the logo, a page's name; in capitals when the skin says so.</summary>
    public static void Display(Label l)
    {
        l.AddThemeFontOverride("font", UiTheme.Bold);
        l.Ready += () =>
        {
            if (l.HasThemeFont("display_font", "Label")) l.AddThemeFontOverride("font", l.GetThemeFont("display_font", "Label"));
            if (l.GetThemeConstant("display_uppercase", "Label") == 1) l.Uppercase = true;
        };
    }

    /// <summary>A button's text in its theme's dim colour (a secondary entry: Quit, an inactive tab).</summary>
    public static void DimFont(Button b)
    {
        b.AddThemeColorOverride("font_color", UiTheme.TextDim);
        b.Ready += () => b.AddThemeColorOverride("font_color", b.GetThemeColor("font_color", Skins.SkinTheme.Dim));
    }

    /// <summary>A small uppercase section heading, letter-spaced.</summary>
    public static Label Section(string text)
    {
        var l = Text(text.ToUpperInvariant());
        l.ThemeTypeVariation = Skins.SkinTheme.Section;
        return l;
    }

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

    /// <summary>A card: the theme's (a glass card on a HUD, a sticker or glowing card in a menu skin), with its own margin.</summary>
    public static PanelContainer Card(Control content, float alpha = 0.55f, int margin = 18)
    {
        var p = new PanelContainer { ThemeTypeVariation = Skins.SkinTheme.Card };
        p.Ready += () =>
        {
            if (p.GetThemeStylebox("panel", Skins.SkinTheme.Card).Duplicate() is not StyleBoxFlat s) return;
            s.ContentMarginLeft = s.ContentMarginRight = s.ContentMarginTop = s.ContentMarginBottom = margin;
            s.BgColor = new Color(s.BgColor, s.BgColor.A * alpha / 0.55f);
            p.AddThemeStyleboxOverride("panel", s);
        };
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
        b.ThemeTypeVariation = Skins.SkinTheme.MenuButton;
        // the slide tweens the boxes' left margin: this button's own copies of its theme's boxes
        b.Ready += () =>
        {
            var boxes = new List<StyleBoxFlat>();
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
                if (b.GetThemeStylebox(state).Duplicate() is StyleBoxFlat box)
                {
                    b.AddThemeStyleboxOverride(state, box);
                    boxes.Add(box);
                }
            HoverSlide.Attach(b, boxes.ToArray());
        };
        Juice.Attach(b);
        return b;
    }

    /// <summary>An ordinary action button (Join, Save, Cancel…).</summary>
    public static Button Button(string text, bool primary = false, int minWidth = 0)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(minWidth, 38), FocusMode = Control.FocusModeEnum.All };
        if (primary) b.ThemeTypeVariation = Skins.SkinTheme.Primary;
        Juice.Attach(b);
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
        b.ThemeTypeVariation = Skins.SkinTheme.Icon;
        Juice.Attach(b);
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
    /// A text field. It holds the keyboard while focused (<see cref="Core.UiFocus"/>), so typing never walks the
    /// player; <paramref name="secret"/> hides what is typed (a password).
    /// </summary>
    public static LineEdit TextRow(Container into, string name, string text, string placeholder, string? hint = null, bool secret = false)
    {
        var row = Row(name, hint);
        var edit = Edit(text, placeholder, secret);
        edit.CustomMinimumSize = new Vector2(ControlWidth, 36);
        row.AddChild(edit);
        into.AddChild(row);
        return edit;
    }

    /// <summary>
    /// A form's text field (#1209): the name just above the field, as on a sign-in form, rather than a settings row's
    /// name at the far left and control at the far right.
    /// </summary>
    public static LineEdit FormField(Container into, string name, string text, string placeholder, string? hint = null, bool secret = false)
    {
        var field = VBox(4);
        var title = HBox(10);
        title.AddChild(Text(name));
        if (hint != null)
        {
            var h = Text(hint, UiTheme.FontTiny, UiTheme.TextFaint);
            h.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
            title.AddChild(h);
        }
        field.AddChild(title);
        var edit = Edit(text, placeholder, secret);
        edit.CustomMinimumSize = new Vector2(FormFieldWidth, 40);
        edit.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        field.AddChild(edit);
        into.AddChild(field);
        return edit;
    }

    /// <summary>How wide a form's text field is: an email address and then some.</summary>
    public const float FormFieldWidth = 460;

    /// <summary>A line edit that holds the keyboard while focused (<see cref="Core.UiFocus"/>).</summary>
    private static LineEdit Edit(string text, string placeholder, bool secret)
    {
        var edit = new LineEdit { Text = text, PlaceholderText = placeholder, Secret = secret };
        edit.FocusEntered += () => Core.UiFocus.Set(edit, true);
        edit.FocusExited += () => Core.UiFocus.Set(edit, false);
        edit.TreeExiting += () => Core.UiFocus.Set(edit, false);
        return edit;
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

    /// <param name="commit">What applies the change; the default <see cref="GameSettings.Commit"/> saves every setting.</param>
    public static OptionButton OptionRow(Container into, string name, string[] items, int selected, Action<int> set, string? hint = null,
        Action? commit = null)
    {
        var row = Row(name, hint);
        var option = new OptionButton { CustomMinimumSize = new Vector2(ControlWidth, 36), FocusMode = Control.FocusModeEnum.All, FitToLongestItem = false };
        option.ClipText = true;
        foreach (string item in items) option.AddItem(item);
        option.Selected = selected;
        option.ItemSelected += i => { set((int)i); (commit ?? GameSettings.Current.Commit)(); };
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
            Color = b.HasThemeColor("accent_bar") ? b.GetThemeColor("accent_bar") : UiTheme.Amber,
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

/// <summary>
/// The juice of a control (#1135): it grows and tilts a little under the pointer or focus and squashes while pressed,
/// springing back (<c>TRANS_BACK</c>). How much comes from its theme (a skin's <c>hoverScale</c> and <c>hoverTilt</c>;
/// the HUD's is none), and Settings › Video › Reduce motion turns it off.
/// </summary>
public static class Juice
{
    private const float Seconds = 0.25f;

    public static void Attach(Control c)
    {
        Tween? tween = null;
        bool hovered = false, focused = false, down = false;
        void Update()
        {
            if (!c.IsInsideTree() || GameSettings.Current.ReduceMotion) return;
            int pct = c.GetThemeConstant("hover_scale_pct", Skins.SkinTheme.JuiceType);
            int tilt = c.GetThemeConstant("hover_tilt_tenths", Skins.SkinTheme.JuiceType);
            if (pct == 0 && tilt == 0) return;
            bool lit = (hovered || focused) && !(c is BaseButton { Disabled: true });
            float scale = down ? 1f - pct / 200f : lit ? 1f + pct / 100f : 1f;
            // neighbours lean opposite ways, as stickers on a page do
            float angle = lit && !down ? Mathf.DegToRad(tilt / 10f) * (c.GetIndex() % 2 == 0 ? -1 : 1) : 0;
            c.PivotOffset = c.Size / 2;
            tween?.Kill();
            tween = c.CreateTween().SetIgnoreTimeScale(true).SetParallel().SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            tween.TweenProperty(c, "scale", new Vector2(scale, scale), Seconds);
            tween.TweenProperty(c, "rotation", angle, Seconds);
        }
        c.MouseEntered += () => { hovered = true; Update(); };
        c.MouseExited += () => { hovered = false; down = false; Update(); };
        c.FocusEntered += () => { focused = true; Update(); };
        c.FocusExited += () => { focused = false; Update(); };
        if (c is BaseButton b)
        {
            b.ButtonDown += () => { down = true; Update(); };
            b.ButtonUp += () => { down = false; Update(); };
        }
    }
}
