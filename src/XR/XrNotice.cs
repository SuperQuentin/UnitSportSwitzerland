using Godot;
using UnitSport.Ui;

namespace UnitSport.XR;

/// <summary>
/// A one-line VR notice such as "Recentred" or "Monitor view  Both eyes". It floats at the top
/// centre in the menus' floating-text style (no box, an outline and a shadow;
/// docs/notes/ui/style-guide.md), with the value that changed in amber, then fades. It is a
/// CanvasLayer, so it shows both on the monitor and on the headset's panel.
/// </summary>
public partial class XrNotice : CanvasLayer
{
    private const float Hold = 1.6f, FadeIn = 0.2f, FadeOut = 1.0f;

    private HBoxContainer _line = null!;
    private Label _text = null!, _accent = null!;
    private float _age = float.MaxValue;

    public override void _Ready()
    {
        Name = "XrNotice";
        Layer = 39;   // over the HUD, under the menus (40) and the controls overlay (42)
        ProcessMode = ProcessModeEnum.Always;

        _line = UiKit.HBox(10);
        _line.Alignment = BoxContainer.AlignmentMode.Center;
        _line.MouseFilter = Control.MouseFilterEnum.Ignore;
        _line.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _line.OffsetTop = 72;
        _line.OffsetBottom = 110;
        _line.Modulate = new Color(1, 1, 1, 0);
        AddChild(_line);
        _text = Floating(UiTheme.Text);
        _accent = Floating(UiTheme.Amber);
        _line.AddChild(_text);
        _line.AddChild(_accent);
    }

    /// <summary>Text over the world: no box, an outline and a soft shadow (style guide, step 6).</summary>
    private static Label Floating(Color color)
    {
        var l = UiKit.Text("", UiTheme.FontHeading, color, bold: true);
        l.AddThemeConstantOverride("outline_size", 4);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.55f));
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.45f));
        l.AddThemeConstantOverride("shadow_offset_x", 1);
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        l.AddThemeConstantOverride("shadow_outline_size", 6);
        return l;
    }

    /// <summary>Shows <paramref name="text"/>, then <paramref name="value"/> in amber if given.</summary>
    public void Show(string text, string? value = null)
    {
        _text.Text = text;
        _accent.Text = value ?? "";
        _accent.Visible = value != null;
        _age = 0f;
    }

    public override void _Process(double delta)
    {
        if (_age > Hold + FadeOut) return;
        _age += (float)delta;
        float a = _age < FadeIn ? _age / FadeIn
            : _age < Hold ? 1f
            : Mathf.Clamp(1f - (_age - Hold) / FadeOut, 0f, 1f);
        _line.Modulate = new Color(1, 1, 1, a);
    }
}
