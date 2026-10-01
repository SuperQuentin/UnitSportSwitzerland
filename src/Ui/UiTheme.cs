using Godot;

namespace UnitSport.Ui;

/// <summary>
/// The one look of every menu: dark glass panels, thin hairlines, an amber accent. Built in code
/// once and shared, so no menu carries its own copy of a stylebox again.
///
/// <para>
/// Set on the menu roots only (title, solo, multiplayer, settings, pause, loading, modals). The
/// in-game HUDs keep the stock look: putting this on the root Window would restyle and resize
/// every one of them at once.
/// </para>
/// </summary>
public static class UiTheme
{
    public static readonly Color Amber = new(0.98f, 0.72f, 0.10f);
    public static readonly Color AmberDim = new(0.98f, 0.72f, 0.10f, 0.35f);
    public static readonly Color Text = new(0.90f, 0.92f, 0.95f);
    public static readonly Color TextDim = new(0.58f, 0.62f, 0.68f);
    public static readonly Color TextFaint = new(0.42f, 0.46f, 0.52f);
    public static readonly Color Hairline = new(1, 1, 1, 0.08f);
    public static readonly Color Good = new(0.36f, 0.82f, 0.48f);
    public static readonly Color Warn = new(0.95f, 0.66f, 0.22f);
    public static readonly Color Bad = new(0.93f, 0.36f, 0.33f);
    public static readonly Color Glass = new(0.055f, 0.065f, 0.085f, 0.82f);

    public const int FontBody = 15, FontSmall = 13, FontTiny = 11, FontHeading = 22, FontTitle = 44;

    private static Theme? _theme;
    private static Font? _font, _bold;

    /// <summary>A modern sans the machine already has: no font file shipped, no licence to carry.</summary>
    public static Font Font => _font ??= new SystemFont
    {
        FontNames = new[] { "Inter", "Segoe UI Variable Text", "Segoe UI", "SF Pro Text", "Roboto", "Noto Sans", "Helvetica Neue", "Arial" },
        Antialiasing = TextServer.FontAntialiasing.Gray,
        SubpixelPositioning = TextServer.SubpixelPositioning.Auto,
    };

    public static Font Bold => _bold ??= new SystemFont
    {
        FontNames = new[] { "Inter", "Segoe UI Variable Display", "Segoe UI", "SF Pro Display", "Roboto", "Noto Sans", "Helvetica Neue", "Arial" },
        FontWeight = 650,
        Antialiasing = TextServer.FontAntialiasing.Gray,
    };

    /// <summary>A rounded glass panel.</summary>
    public static StyleBoxFlat GlassPanel(float alpha = 0.82f, int radius = 12, int margin = 22)
    {
        var s = new StyleBoxFlat
        {
            BgColor = new Color(Glass, alpha),
            BorderColor = new Color(1, 1, 1, 0.07f),
            ShadowColor = new Color(0, 0, 0, 0.38f),
            ShadowSize = 22,
            ContentMarginLeft = margin, ContentMarginRight = margin,
            ContentMarginTop = margin, ContentMarginBottom = margin,
            AntiAliasing = true,
        };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(radius);
        return s;
    }

    public static StyleBoxFlat Flat(Color bg, int radius = 8, int marginH = 14, int marginV = 8, Color? border = null, int borderWidth = 0)
    {
        var s = new StyleBoxFlat
        {
            BgColor = bg,
            ContentMarginLeft = marginH, ContentMarginRight = marginH,
            ContentMarginTop = marginV, ContentMarginBottom = marginV,
            AntiAliasing = true,
        };
        s.SetCornerRadiusAll(radius);
        if (border is { } b) { s.BorderColor = b; s.SetBorderWidthAll(borderWidth); }
        return s;
    }

