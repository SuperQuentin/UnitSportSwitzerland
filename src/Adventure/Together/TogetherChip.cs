using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Adventure.Together;

/// <summary>
/// "Together ×2" (#1266): a small amber line beside the stamina ring's place, low right of the crosshair, while
/// party friends are near (<see cref="TogetherService.NearHere"/>), hidden at 0, in a match and when the setting
/// (<see cref="GameSettings.TogetherChip"/>) is off. Text over the world: no box, an outline and a shadow
/// (<c>ui/style-guide</c>). Its text changes only on <see cref="TogetherService.NearChanged"/>; nothing per frame.
/// </summary>
public sealed partial class TogetherChip : CanvasLayer
{
    private Label _label = null!;
    private TogetherService? _service;

    /// <summary>What it shows now ("" when hidden), for the checks.</summary>
    public string Shown => _label.Visible ? _label.Text : "";

    public override void _Ready()
    {
        Name = "TogetherChip";
        Layer = 8;
        _label = new Label
        {
            Theme = UiTheme.Get(),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _label.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        _label.AddThemeFontOverride("font", UiTheme.Bold);
        _label.AddThemeColorOverride("font_color", UiTheme.Amber);
        _label.AddThemeConstantOverride("outline_size", 4);
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.55f));
        _label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.45f));
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 2);
        _label.AddThemeConstantOverride("shadow_outline_size", 6);
        // beside where the first-person stamina ring sits (centre + 58, 66): right of it, the same height
        _label.SetAnchorsPreset(Control.LayoutPreset.Center);
        _label.Position = new Vector2(80f, 56f);
        AddChild(_label);
    }

    public override void _Process(double delta)
    {
        // the service comes and goes with the world: hook it when it is there
        var s = TogetherService.Instance;
        // at a shared pot its panel is the HUD
        bool atPot = s?.MyPot != null;
        if (atPot && _label.Visible) _label.Visible = false;
        else if (!atPot && !_label.Visible && s != null && s.NearHere > 0 && GameSettings.Current.TogetherChip && !Permissions.InMatch) _label.Visible = true;
        if (s == _service) return;
        if (_service != null && IsInstanceValid(_service)) _service.NearChanged -= OnNear;
        _service = s;
        if (s != null) s.NearChanged += OnNear;
        OnNear(s?.NearHere ?? 0);
    }

    private void OnNear(int n)
    {
        bool show = n > 0 && GameSettings.Current.TogetherChip && !Permissions.InMatch;
        _label.Text = TogetherRules.ChipText(n);
        _label.Visible = show;
    }
}
