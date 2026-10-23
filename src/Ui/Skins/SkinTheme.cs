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
    public const string MenuButton = "MenuEntryButton", Primary = "PrimaryButton", Icon = "IconButton", Card = "CardPanel", Page = "PagePanel",
        Tab = "TabButton";

    /// <summary>The theme type holding the juice numbers (<see cref="Juice"/>).</summary>
    public const string JuiceType = "Juice";

    private static FontFile? _saira, _sairaCondensed;

    public static Color C(SkinData s, string role)
    {
        var (r, g, b, a) = s.Color(role);
        return new Color(r, g, b, a);
    }

    /// <summary>The skin's text, bold and display faces (the display face sets big titles: the logo, a page's name).</summary>
    public static (Font Regular, Font Bold, Font Display) Fonts(SkinData s)
    {
        if (s.Font == "saira" && (_saira ??= ResourceLoader.Load<FontFile>("res://assets/fonts/Saira-Variable.ttf")) is { } saira
            && (_sairaCondensed ??= ResourceLoader.Load<FontFile>("res://assets/fonts/SairaCondensed-ExtraBold.ttf")) is { } condensed)
        {
            var weight = TextServerManager.GetPrimaryInterface().NameToTag("weight");
            // the condensed titles lean as the controls do: an oblique made from the upright face
            var lean = new Transform2D(1, s.Shape.Skew > 0 ? 0.2f : 0, 0, 1, 0, 0);   // Godot: (1, slant, 0, 1, 0, 0) is an oblique
            return (new FontVariation { BaseFont = saira, VariationOpentype = new Godot.Collections.Dictionary { { weight, 500 } } },
                    new FontVariation { BaseFont = saira, VariationOpentype = new Godot.Collections.Dictionary { { weight, 650 } } },
                    new FontVariation { BaseFont = condensed, VariationTransform = lean });
        }
        return (UiTheme.Font, UiTheme.Bold, UiTheme.Bold);
    }

    /// <summary>A box of the skin's shape: fill, outline, and its shadow (hard when offset, a glow when blurred).</summary>
    private static StyleBoxFlat Box(SkinData s, Color fill, int radius, int marginH, int marginV, Color? edge = null, int outline = 0, bool shadow = false, bool slant = false)
    {
        var b = UiTheme.Flat(fill, radius, marginH, marginV, edge, outline);
        if (slant && s.Shape.Skew > 0)
        {
            // a parallelogram: the box leans, its content stays upright; a little more room at the sides for the lean
            b.Skew = new Vector2(s.Shape.Skew, 0);
            b.ContentMarginLeft += 6;
            b.ContentMarginRight += 6;
        }
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
        var (regular, bold, display) = Fonts(s);
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

        var focus = Box(s, new Color(0, 0, 0, 0), sh.ControlRadius, 14, 8, accent, Mathf.Max(2, sh.ControlOutline), slant: true);
        foreach (string type in new[] { "Button", "OptionButton", "MenuButton" })
        {
            t.SetStylebox("normal", type, Box(s, control, sh.ControlRadius, 14, 8, edge, sh.ControlOutline, slant: true));
            t.SetStylebox("hover", type, Box(s, hover, sh.ControlRadius, 14, 8, edge, sh.ControlOutline, slant: true));
            t.SetStylebox("pressed", type, Box(s, pressed, sh.ControlRadius, 14, 8, edge, sh.ControlOutline, slant: true));
            t.SetStylebox("hover_pressed", type, Box(s, pressed, sh.ControlRadius, 14, 8, edge, sh.ControlOutline, slant: true));
            t.SetStylebox("disabled", type, Box(s, new Color(control, control.A * 0.5f), sh.ControlRadius, 14, 8, new Color(edge, edge.A * 0.4f), sh.ControlOutline, slant: true));
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
        if (sh.Skew > 0)
        {
            // a slanted switch: a parallelogram track, a square knob
            foreach (var (name, on) in new[] { ("checked", true), ("unchecked", false), ("checked_disabled", true), ("unchecked_disabled", false) })
                t.SetIcon(name, "CheckButton", UiTheme.SlantSwitch(on, accent, control.Lightened(0.08f), onAccent, inkFaint, sh.Skew));
        }
        else
        {
            t.SetIcon("checked", "CheckButton", UiTheme.Pill(true, accent, off, onAccent, card));
            t.SetIcon("unchecked", "CheckButton", UiTheme.Pill(false, accent, off, onAccent, card));
            t.SetIcon("checked_disabled", "CheckButton", UiTheme.Pill(true, accent, off, onAccent, card));
            t.SetIcon("unchecked_disabled", "CheckButton", UiTheme.Pill(false, accent, off, onAccent, card));
        }
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

        var field = Box(s, control, sh.ControlRadius, 12, 8, edge, sh.ControlOutline, slant: true);
        t.SetStylebox("normal", "LineEdit", field);
        t.SetStylebox("focus", "LineEdit", Box(s, control, sh.ControlRadius, 12, 8, accent, Mathf.Max(2, sh.ControlOutline), slant: true));
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

        AddVariations(t, s, bold, display);
        return t;
    }

    /// <summary>The variations <see cref="UiKit"/> uses, in the skin's colours and shapes.</summary>
    public static void AddVariations(Theme t, SkinData s, Font bold, Font? display = null)
    {
        display ??= bold;
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
        // what UiKit.Text(bold: true) asks its theme for, and the big titles' face (the logo, a page's name)
        t.SetFont("bold_font", "Label", bold);
        t.SetFont("display_font", "Label", display);
        t.SetConstant("display_uppercase", "Label", sh.DisplayUppercase ? 1 : 0);

        // the big menu entry: clear at rest, a sticker or a tint under the pointer, an accent bar at its left
        t.SetTypeVariation(MenuButton, "Button");
        // a filling skin: a faint bar at rest that fills with the accent under the pointer, its text dark on it
        // (no vertical margin when filled: a condensed display face already carries a tall line)
        var rest = sh.MenuFill ? Box(s, new Color(1, 1, 1, 0.04f), sh.ControlRadius, 18, 0, slant: true)
            : UiTheme.Flat(new Color(0, 0, 0, 0), sh.ControlRadius, 18, 8, new Color(soft, 0), sh.ControlOutline);
        var lit = sh.MenuFill ? Box(s, accent, sh.ControlRadius, 18, 0, slant: true) : Box(s, hover, sh.ControlRadius, 18, 8, soft, sh.ControlOutline);
        var down = sh.MenuFill ? Box(s, accent.Darkened(0.15f), sh.ControlRadius, 18, 0, slant: true) : Box(s, pressed, sh.ControlRadius, 18, 8, soft, sh.ControlOutline);
        t.SetStylebox("normal", MenuButton, rest);
        t.SetStylebox("hover", MenuButton, lit);
        t.SetStylebox("pressed", MenuButton, down);
        t.SetStylebox("hover_pressed", MenuButton, down);
        t.SetStylebox("focus", MenuButton, new StyleBoxEmpty());
        t.SetStylebox("disabled", MenuButton, rest);
        t.SetFont("font", MenuButton, sh.MenuFill ? display : bold);
        t.SetColor("font_color", MenuButton, ink);
        foreach (string c in new[] { "font_focus_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
            t.SetColor(c, MenuButton, sh.MenuFill ? onAccent : ink);
        // the filled entry is its own marker: no bar at its edge
        t.SetColor("accent_bar", MenuButton, sh.MenuFill ? new Color(accent, 0) : accent);
        if (sh.MenuFill) t.SetStylebox("focus", MenuButton, lit);

        t.SetTypeVariation(Primary, "Button");
        t.SetStylebox("normal", Primary, Box(s, accent, sh.ControlRadius, 18, 8, edge, sh.ControlOutline, shadow: sh.ControlShadow, slant: true));
        t.SetStylebox("hover", Primary, Box(s, accent.Lightened(0.12f), sh.ControlRadius, 18, 8, edge, sh.ControlOutline, shadow: sh.ControlShadow, slant: true));
        t.SetStylebox("pressed", Primary, Box(s, accent.Darkened(0.1f), sh.ControlRadius, 18, 8, edge, sh.ControlOutline, slant: true));
        t.SetStylebox("hover_pressed", Primary, Box(s, accent.Darkened(0.1f), sh.ControlRadius, 18, 8, edge, sh.ControlOutline, slant: true));
        t.SetStylebox("focus", Primary, Box(s, new Color(0, 0, 0, 0), sh.ControlRadius, 18, 8, ink, 2, slant: true));
        t.SetStylebox("disabled", Primary, Box(s, new Color(accent, 0.25f), sh.ControlRadius, 18, 8, slant: true));
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

        // a page's tabs: a slanted tab fills with the accent; a square skin underlines the chosen one
        t.SetTypeVariation(Tab, "Button");
        var tabRest = Box(s, new Color(0, 0, 0, 0), 6, 14, 6, slant: true);
        var tabOn = sh.Skew > 0 ? Box(s, accent, 0, 14, 6, slant: true) : UiTheme.Flat(new Color(accent, 0.12f), 6, 14, 6);
        if (sh.Skew <= 0) { tabOn.BorderColor = accent; tabOn.BorderWidthBottom = 3; }
        t.SetStylebox("normal", Tab, tabRest);
        t.SetStylebox("hover", Tab, Box(s, new Color(accent, 0.10f), sh.Skew > 0 ? 0 : 6, 14, 6, slant: true));
        t.SetStylebox("pressed", Tab, tabOn);
        t.SetStylebox("hover_pressed", Tab, tabOn);
        t.SetStylebox("focus", Tab, Box(s, new Color(0, 0, 0, 0), 6, 14, 6, new Color(accent, 0.7f), 1, slant: true));
        t.SetColor("font_color", Tab, C(s, "inkDim"));
        t.SetColor("font_hover_color", Tab, ink);
        t.SetColor("font_focus_color", Tab, ink);
        t.SetColor("font_pressed_color", Tab, sh.Skew > 0 ? onAccent : accent);
        t.SetColor("font_hover_pressed_color", Tab, sh.Skew > 0 ? onAccent : accent);
        if (sh.Skew > 0) t.SetFont("font", Tab, display);

        t.SetTypeVariation(Card, "PanelContainer");
        t.SetStylebox("panel", Card, Box(s, card, sh.Radius, 18, 18, edge, sh.Outline, shadow: sh.ControlShadow));
        t.SetTypeVariation(Page, "PanelContainer");
        t.SetStylebox("panel", Page, Box(s, card, sh.Radius + 4, 26, 26, edge, sh.Outline, shadow: true));

        t.SetConstant("hover_scale_pct", JuiceType, Mathf.RoundToInt(sh.HoverScale * 100));
        t.SetConstant("hover_tilt_tenths", JuiceType, Mathf.RoundToInt(sh.HoverTilt * 10));
    }
}