    public static Theme Get()
    {
        if (_theme != null) return _theme;
        var t = new Theme { DefaultFont = Font, DefaultFontSize = FontBody };

        // --- labels
        t.SetColor("font_color", "Label", Text);
        t.SetConstant("line_spacing", "Label", 2);

        // --- panels
        t.SetStylebox("panel", "PanelContainer", GlassPanel());
        t.SetStylebox("panel", "Panel", GlassPanel());

        // --- buttons: transparent until hovered; focus is an amber outline, so a pad always sees it
        var focus = Flat(new Color(0, 0, 0, 0), 8, 14, 8, Amber, 1);
        foreach (string type in new[] { "Button", "OptionButton", "CheckButton", "CheckBox", "MenuButton" })
        {
            t.SetStylebox("normal", type, Flat(new Color(1, 1, 1, 0.045f)));
            t.SetStylebox("hover", type, Flat(new Color(1, 1, 1, 0.09f)));
            t.SetStylebox("pressed", type, Flat(new Color(1, 1, 1, 0.13f)));
            t.SetStylebox("hover_pressed", type, Flat(new Color(1, 1, 1, 0.13f)));
            t.SetStylebox("disabled", type, Flat(new Color(1, 1, 1, 0.02f)));
            t.SetStylebox("focus", type, focus);
            t.SetColor("font_color", type, Text);
            t.SetColor("font_hover_color", type, Colors.White);
            t.SetColor("font_pressed_color", type, Amber);
            t.SetColor("font_hover_pressed_color", type, Amber);
            t.SetColor("font_focus_color", type, Colors.White);
            t.SetColor("font_disabled_color", type, TextFaint);
            t.SetConstant("h_separation", type, 10);
        }
        // toggles read as rows, not boxes
        foreach (string type in new[] { "CheckButton", "CheckBox" })
        {
            var clear = Flat(new Color(0, 0, 0, 0), 8, 4, 4);
            t.SetStylebox("normal", type, clear);
            t.SetStylebox("pressed", type, clear);
            t.SetStylebox("hover", type, Flat(new Color(1, 1, 1, 0.05f), 8, 4, 4));
            t.SetStylebox("hover_pressed", type, Flat(new Color(1, 1, 1, 0.05f), 8, 4, 4));
        }
        t.SetIcon("checked", "CheckButton", Pill(true));
        t.SetIcon("unchecked", "CheckButton", Pill(false));
        t.SetIcon("checked_disabled", "CheckButton", Pill(true));
        t.SetIcon("unchecked_disabled", "CheckButton", Pill(false));
        t.SetIcon("checked", "CheckBox", Tick(true));
        t.SetIcon("unchecked", "CheckBox", Tick(false));

        // --- dropdown and its popup
        t.SetIcon("arrow", "OptionButton", Chevron());
        var popup = GlassPanel(0.97f, 10, 6);
        popup.ShadowSize = 14;
        t.SetStylebox("panel", "PopupMenu", popup);
        t.SetStylebox("hover", "PopupMenu", Flat(new Color(Amber, 0.16f), 6, 10, 4));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Colors.White);
        t.SetConstant("v_separation", "PopupMenu", 8);
        t.SetConstant("item_start_padding", "PopupMenu", 10);
        t.SetIcon("radio_checked", "PopupMenu", Dot(Amber, 8));
        t.SetIcon("radio_unchecked", "PopupMenu", Dot(new Color(1, 1, 1, 0), 8));
        t.SetIcon("checked", "PopupMenu", Dot(Amber, 8));
        t.SetIcon("unchecked", "PopupMenu", Dot(new Color(1, 1, 1, 0), 8));

        // --- text fields: an underline, brighter and amber when focused
        var field = Flat(new Color(1, 1, 1, 0.05f), 6, 12, 8);
        var fieldFocus = Flat(new Color(1, 1, 1, 0.07f), 6, 12, 8, Amber, 1);
        t.SetStylebox("normal", "LineEdit", field);
        t.SetStylebox("focus", "LineEdit", fieldFocus);
        t.SetStylebox("read_only", "LineEdit", field);
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", TextFaint);
        t.SetColor("caret_color", "LineEdit", Amber);
        t.SetColor("selection_color", "LineEdit", new Color(Amber, 0.3f));

        // --- sliders: a 4 px track, amber fill, round grabber
        var track = Flat(new Color(1, 1, 1, 0.10f), 3, 0, 2);
        var fill = Flat(Amber, 3, 0, 2);
        t.SetStylebox("slider", "HSlider", track);
        t.SetStylebox("grabber_area", "HSlider", fill);
        t.SetStylebox("grabber_area_highlight", "HSlider", fill);
        t.SetIcon("grabber", "HSlider", Dot(Colors.White, 14));
        t.SetIcon("grabber_highlight", "HSlider", Dot(Amber, 16));
        t.SetIcon("grabber_disabled", "HSlider", Dot(TextFaint, 14));
        t.SetIcon("tick", "HSlider", new ImageTexture());
        t.SetConstant("center_grabber", "HSlider", 1);

        // --- scrollbars: thin, no buttons
        foreach (string type in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", type, Flat(new Color(1, 1, 1, 0.03f), 3, 2, 2));
            t.SetStylebox("scroll_focus", type, Flat(new Color(1, 1, 1, 0.03f), 3, 2, 2));
            t.SetStylebox("grabber", type, Flat(new Color(1, 1, 1, 0.18f), 3, 2, 2));
            t.SetStylebox("grabber_highlight", type, Flat(new Color(1, 1, 1, 0.30f), 3, 2, 2));
            t.SetStylebox("grabber_pressed", type, Flat(new Color(Amber, 0.6f), 3, 2, 2));
        }
        t.SetStylebox("panel", "ScrollContainer", new StyleBoxEmpty());

