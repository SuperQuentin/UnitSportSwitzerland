using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Player;

/// <summary>
/// The spectator camera's speed in the bottom-right corner under its key hints (#1796, #1798), shown when it changes (a notch, a held
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
        // the bottom-right corner under the key hints (#1798), where a vehicle's readout stands (PlayerFeel's engine
        // line; the prompt bar leaves it 64 px): next to its own Faster / Slower rows. The top right is the perf and
        // position overlay's (F3), the top centre the compass's, the bottom left the chat's.
        var column = UiKit.VBox(0);
        column.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        column.OffsetLeft = column.OffsetRight = -18f;
        column.OffsetTop = column.OffsetBottom = -12f;
        column.GrowHorizontal = Control.GrowDirection.Begin;
        column.GrowVertical = Control.GrowDirection.Begin;
        var align = HorizontalAlignment.Right;
        if (Platform.IsTouch)
        {
            // a phone's pedals and pad take the bottom right (#1043): top centre, as the vehicle's readout
            column.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            column.OffsetLeft = column.OffsetRight = 0f;
            column.OffsetTop = column.OffsetBottom = 16f;
            column.GrowHorizontal = Control.GrowDirection.Both;
            column.GrowVertical = Control.GrowDirection.End;
            align = HorizontalAlignment.Center;
        }
        root.AddChild(column);
        _column = column;

        // the note over the speed: the speed keeps the corner's line, the note grows up toward the hints
        column.AddChild(_note = Floating(UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, align: align)));
        column.AddChild(_speed = Floating(UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true, align: align)));
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
