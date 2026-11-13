using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Player;

/// <summary>
/// The spectator camera's speed in the top-right corner (#1796), shown when it changes (a notch, a held
/// button's ramp, boost pressed or let go) and faded <see cref="HoldSeconds"/> after the last change: nothing on
/// screen unless in use (<c>ui/style-guide</c>). Floating text, no box; the text is set on a change only and the
/// fade runs only while it shows (<c>general/perf-no-per-frame-allocations</c>).
/// </summary>
public partial class SpectatorSpeedHud : CanvasLayer
{
    /// <summary>How long the speed stays after the last change, then how long it fades, s.</summary>
    public const double HoldSeconds = 1.5, FadeSeconds = 0.6, FadeInSeconds = 0.15;

    private Label _speed = null!, _note = null!;
    private Control _column = null!;
    private double _shownFor;

    public override void _Ready()
    {
        Name = "SpectatorSpeedHud";
        Layer = 9;
        var root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        // the top-right corner: the spectator's key hints have the bottom-right (with Faster / Slower), the chat the
        // bottom-left
        var column = UiKit.VBox(0);
        column.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AnchorLeft = column.AnchorRight = 1f;
        column.OffsetLeft = column.OffsetRight = -16f;
        column.OffsetTop = column.OffsetBottom = 16f;
        column.GrowHorizontal = Control.GrowDirection.Begin;
        root.AddChild(column);
        _column = column;

        column.AddChild(_speed = Floating(UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true, align: HorizontalAlignment.Right)));
        column.AddChild(_note = Floating(UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, align: HorizontalAlignment.Right)));
        _column.Modulate = Colors.Transparent;
        SetProcess(false);
    }

    /// <summary>Over snow and sky as over a night: the floating text's outline and shadow (<c>ui/style-guide</c>).</summary>
    private static Label Floating(Label label)
    {
        label.AddThemeConstantOverride("outline_size", 4);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.55f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.45f));
        label.AddThemeConstantOverride("shadow_offset_x", 1);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        label.AddThemeConstantOverride("shadow_outline_size", 6);
        return label;
    }

    /// <param name="speed">The speed flown, boost included, m/s.</param>
    /// <param name="limited">The speed asked for is more than the altitude allows: climbing goes faster.</param>
    public void Show(float speed, bool boosted, bool limited)
    {
        if (!IsNodeReady()) return;
        _speed.Text = "Spectator " + SpectatorSpeed.Format(speed) + (boosted ? " · boost" : "");
        _note.Text = limited ? "climb to go faster" : "";
        _note.Visible = limited;
        // fading in from where a fade out had got to, so a quick second notch does not blink
        _shownFor = FadeInSeconds * _column.Modulate.A;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        _shownFor += delta;
        double fadeStart = FadeInSeconds + HoldSeconds;
        float alpha = _shownFor < FadeInSeconds ? (float)(_shownFor / FadeInSeconds)
            : _shownFor < fadeStart ? 1f
            : 1f - (float)((_shownFor - fadeStart) / FadeSeconds);
        if (alpha <= 0f)
        {
            alpha = 0f;
            SetProcess(false);
        }
        _column.Modulate = new Color(1, 1, 1, alpha);
    }
}
