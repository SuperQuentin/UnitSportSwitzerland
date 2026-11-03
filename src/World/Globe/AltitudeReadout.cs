using System.Globalization;
using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// The camera's altitude on screen, for the street-to-Earth pull-back film (#1430 H4,
/// docs/plans/ellipsoid-handover.md "Showcase"): bottom left, metres to 10 km, kilometres above,
/// read through the frame (<see cref="WorldOrigin.AltitudeOf"/>: in Space Y is not the altitude).
/// A film overlay, not the HUD: shown only with <c>--altitude-readout</c>, even with <c>--nohud</c>.
/// </summary>
public partial class AltitudeReadout : CanvasLayer
{
    public static bool Requested => CmdArgs.Has("--altitude-readout");

    public WorldOrigin? Origin { get; set; }

    private Label _label = null!;
    private string _shown = "";

    public override void _Ready()
    {
        Layer = 90;
        AddToGroup(ShotRunner.FilmOverlay);
        _label = new Label
        {
            AnchorLeft = 0, AnchorTop = 1, AnchorRight = 0, AnchorBottom = 1,
            OffsetLeft = 40, OffsetTop = -110, OffsetRight = 900, OffsetBottom = -40,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        _label.AddThemeFontSizeOverride("font_size", 44);
        _label.AddThemeColorOverride("font_color", Colors.White);
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        _label.AddThemeConstantOverride("outline_size", 8);
        AddChild(_label);
    }

    public override void _Process(double delta)
    {
        if (Origin == null || GetViewport()?.GetCamera3D() is not { } cam) return;
        string text = Format(Origin.AltitudeOf(cam.GlobalPosition));
        if (text != _shown) _label.Text = _shown = text;
    }

    /// <summary>"12 m", "8,450 m", "12.4 km", "1,234 km", "12,000 km": whole metres to 10 km, then kilometres.</summary>
    public static string Format(double metres)
    {
        var inv = CultureInfo.InvariantCulture;
        if (metres < 10_000) return Math.Round(Math.Max(0, metres)).ToString("#,0", inv) + " m";
        double km = metres / 1000;
        return (km < 100 ? km.ToString("0.0", inv) : Math.Round(km).ToString("#,0", inv)) + " km";
    }
}
