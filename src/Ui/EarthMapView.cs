using Godot;
using UnitSport.Core;
using UnitSport.Map;
using UnitSport.Terrain.Format;

namespace UnitSport.Ui;

/// <summary>
/// The pan, zoom and projection of a map of the Earth (#1514, G2 C5): an oblique stereographic projection
/// (<see cref="MapProjection"/>) centred on the view, re-centred as it pans, true north up at the centre, scale 1
/// there. The adventure map draws on it (<c>Adventure/Places/AdventureMap</c>); MapSetup's Swiss map keeps
/// <see cref="MapView"/>, LV95 being its own projection. Docs: <c>docs/notes/ui/map-projection.md</c>.
///
/// <para>
/// Earth places (<see cref="GlobalPos"/>, latitude and longitude) go through the projection itself
/// (<see cref="ToScreen(GlobalPos)"/>). What is still held in LV95 metres until G2 D (the relief, the fog lattice,
/// the canton borders, POIs, pins) goes through <see cref="ToScreen(double, double)"/>: the projection's first-order
/// fit to LV95 at the view's centre (a turn by the convergence and a scale), refitted whenever the view moves.
/// Both are conformal, so the fit's error is second order: under 0.5 px at the widest zoom, far less zoomed in
/// (<c>MapProjectionTests</c>). Textures in an LV95 lattice are drawn whole under that affine, never resampled.
/// Nothing here allocates per redraw.
/// </para>
/// </summary>
public sealed class EarthMapView
{
    private readonly int[] _steps;
    private MapProjection _proj;
    private double _latDeg, _lonDeg;
    // the LV95 fit at the centre: LV95 metres from (_cE, _cN) to map metres (x east, y north)
    private double _cE, _cN, _a11, _a12, _a21, _a22, _i11, _i12, _i21, _i22;

    public EarthMapView(int[]? steps = null)
    {
        _steps = steps ?? MapView.DefaultSteps;
        var (lat, lon) = SwissGeodesy.ToLatLon(2_660_000, 1_180_000);
        _proj = new MapProjection(lat, lon);
        SetCentre(lat, lon);
    }

    public int Zoom { get; set; }
    public Vector2 Size { get; set; }

    /// <summary>Screen pixels to one kilometre at the centre.</summary>
    public int PxPerKm => _steps[Math.Clamp(Zoom, 0, _steps.Length - 1)];
    /// <summary>Screen pixels to one metre at the centre.</summary>
    public double Ppm => PxPerKm / 1000.0;
    public int Steps => _steps.Length;

    /// <summary>The projection, centred on the view.</summary>
    public MapProjection Projection => _proj;

    /// <summary>The view's centre, degrees.</summary>
    public (double LatDeg, double LonDeg) Centre => (_latDeg, _lonDeg);

    /// <summary>Centres the view on a latitude and longitude: the projection and the LV95 fit follow.</summary>
    public void SetCentre(double latDeg, double lonDeg)
    {
        _latDeg = Math.Clamp(latDeg, -89.9, 89.9);
        _lonDeg = lonDeg;
        _proj = new MapProjection(_latDeg, _lonDeg);
        Refit();
    }

    /// <summary>The projection's derivative against LV95 at the centre, by central differences of ±500 m.</summary>
    private void Refit()
    {
        (_cE, _cN) = SwissGeodesy.ToLv95(_latDeg, _lonDeg);
        const double d = 500;
        var (xe1, ye1) = MapOfLv95(_cE + d, _cN);
        var (xe0, ye0) = MapOfLv95(_cE - d, _cN);
        var (xn1, yn1) = MapOfLv95(_cE, _cN + d);
        var (xn0, yn0) = MapOfLv95(_cE, _cN - d);
        _a11 = (xe1 - xe0) / (2 * d); _a21 = (ye1 - ye0) / (2 * d);
        _a12 = (xn1 - xn0) / (2 * d); _a22 = (yn1 - yn0) / (2 * d);
        double det = _a11 * _a22 - _a12 * _a21;
        _i11 = _a22 / det; _i12 = -_a12 / det; _i21 = -_a21 / det; _i22 = _a11 / det;
        // the centre's own LV95 is not exactly the projection's 0 (ToLv95 at a ground height): take the offset in
        var (x0, y0) = MapOfLv95(_cE, _cN);
        _cE -= _i11 * x0 + _i12 * y0;
        _cN -= _i21 * x0 + _i22 * y0;
    }