        // --- separators: a hairline
        var line = new StyleBoxLine { Color = Hairline, Thickness = 1 };
        t.SetStylebox("separator", "HSeparator", line);
        t.SetConstant("separation", "HSeparator", 12);
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Hairline, Thickness = 1, Vertical = true });

        // --- progress bar
        t.SetStylebox("background", "ProgressBar", Flat(new Color(1, 1, 1, 0.08f), 3, 0, 2));
        t.SetStylebox("fill", "ProgressBar", Flat(Amber, 3, 0, 2));
        t.SetColor("font_color", "ProgressBar", new Color(0, 0, 0, 0));

        // --- tooltips
        t.SetStylebox("panel", "TooltipPanel", Flat(new Color(0.04f, 0.05f, 0.06f, 0.96f), 6, 10, 6, Hairline, 1));
        t.SetColor("font_color", "TooltipLabel", Text);

        _theme = t;
        return t;
    }

    // ---- small textures drawn at startup: no image assets -------------------------------------

    /// <summary>The on/off pill of a toggle.</summary>
    private static ImageTexture Pill(bool on)
    {
        const int w = 38, h = 22;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var track = on ? Amber : new Color(1, 1, 1, 0.16f);
        var knob = on ? new Color(0.08f, 0.08f, 0.1f) : new Color(0.85f, 0.87f, 0.9f);
        float r = h / 2f;
        float knobX = on ? w - r : r;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                // the capsule: distance to its centre segment
                float cx = Mathf.Clamp(px, r, w - r);
                float d = new Vector2(px - cx, py - r).Length();
                float a = Mathf.Clamp(r - d, 0, 1);
                var c = new Color(track, track.A * a);
                float dk = new Vector2(px - knobX, py - r).Length();
                float ak = Mathf.Clamp(r - 3 - dk, 0, 1);
                if (ak > 0) c = c.Lerp(knob, ak) with { A = Mathf.Max(c.A, ak) };
                img.SetPixel(x, y, c);
            }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>A rounded square tick box.</summary>
    private static ImageTexture Tick(bool on)
    {
        const int s = 18;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float qx = Mathf.Max(Mathf.Abs(px - s / 2f) - (s / 2f - 4), 0);
                float qy = Mathf.Max(Mathf.Abs(py - s / 2f) - (s / 2f - 4), 0);
                float d = new Vector2(qx, qy).Length() - 3.5f;
                float a = Mathf.Clamp(0.5f - d, 0, 1);
                float border = Mathf.Clamp(0.5f - Mathf.Abs(d + 0.75f) + 0.75f, 0, 1);
                Color c = on ? new Color(Amber, a) : new Color(1, 1, 1, 0.35f * border * a);
                img.SetPixel(x, y, c);
            }
        if (on)
        {
            // the tick itself
            var dark = new Color(0.08f, 0.08f, 0.1f);
            void Seg(Vector2 a, Vector2 b)
            {
                for (float t = 0; t <= 1; t += 0.02f)
                {
                    var p = a.Lerp(b, t);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = (int)p.X + dx, y = (int)p.Y + dy;
                            if (x >= 0 && y >= 0 && x < s && y < s && Mathf.Abs(dx) + Mathf.Abs(dy) < 2) img.SetPixel(x, y, dark);
                        }
                }
            }
            Seg(new Vector2(4.5f, 9.5f), new Vector2(7.5f, 12.5f));
            Seg(new Vector2(7.5f, 12.5f), new Vector2(13.5f, 5.5f));
        }
        return ImageTexture.CreateFromImage(img);
    }

    private static readonly Dictionary<(Color, int), ImageTexture> Dots = new();

    /// <summary>An anti-aliased filled circle (cached: status dots are re-set on every refresh).</summary>
    public static ImageTexture Dot(Color color, int size)
    {
        if (Dots.TryGetValue((color, size), out var cached)) return cached;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x + 0.5f - r, y + 0.5f - r).Length();
                img.SetPixel(x, y, new Color(color, color.A * Mathf.Clamp(r - d, 0, 1)));
            }
        return Dots[(color, size)] = ImageTexture.CreateFromImage(img);
    }

    /// <summary>The dropdown's down chevron.</summary>
    private static ImageTexture Chevron()
    {
        const int w = 12, h = 8;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                // distance to the two strokes of a "v"
                float d1 = DistSeg(new Vector2(px, py), new Vector2(1.5f, 2), new Vector2(6, 6.5f));
                float d2 = DistSeg(new Vector2(px, py), new Vector2(6, 6.5f), new Vector2(10.5f, 2));
                float a = Mathf.Clamp(1.4f - Mathf.Min(d1, d2), 0, 1);
                img.SetPixel(x, y, new Color(TextDim, a));
            }
        return ImageTexture.CreateFromImage(img);
    }

    internal static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return (p - (a + ab * t)).Length();
    }
}
