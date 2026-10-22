using Godot;

namespace UnitSport.Ui.Skins;

/// <summary>
/// Turns a <see cref="SkinData"/> into a Godot <c>Theme</c> (#1135): every control a menu uses, plus the named variations
/// <see cref="UiKit"/> asks for, so a label or button takes its colours from whichever theme it sits under (the HUD's or
/// a menu skin's), also when a page refreshes it later. <see cref="AddVariations"/> is shared with the HUD theme
/// (<see cref="UiTheme.Get"/>), which keeps its own look.
/// </summary>
public static class SkinTheme
{
    /// <summary>Label variations: a label set to one takes that role's colour from its theme.</summary>
    public const string Dim = "DimLabel", Faint = "FaintLabel", Accent = "AccentLabel", Good = "GoodLabel", Warn = "WarnLabel",
        Bad = "BadLabel", Section = "SectionLabel";

    /// <summary>Control variations.</summary>
    public const string MenuButton = "MenuEntryButton", Primary = "PrimaryButton", Icon = "IconButton", Card = "CardPanel", Page = "PagePanel";

    /// <summary>The theme type holding the juice numbers (<see cref="Juice"/>).</summary>
    public const string JuiceType = "Juice";

    private static FontFile? _baloo;

    public static Color C(SkinData s, string role)
    {
        var (r, g, b, a) = s.Color(role);
        return new Color(r, g, b, a);
    }

    private static (Font Regular, Font Bold) Fonts(SkinData s)
    {
        if (s.Font == "baloo2" && (_baloo ??= ResourceLoader.Load<FontFile>("res://assets/fonts/Baloo2-Variable.ttf")) is { } baloo)
        {
            var weight = TextServerManager.GetPrimaryInterface().NameToTag("weight");
            return (new FontVariation { BaseFont = baloo, VariationOpentype = new Godot.Collections.Dictionary { { weight, 500 } } },
                    new FontVariation { BaseFont = baloo, VariationOpentype = new Godot.Collections.Dictionary { { weight, 750 } } });
        }
        return (UiTheme.Font, UiTheme.Bold);
    }

    /// <summary>A box of the skin's shape: fill, outline, and its shadow (hard when offset, a glow when blurred).</summary>
    private static StyleBoxFlat Box(SkinData s, Color fill, int radius, int marginH, int marginV, Color? edge = null, int outline = 0, bool shadow = false)
    {
        var b = UiTheme.Flat(fill, radius, marginH, marginV, edge, outline);
        // the default 1 px anti-aliasing shows the fill as a halo outside a thick outline at the corners
        b.AntiAliasingSize = 0.5f;
        if (shadow && (s.Shape.ShadowSize > 0 || s.Shape.ShadowX != 0 || s.Shape.ShadowY != 0))
        {
            b.ShadowColor = C(s, "shadow");
            // a blur of 1 px reads as the hard edge of a sticker; a larger one is a soft shadow or a glow
            b.ShadowSize = Mathf.Max(1, s.Shape.ShadowSize);
            b.ShadowOffset = new Vector2(s.Shape.ShadowX, s.Shape.ShadowY);
        }
        return b;
    }

