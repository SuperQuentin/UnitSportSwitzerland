using Godot;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// A body's map in the place search (#2039): its equirectangular photo (the sky's own maps: the Moon's LROC mosaic, the
/// planets' 2k maps; a moon with none in its tint over a 30° grid), longitude 0 in the middle and east to the right,
/// each place a dot, you a ring when you are on it. The pointer over a dot names it (<see cref="Hovered"/>), a click on
/// it is <see cref="Picked"/>, a click anywhere else <see cref="PickedAt"/> that latitude and longitude. Drawn only when
/// something changes.
/// </summary>
public partial class BodyMapView : Control
{
    /// <summary>The map's width, px (its height half of it).</summary>
    public const float Width = 420;

    /// <summary>A dot within this of the pointer is the one under it, px.</summary>
    private const float PickPx = 9;

    private Texture2D? _photo;
    private Color _tint = new(0.5f, 0.5f, 0.5f);
    private System.Collections.Generic.List<BodyPlace> _places = new();
    private (double Lat, double Lon)? _here;
    private int _hover = -1;

    /// <summary>A place's index in the list shown.</summary>
    public event System.Action<int>? Picked;

    /// <summary>A spot with no place: latitude and east longitude, degrees.</summary>
    public event System.Action<double, double>? PickedAt;

    /// <summary>The name under the pointer, or null.</summary>
    public event System.Action<string?>? Hovered;

    public BodyMapView()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.Cross;
    }

    /// <summary>The body whose map to draw.</summary>
    public void SetBody(BodyId body)
    {
        var sky = CelestialBody.Of(body).Sky;
        string? path = body == BodyId.Moon ? World.MoonDisc.MapPath : sky is { } s ? World.SkyBodies.MapOf(s) : null;
        _photo = path != null ? GD.Load<Texture2D>(path) : null;
        _tint = sky is { } t ? World.SkyBodies.TintOf(t) : new Color(0.6f, 0.6f, 0.6f);
        _hover = -1;
        QueueRedraw();
    }

    /// <summary>The places to mark (the list's, in its order) and where you are on this body, if you are.</summary>
    public void Show(System.Collections.Generic.List<BodyPlace> places, Geodetic? here)
    {
        _places = places;
        _here = here is { } g ? (g.LatDeg, g.LonDeg) : null;
        _hover = -1;
        QueueRedraw();
    }

    private Vector2 ToMap(double lat, double lon)
    {
        double l = ((lon + 180) % 360 + 360) % 360 - 180;
        return new Vector2((float)((l + 180) / 360 * Size.X), (float)((90 - lat) / 180 * Size.Y));
    }

    private (double Lat, double Lon) FromMap(Vector2 p) =>
        (90 - p.Y / Size.Y * 180, p.X / Size.X * 360 - 180);

    private int Nearest(Vector2 at)
    {
        int best = -1;
        float bestD = PickPx * PickPx;
        for (int i = 0; i < _places.Count; i++)
        {
            float d = ToMap(_places[i].LatDeg, _places[i].LonDeg).DistanceSquaredTo(at);
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion m)
        {
            int h = Nearest(m.Position);
            if (h != _hover)
            {
                _hover = h;
                Hovered?.Invoke(h >= 0 ? PlaceSearchUi.Describe(_places[h]) : null);
                QueueRedraw();
            }
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b)
        {
            int i = Nearest(b.Position);
            if (i >= 0) Picked?.Invoke(i);
            else
            {
                var (lat, lon) = FromMap(b.Position);
                PickedAt?.Invoke(System.Math.Clamp(lat, -89.5, 89.5), lon);
            }
            AcceptEvent();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && _hover >= 0)
        {
            _hover = -1;
            Hovered?.Invoke(null);
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        if (_photo != null) DrawTextureRect(_photo, rect, false);
        else
        {
            DrawRect(rect, _tint * new Color(0.55f, 0.55f, 0.55f));
            var line = new Color(1, 1, 1, 0.12f);
            for (int lon = -150; lon <= 150; lon += 30) DrawLine(ToMap(90, lon), ToMap(-90, lon), line);
            for (int lat = -60; lat <= 60; lat += 30) DrawLine(ToMap(lat, -180), ToMap(lat, 179.999), line);
        }
        DrawRect(rect, UiTheme.Hairline, false, 1);
        var shade = new Color(0, 0, 0, 0.75f);
        for (int i = 0; i < _places.Count; i++)
        {
            if (i == _hover) continue;
            var p = ToMap(_places[i].LatDeg, _places[i].LonDeg);
            DrawCircle(p, 4.5f, shade);
            DrawCircle(p, 3f, UiTheme.Text);
        }
        if (_here is { } here)
        {
            var p = ToMap(here.Lat, here.Lon);
            DrawArc(p, 7f, 0, Mathf.Tau, 24, shade, 4f);
            DrawArc(p, 7f, 0, Mathf.Tau, 24, UiTheme.Good, 2f);
        }
        if (_hover >= 0)
        {
            var p = ToMap(_places[_hover].LatDeg, _places[_hover].LonDeg);
            DrawCircle(p, 7f, shade);
            DrawCircle(p, 5f, UiTheme.Amber);
            var font = UiTheme.Bold;
            string name = _places[_hover].Name;
            var size = font.GetStringSize(name, HorizontalAlignment.Left, -1, UiTheme.FontSmall);
            float x = Mathf.Clamp(p.X - size.X / 2, 4, Size.X - size.X - 4);
            float y = p.Y > 30 ? p.Y - 12 : p.Y + 12 + size.Y;
            DrawRect(new Rect2(x - 5, y - size.Y + 1, size.X + 10, size.Y + 4), new Color(0, 0, 0, 0.7f));
            DrawString(font, new Vector2(x, y), name, HorizontalAlignment.Left, -1, UiTheme.FontSmall, UiTheme.Amber);
        }
    }
}
