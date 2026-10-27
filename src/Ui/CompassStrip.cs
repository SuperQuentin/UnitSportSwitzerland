using Godot;

namespace UnitSport.Ui;

/// <summary>How a marker is drawn on the strip.</summary>
public enum MarkerShape : byte
{
    /// <summary>A diamond (the tracked target, the safe zone, a ping).</summary>
    Diamond,
    /// <summary>A triangle pointing down (a waypoint, a mate).</summary>
    Down,
    /// <summary>A small dot (a place nearby).</summary>
    Dot,
    /// <summary>A pin's icon (<see cref="NavIcons"/>, <see cref="CompassMarker.Icon"/>).</summary>
    Icon,
    /// <summary>A place's kind glyph (<see cref="CompassMarker.Icon"/> is the <c>PoiKind</c>).</summary>
    Place,
}

/// <summary>
/// One marker on a compass strip. <see cref="Toward"/> is the direction from the viewer, (east, north) metres;
/// <see cref="Label"/> is built by the source only when its text changes (never per frame); <see cref="Row"/> the line
/// under the strip its label goes on.
/// </summary>
public struct CompassMarker
{
    public int Key;
    public Vector2 Toward;
    public Color Color;
    public MarkerShape Shape;
    public byte Row;
    public byte Icon;
    public string Label;
}

/// <summary>What a compass strip shows: the Battle Royale's (<c>BrCompass</c>) or free roam's (<c>AdventureCompass</c>).</summary>
public interface ICompassSource
{
    /// <summary>The strip is up now.</summary>
    bool Shown { get; }
    /// <summary>Which way the view looks, (east, north); null hides the strip this frame.</summary>
    Vector2? Heading { get; }
    /// <summary>Fills <paramref name="into"/> with the markers, most important first; returns how many.</summary>
    int Fill(CompassMarker[] into);
    /// <summary>Labels drop when two markers crowd each other, at most 8 are drawn (free roam); the BR draws all.</summary>
    bool Declutter { get; }
    /// <summary>The strip fades to 30 % after a while with nothing changing (free roam).</summary>
    bool Fades { get; }
}

/// <summary>
/// The compass strip at the top centre of the screen (#190, generalised in #1226): ticks every 5°, the bearing every
/// 15°, N/NE/E/... every 45°, all sliding as you turn, the exact bearing under the centre mark; markers ride on it and
/// are pinned to the edge, with a turn arrow, when behind. It knows nothing of the Battle Royale or the Adventure: an
/// <see cref="ICompassSource"/> says what to draw. Redrawn only when the heading moved by more than 0.2° or a marker
/// changed; tick and bearing texts come from static tables (<c>general/perf-no-per-frame-allocations</c>).
/// </summary>
public partial class CompassStrip : Control
{
    public const float Width = 560f, Height = 34f, Top = 8f;
    /// <summary>Degrees either side of the centre that the strip shows.</summary>
    protected const float HalfSpan = 80f;
    private const int MaxMarkers = 32;

    private static readonly string[] Cardinals = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    /// <summary>"0", "15", ... "345": the tick numbers.</summary>
    private static readonly string[] Ticks = BuildTicks();
    /// <summary>"0" .. "359": the bearing under the caret.</summary>
    private static readonly string[] Degrees = BuildDegrees();

    private readonly ICompassSource _source;
    private CompassMarker[] _markers = new CompassMarker[MaxMarkers], _previous = new CompassMarker[MaxMarkers];
    private int _count, _previousCount;
    private readonly float[] _xs = new float[MaxMarkers];
    private readonly bool[] _drawn = new bool[MaxMarkers], _labelled = new bool[MaxMarkers];
    private float _heading = float.NaN, _drawnHeading = float.NaN;
    private double _quietSince;

    /// <summary>Redraws so far (checks: standing still costs none).</summary>
    public int Redraws { get; private set; }
    /// <summary>The markers drawn last (checks).</summary>
    public ReadOnlySpan<CompassMarker> Markers => _markers.AsSpan(0, _count);
    /// <summary>The heading drawn last, degrees.</summary>
    public float DrawnHeading => _drawnHeading;