    private (double X, double Y) MapOfLv95(double e, double n)
    {
        var (lat, lon) = SwissGeodesy.ToLatLon(e, n);
        return _proj.Forward(lat, lon);
    }

    // ---- map metres <-> screen ----------------------------------------------------------------------

    public Vector2 MapToScreen(double x, double y) => new(
        (float)(x * Ppm + Size.X / 2), (float)(-y * Ppm + Size.Y / 2));

    public (double X, double Y) ScreenToMap(Vector2 p) => (
        (p.X - Size.X / 2) / Ppm, -(p.Y - Size.Y / 2) / Ppm);

    // ---- Earth places -------------------------------------------------------------------------------

    public Vector2 ToScreen(GlobalPos at)
    {
        var (x, y) = _proj.Forward(at.Ecef);
        return MapToScreen(x, y);
    }

    public Vector2 ToScreenLatLon(double latDeg, double lonDeg)
    {
        var (x, y) = _proj.Forward(latDeg, lonDeg);
        return MapToScreen(x, y);
    }

    /// <summary>The latitude and longitude under a screen point.</summary>
    public (double LatDeg, double LonDeg) ToLatLon(Vector2 p)
    {
        var (x, y) = ScreenToMap(p);
        return _proj.Inverse(x, y);
    }

    /// <summary>
    /// A heading from true north (radians, as <c>Rotation.Y</c>: 0 north, positive turning west) at a screen point
    /// as one on the screen (0 up): the map's convergence there added. 0 at the centre's meridian.
    /// </summary>
    public float ScreenYaw(Vector2 at, float trueYaw)
    {
        var (x, y) = ScreenToMap(at);
        return trueYaw - (float)(_proj.ConvergenceAt(x, y) * Math.PI / 180);
    }

    // ---- LV95 (notation and the stores G2 D converts) -----------------------------------------------

    /// <summary>An LV95 point on the screen, through the projection's fit at the centre.</summary>
    public Vector2 ToScreen(double e, double n)
    {
        double de = e - _cE, dn = n - _cN;
        return MapToScreen(_a11 * de + _a12 * dn, _a21 * de + _a22 * dn);
    }

    /// <summary>The LV95 point under a screen point, through the projection itself (a click, the centre cross).</summary>
    public (double E, double N) ToLv95(Vector2 p)
    {
        var (lat, lon) = ToLatLon(p);
        return SwissGeodesy.ToLv95(lat, lon);
    }

    /// <summary>The LV95 box holding the view (west, south, east, north): for culling.</summary>
    public (double W, double S, double E, double N) Bounds()
    {
        double w = double.MaxValue, s = double.MaxValue, e = double.MinValue, n = double.MinValue;
        for (int k = 0; k < 4; k++)
        {
            var (x, y) = ScreenToMap(new Vector2((k & 1) * Size.X, (k >> 1) * Size.Y));
            double pe = _cE + _i11 * x + _i12 * y, pn = _cN + _i21 * x + _i22 * y;
            w = Math.Min(w, pe); e = Math.Max(e, pe); s = Math.Min(s, pn); n = Math.Max(n, pn);
        }
        return (w, s, e, n);
    }

    // ---- moving -------------------------------------------------------------------------------------

    /// <summary>Moves the map by a pointer's travel in pixels (a drag): the point that was at the centre follows the pointer.</summary>
    public void Pan(Vector2 pixels)
    {
        var (lat, lon) = _proj.Inverse(-pixels.X / Ppm, pixels.Y / Ppm);
        SetCentre(lat, lon);
    }