    /// <summary>The whole theme of a menu skin.</summary>
    public static Theme Build(SkinData s)
    {
        var (regular, bold) = Fonts(s);
        var sh = s.Shape;
        Color ink = C(s, "ink"), inkFaint = C(s, "inkFaint"), inkDim = C(s, "inkDim"), accent = C(s, "accent"),
            onAccent = C(s, "onAccent"), edge = C(s, "controlEdge"), card = C(s, "card"), hairline = C(s, "hairline"),
            control = C(s, "control"), hover = C(s, "controlHover"), pressed = C(s, "controlPressed"),
            cardEdge = C(s, "cardEdge");
        var t = new Theme { DefaultFont = regular, DefaultFontSize = UiTheme.FontBody };

        t.SetColor("font_color", "Label", ink);
        t.SetConstant("line_spacing", "Label", 2);

        var panel = Box(s, card, sh.Radius, 22, 22, cardEdge, sh.Outline, shadow: true);
        t.SetStylebox("panel", "PanelContainer", panel);
        t.SetStylebox("panel", "Panel", panel);

        var focus = UiTheme.Flat(new Color(0, 0, 0, 0), sh.ControlRadius, 14, 8, accent, Mathf.Max(2, sh.ControlOutline));
        foreach (string type in new[] { "Button", "OptionButton", "MenuButton" })
        {
            t.SetStylebox("normal", type, Box(s, control, sh.ControlRadius, 14, 8, edge, sh.ControlOutline));
            t.SetStylebox("hover", type, Box(s, hover, sh.ControlRadius, 14, 8, edge, sh.ControlOutline));
            t.SetStylebox("pressed", type, Box(s, pressed, sh.ControlRadius, 14, 8, edge, sh.ControlOutline));
            t.SetStylebox("hover_pressed", type, Box(s, pressed, sh.ControlRadius, 14, 8, edge, sh.ControlOutline));
            t.SetStylebox("disabled", type, Box(s, new Color(control, control.A * 0.5f), sh.ControlRadius, 14, 8, new Color(edge, edge.A * 0.4f), sh.ControlOutline));
            t.SetStylebox("focus", type, focus);
        }
        foreach (string type in new[] { "Button", "OptionButton", "CheckButton", "CheckBox", "MenuButton" })
        {
            t.SetColor("font_color", type, ink);
            t.SetColor("font_hover_color", type, ink);
            t.SetColor("font_pressed_color", type, ink);
            t.SetColor("font_hover_pressed_color", type, ink);
            t.SetColor("font_focus_color", type, ink);
            t.SetColor("font_disabled_color", type, inkFaint);
            t.SetConstant("h_separation", type, 10);
        }
        foreach (string type in new[] { "CheckButton", "CheckBox" })
        {
            var clear = UiTheme.Flat(new Color(0, 0, 0, 0), sh.ControlRadius, 4, 4);
            t.SetStylebox("normal", type, clear);
            t.SetStylebox("pressed", type, clear);
            t.SetStylebox("hover", type, UiTheme.Flat(new Color(hover, hover.A * 0.6f), sh.ControlRadius, 4, 4));
            t.SetStylebox("hover_pressed", type, UiTheme.Flat(new Color(hover, hover.A * 0.6f), sh.ControlRadius, 4, 4));
            t.SetStylebox("focus", type, UiTheme.Flat(new Color(0, 0, 0, 0), sh.ControlRadius, 4, 4, accent, 2));
        }
        var off = new Color(inkFaint, 0.45f);
        t.SetIcon("checked", "CheckButton", UiTheme.Pill(true, accent, off, onAccent, card));
        t.SetIcon("unchecked", "CheckButton", UiTheme.Pill(false, accent, off, onAccent, card));
        t.SetIcon("checked_disabled", "CheckButton", UiTheme.Pill(true, accent, off, onAccent, card));
        t.SetIcon("unchecked_disabled", "CheckButton", UiTheme.Pill(false, accent, off, onAccent, card));
        t.SetIcon("checked", "CheckBox", UiTheme.Tick(true, accent, inkDim, onAccent));
        t.SetIcon("unchecked", "CheckBox", UiTheme.Tick(false, accent, inkDim, onAccent));

        t.SetIcon("arrow", "OptionButton", UiTheme.Chevron(inkDim));
        var popup = Box(s, card, sh.ControlRadius + 2, 6, 6, edge, Mathf.Max(1, sh.ControlOutline), shadow: true);
        t.SetStylebox("panel", "PopupMenu", popup);
        t.SetStylebox("hover", "PopupMenu", UiTheme.Flat(new Color(accent, 0.22f), sh.ControlRadius, 10, 4));
        t.SetColor("font_color", "PopupMenu", ink);
        t.SetColor("font_hover_color", "PopupMenu", ink);
        t.SetConstant("v_separation", "PopupMenu", 8);
        t.SetConstant("item_start_padding", "PopupMenu", 10);
        t.SetIcon("radio_checked", "PopupMenu", UiTheme.Dot(accent, 8));
        t.SetIcon("radio_unchecked", "PopupMenu", UiTheme.Dot(new Color(1, 1, 1, 0), 8));
        t.SetIcon("checked", "PopupMenu", UiTheme.Dot(accent, 8));
        t.SetIcon("unchecked", "PopupMenu", UiTheme.Dot(new Color(1, 1, 1, 0), 8));

        var field = Box(s, control, sh.ControlRadius, 12, 8, edge, sh.ControlOutline);
        t.SetStylebox("normal", "LineEdit", field);
        t.SetStylebox("focus", "LineEdit", Box(s, control, sh.ControlRadius, 12, 8, accent, Mathf.Max(2, sh.ControlOutline)));
        t.SetStylebox("read_only", "LineEdit", field);
        t.SetColor("font_color", "LineEdit", ink);
        t.SetColor("font_placeholder_color", "LineEdit", inkFaint);
        t.SetColor("caret_color", "LineEdit", accent);
        t.SetColor("selection_color", "LineEdit", new Color(accent, 0.3f));

        var track = UiTheme.Flat(new Color(inkFaint, 0.35f), 4, 0, 3);
        var fill = UiTheme.Flat(accent, 4, 0, 3);
        t.SetStylebox("slider", "HSlider", track);
        t.SetStylebox("grabber_area", "HSlider", fill);
        t.SetStylebox("grabber_area_highlight", "HSlider", fill);
        t.SetIcon("grabber", "HSlider", UiTheme.Dot(cardEdge.A > 0.5f ? cardEdge : ink, 16));
        t.SetIcon("grabber_highlight", "HSlider", UiTheme.Dot(accent, 18));
        t.SetIcon("grabber_disabled", "HSlider", UiTheme.Dot(inkFaint, 14));
        t.SetIcon("tick", "HSlider", new ImageTexture());
        t.SetConstant("center_grabber", "HSlider", 1);

        foreach (string type in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", type, UiTheme.Flat(new Color(inkFaint, 0.12f), 4, 2, 2));
            t.SetStylebox("scroll_focus", type, UiTheme.Flat(new Color(inkFaint, 0.12f), 4, 2, 2));
            t.SetStylebox("grabber", type, UiTheme.Flat(new Color(inkDim, 0.5f), 4, 2, 2));
            t.SetStylebox("grabber_highlight", type, UiTheme.Flat(new Color(inkDim, 0.8f), 4, 2, 2));
            t.SetStylebox("grabber_pressed", type, UiTheme.Flat(accent, 4, 2, 2));
        }
        t.SetStylebox("panel", "ScrollContainer", new StyleBoxEmpty());

        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = hairline, Thickness = Mathf.Max(1, sh.ControlOutline) });
        t.SetConstant("separation", "HSeparator", 12);
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = hairline, Thickness = Mathf.Max(1, sh.ControlOutline), Vertical = true });

        t.SetStylebox("background", "ProgressBar", UiTheme.Flat(new Color(inkFaint, 0.25f), 6, 0, 3));
        t.SetStylebox("fill", "ProgressBar", UiTheme.Flat(C(s, "accent2"), 6, 0, 3));
        t.SetColor("font_color", "ProgressBar", new Color(0, 0, 0, 0));

        t.SetStylebox("panel", "TooltipPanel", Box(s, card, sh.ControlRadius, 10, 6, edge, Mathf.Max(1, sh.ControlOutline)));
        t.SetColor("font_color", "TooltipLabel", ink);

        AddVariations(t, s, bold);
        return t;
    }

    /// <summary>The variations <see cref="UiKit"/> uses, in the skin's colours and shapes.</summary>
    public static void AddVariations(Theme t, SkinData s, Font bold)
    {
        var sh = s.Shape;
        Color ink = C(s, "ink"), accent = C(s, "accent"), onAccent = C(s, "onAccent"), edge = C(s, "cardEdge"), card = C(s, "card"),
            hover = C(s, "controlHover"), pressed = C(s, "controlPressed"), soft = C(s, "controlEdge");

        foreach (var (name, role) in new[] { (Dim, "inkDim"), (Faint, "inkFaint"), (Accent, "accent"), (Good, "good"), (Warn, "warn"), (Bad, "bad") })
        {
            t.SetTypeVariation(name, "Label");
            t.SetColor("font_color", name, C(s, role));
        }
        t.SetTypeVariation(Section, "Label");
        t.SetColor("font_color", Section, C(s, "inkDim"));
        t.SetFont("font", Section, new FontVariation { BaseFont = bold, SpacingGlyph = 2 });
        t.SetFontSize("font_size", Section, UiTheme.FontTiny);
        // what UiKit.Text(bold: true) asks its theme for
        t.SetFont("bold_font", "Label", bold);

        // the big menu entry: clear at rest, a sticker or a tint under the pointer, an accent bar at its left
        t.SetTypeVariation(MenuButton, "Button");
        var rest = UiTheme.Flat(new Color(0, 0, 0, 0), sh.ControlRadius, 18, 8, new Color(soft, 0), sh.ControlOutline);
        var lit = Box(s, hover, sh.ControlRadius, 18, 8, soft, sh.ControlOutline);
        var down = Box(s, pressed, sh.ControlRadius, 18, 8, soft, sh.ControlOutline);
        t.SetStylebox("normal", MenuButton, rest);
        t.SetStylebox("hover", MenuButton, lit);
        t.SetStylebox("pressed", MenuButton, down);
        t.SetStylebox("hover_pressed", MenuButton, down);
        t.SetStylebox("focus", MenuButton, new StyleBoxEmpty());
        t.SetStylebox("disabled", MenuButton, rest);
        t.SetFont("font", MenuButton, bold);
        foreach (string c in new[] { "font_color", "font_focus_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
            t.SetColor(c, MenuButton, ink);
        t.SetColor("accent_bar", MenuButton, accent);

        t.SetTypeVariation(Primary, "Button");
        t.SetStylebox("normal", Primary, Box(s, accent, sh.ControlRadius, 18, 8, edge, sh.ControlOutline, shadow: sh.ControlShadow));
        t.SetStylebox("hover", Primary, Box(s, accent.Lightened(0.12f), sh.ControlRadius, 18, 8, edge, sh.ControlOutline, shadow: sh.ControlShadow));
        t.SetStylebox("pressed", Primary, Box(s, accent.Darkened(0.1f), sh.ControlRadius, 18, 8, edge, sh.ControlOutline));
        t.SetStylebox("hover_pressed", Primary, Box(s, accent.Darkened(0.1f), sh.ControlRadius, 18, 8, edge, sh.ControlOutline));
        t.SetStylebox("focus", Primary, UiTheme.Flat(new Color(0, 0, 0, 0), sh.ControlRadius, 18, 8, ink, 2));
        t.SetStylebox("disabled", Primary, UiTheme.Flat(new Color(accent, 0.25f), sh.ControlRadius, 18, 8));
        foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            t.SetColor(c, Primary, onAccent);
        t.SetColor("font_disabled_color", Primary, new Color(onAccent, 0.6f));
        t.SetFont("font", Primary, bold);

        t.SetTypeVariation(Icon, "Button");
        var clear = UiTheme.Flat(new Color(0, 0, 0, 0), sh.ControlRadius, 6, 6);
        t.SetStylebox("normal", Icon, clear);
        t.SetStylebox("hover", Icon, UiTheme.Flat(new Color(hover, hover.A * 0.8f), sh.ControlRadius, 6, 6));
        t.SetStylebox("pressed", Icon, UiTheme.Flat(pressed, sh.ControlRadius, 6, 6));
        t.SetStylebox("hover_pressed", Icon, UiTheme.Flat(pressed, sh.ControlRadius, 6, 6));
        t.SetStylebox("disabled", Icon, clear);
        t.SetColor("icon_normal_color", Icon, C(s, "inkDim"));
        t.SetColor("icon_hover_color", Icon, ink);
        t.SetColor("icon_focus_color", Icon, ink);
        t.SetColor("icon_pressed_color", Icon, accent);

        t.SetTypeVariation(Card, "PanelContainer");
        t.SetStylebox("panel", Card, Box(s, card, sh.Radius, 18, 18, edge, sh.Outline, shadow: sh.ControlShadow));
        t.SetTypeVariation(Page, "PanelContainer");
        t.SetStylebox("panel", Page, Box(s, card, sh.Radius + 4, 26, 26, edge, sh.Outline, shadow: true));

        t.SetConstant("hover_scale_pct", JuiceType, Mathf.RoundToInt(sh.HoverScale * 100));
        t.SetConstant("hover_tilt_tenths", JuiceType, Mathf.RoundToInt(sh.HoverTilt * 10));
    }
}