    public CompassStrip(ICompassSource source)
    {
        _source = source;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public CompassStrip() : this(null!) { }

    /// <summary>Bearing of a direction (east, north): 0 north, 90 east, clockwise (<see cref="CompassMath.Bearing"/>).</summary>
    public static float Bearing(Vector2 eastNorth) => CompassMath.Bearing(eastNorth);

    private static string[] BuildTicks()
    {
        var t = new string[24];
        for (int i = 0; i < 24; i++) t[i] = (i * 15).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return t;
    }

    private static string[] BuildDegrees()
    {
        var d = new string[360];
        for (int i = 0; i < 360; i++) d[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return d;
    }

    /// <summary>A marker's x on the strip at a heading (checks).</summary>
    public static (float X, bool Pinned) XOf(Vector2 toward, float heading) =>
        CompassMath.Place(CompassMath.Relative(Bearing(toward), heading), Width, HalfSpan);

    public override void _Process(double delta)
    {
        bool shown = _source != null && _source.Shown && _source.Heading is { } h && h.LengthSquared() > 1e-8f;
        Visible = shown;
        if (!shown) { _drawnHeading = float.NaN; return; }
        var size = GetViewportRect().Size;
        var pos = new Vector2((size.X - Width) * 0.5f, Top);
        if (Position != pos) Position = pos;
        if (Size.X != Width) Size = new Vector2(Width, Height + 26);
        _heading = Bearing(_source!.Heading!.Value);

        (_previous, _markers) = (_markers, _previous);
        _previousCount = _count;
        _count = Math.Min(_source.Fill(_markers), MaxMarkers);
        bool changed = _count != _previousCount || float.IsNaN(_drawnHeading)
            || Mathf.Abs(Mathf.Wrap(_heading - _drawnHeading, -180f, 180f)) > 0.2f;
        for (int i = 0; i < _count && !changed; i++)
        {
            ref var a = ref _markers[i];
            ref var b = ref _previous[i];
            changed = a.Key != b.Key || !ReferenceEquals(a.Label, b.Label) || a.Color != b.Color
                || Mathf.Abs(Mathf.Wrap(Bearing(a.Toward) - Bearing(b.Toward), -180f, 180f)) > 0.2f;
        }
        double now = Core.GameClock.Now;
        bool turned = !float.IsNaN(_drawnHeading) && Mathf.Abs(Mathf.Wrap(_heading - _drawnHeading, -180f, 180f)) > 20f;
        if (changed && (_count != _previousCount || turned)) _quietSince = now;
        if (_source.Fades)
        {
            float want = now - _quietSince > 6 ? 0.3f : 1f;
            var m = Modulate;
            if (!Mathf.IsEqualApprox(m.A, want)) Modulate = m with { A = Mathf.MoveToward(m.A, want, (float)delta * 1.5f) };
        }
        if (!changed) return;
        _drawnHeading = _heading;
        Redraws++;
        QueueRedraw();
    }

    /// <summary>Keeps the strip awake (a marker appeared, the player looked round): back to full alpha.</summary>
    public void Wake() => _quietSince = Core.GameClock.Now;

    public override void _Draw()
    {
        if (float.IsNaN(_drawnHeading)) return;
        var font = ThemeDB.FallbackFont;
        float heading = _drawnHeading;
        float ppd = Width / (HalfSpan * 2f);
        float mid = Width * 0.5f;

        // the band, fading out at both ends
        DrawRect(new Rect2(0, 0, Width, Height), new Color(0.05f, 0.05f, 0.08f, 0.55f));
        for (int deg = (int)Mathf.Floor((heading - HalfSpan) / 5f) * 5; deg <= heading + HalfSpan; deg += 5)
        {
            float x = mid + (deg - heading) * ppd;
            if (x < 0 || x > Width) continue;
            float a = 1f - Mathf.Clamp((Mathf.Abs(x - mid) - Width * 0.32f) / (Width * 0.18f), 0f, 1f);
            int d = (int)Mathf.PosMod(deg, 360);
            var col = new Color(1, 1, 1, a);
            if (d % 45 == 0)
            {
                string label = Cardinals[d / 45];
                var c = d == 0 ? new Color(1f, 0.35f, 0.3f, a) : col;
                float w = font.GetStringSize(label, HorizontalAlignment.Left, -1, label.Length == 1 ? 17 : 14).X;
                DrawString(font, new Vector2(x - w * 0.5f, 22), label, HorizontalAlignment.Left, -1, label.Length == 1 ? 17 : 14, c);
                DrawLine(new Vector2(x, Height - 7), new Vector2(x, Height), c, 2f);
            }
            else if (d % 15 == 0)
            {
                string label = Ticks[d / 15];
                float w = font.GetStringSize(label, HorizontalAlignment.Left, -1, 11).X;
                DrawString(font, new Vector2(x - w * 0.5f, 20), label, HorizontalAlignment.Left, -1, 11, col with { A = a * 0.8f });
                DrawLine(new Vector2(x, Height - 6), new Vector2(x, Height), col, 1.5f);
            }
            else DrawLine(new Vector2(x, Height - 4), new Vector2(x, Height), col with { A = a * 0.6f }, 1f);
        }

        // the markers, in the source's order (later ones drawn over earlier ones)
        int n = _count;
        for (int i = 0; i < n; i++) _xs[i] = XOf(_markers[i].Toward, heading).X;
        if (_source.Declutter) CompassMath.Declutter(_xs.AsSpan(0, n), _drawn.AsSpan(0, n), _labelled.AsSpan(0, n));
        else for (int i = 0; i < n; i++) _drawn[i] = _labelled[i] = true;
        for (int i = 0; i < n; i++)
            if (_drawn[i]) Marker(font, heading, in _markers[i], _labelled[i]);

        // the centre mark and the exact bearing under it
        DrawColoredPolygon(new[] { new Vector2(mid, Height - 2), new Vector2(mid - 6, Height + 7), new Vector2(mid + 6, Height + 7) }, Colors.White);
        string b = Degrees[(int)Mathf.Round(heading) % 360];
        float bw = font.GetStringSize(b, HorizontalAlignment.Left, -1, 14).X;
        DrawRect(new Rect2(mid - bw * 0.5f - 6, Height + 8, bw + 12, 18), new Color(0.05f, 0.05f, 0.08f, 0.7f));
        DrawString(font, new Vector2(mid - bw * 0.5f, Height + 22), b, HorizontalAlignment.Left, -1, 14, Colors.White);
    }

    private void Marker(Font font, float heading, in CompassMarker m, bool labelled)
    {
        if (m.Toward.LengthSquared() < 1f) return;
        float rel = CompassMath.Relative(Bearing(m.Toward), heading);
        var (x, pinned) = CompassMath.Place(rel, Width, HalfSpan);
        float y = 6f;
        var color = m.Color;
        switch (m.Shape)
        {
            case MarkerShape.Diamond:
                DrawColoredPolygon(new[] { new Vector2(x, y - 5), new Vector2(x + 6, y + 1), new Vector2(x, y + 7), new Vector2(x - 6, y + 1) }, color);
                break;
            case MarkerShape.Down:
                DrawColoredPolygon(new[] { new Vector2(x, y + 8), new Vector2(x - 5, y - 2), new Vector2(x + 5, y - 2) }, color);
                break;
            case MarkerShape.Dot:
                DrawCircle(new Vector2(x, y + 1), 3f, color);
                break;
            case MarkerShape.Icon:
                DrawCircle(new Vector2(x, y + 1), 7.5f, new Color(0, 0, 0, 0.55f));
                NavIcons.Draw(this, m.Icon, new Vector2(x, y + 1), 6f, color);
                break;
            case MarkerShape.Place:
                DrawCircle(new Vector2(x, y + 1), 6.5f, color with { A = color.A * 0.5f });
                Adventure.Places.PlaceCard.Glyph(this, (Terrain.Format.PoiKind)m.Icon, new Vector2(x, y + 1), 4.5f, color);
                break;
        }
        if (pinned)
        {
            // behind you: an arrow on the edge says which way to turn
            float s = Mathf.Sign(rel);
            DrawColoredPolygon(new[] { new Vector2(x + s * 14, y + 1), new Vector2(x + s * 8, y - 4), new Vector2(x + s * 8, y + 6) }, color);
        }
        if (!labelled || string.IsNullOrEmpty(m.Label)) return;
        float w = font.GetStringSize(m.Label, HorizontalAlignment.Left, -1, 11).X;
        DrawString(font, new Vector2(Mathf.Clamp(x - w * 0.5f, 0, Width - w), Height + 40 + m.Row * 13), m.Label, HorizontalAlignment.Left, -1, 11, color);
    }
}