    /// <summary>Zooms by steps keeping the place under <paramref name="at"/> where it is. False when already at the end.</summary>
    public bool ZoomAt(Vector2 at, int steps)
    {
        int next = Math.Clamp(Zoom + steps, 0, _steps.Length - 1);
        if (next == Zoom) return false;
        var (keepLat, keepLon) = ToLatLon(at);
        Zoom = next;
        // the kept place's screen point now, and the pan that brings it back (twice: the centre moves under it)
        for (int i = 0; i < 2; i++)
        {
            var now = ToScreenLatLon(keepLat, keepLon);
            Pan(at - now);
        }
        return true;
    }

    /// <summary>Centres on an Earth place, optionally at the first zoom step of at least <paramref name="pxPerKm"/>.</summary>
    public void FocusOn(GlobalPos at, int? pxPerKm = null)
    {
        var g = at.Geodetic;
        SetCentre(g.LatDeg, g.LonDeg);
        SetZoom(pxPerKm);
    }

    /// <summary>Centres on an LV95 point, optionally at the first zoom step of at least <paramref name="pxPerKm"/>.</summary>
    public void FocusOn(double e, double n, int? pxPerKm = null)
    {
        var (lat, lon) = SwissGeodesy.ToLatLon(e, n);
        SetCentre(lat, lon);
        SetZoom(pxPerKm);
    }

    private void SetZoom(int? pxPerKm)
    {
        if (pxPerKm is { } want) Zoom = Array.FindIndex(_steps, z => z >= want) is var i and >= 0 ? i : _steps.Length - 1;
    }

    // ---- drawing ------------------------------------------------------------------------------------

    /// <summary>
    /// Draws a texture laid out on an LV95 lattice (north-west corner, cell spacing in metres) under the fit at the
    /// centre: the whole texture through one transform, so the GPU clips it and nothing is resampled or rebuilt.
    /// Its UV spans the whole texture, as a shader on it (the fog's) expects.
    /// </summary>
    public void DrawSampled(CanvasItem on, Texture2D? texture, (double MinE, double MaxN, double Spacing, int Cols, int Rows) lattice)
    {
        var (west, north, sp, cols, rows) = lattice;
        if (texture == null || cols == 0) return;
        var o = ToScreen(west, north);
        // one cell east and one cell south on the screen: the lattice's x and y axes
        var ex = new Vector2((float)(_a11 * sp * Ppm), (float)(-_a21 * sp * Ppm));
        var sy = new Vector2((float)(-_a12 * sp * Ppm), (float)(_a22 * sp * Ppm));
        on.DrawSetTransformMatrix(new Transform2D(ex, sy, o));
        on.DrawTextureRect(texture, new Rect2(0, 0, cols, rows), false);
        on.DrawSetTransformMatrix(Transform2D.Identity);
    }

    private Vector2[] _borderScreen = [];

    /// <summary>Canton borders in one <see cref="CanvasItem.DrawMultiline"/> call, off-screen segments culled.</summary>
    public void DrawBorders(CanvasItem on, CountryData? country, Color color)
    {
        if (country == null) return;
        var borders = MapView.Borders(country);
        if (borders.Length == 0) return;
        if (_borderScreen.Length != borders.Length) _borderScreen = new Vector2[borders.Length];
        var (w, s, e, n) = Bounds();
        int kept = 0;
        for (int i = 0; i + 1 < borders.Length; i += 2)
        {
            var a = borders[i];
            var b = borders[i + 1];
            if (Math.Max(a.X, b.X) < w || Math.Min(a.X, b.X) > e || Math.Max(a.Y, b.Y) < s || Math.Min(a.Y, b.Y) > n) continue;
            _borderScreen[kept++] = ToScreen(a.X, a.Y);
            _borderScreen[kept++] = ToScreen(b.X, b.Y);
        }
        if (kept == 0) return;
        var parked = new Vector2(-1e4f, -1e4f);
        for (int i = kept; i < _borderScreen.Length; i++) _borderScreen[i] = parked;
        on.DrawMultiline(_borderScreen, color);
    }
}
