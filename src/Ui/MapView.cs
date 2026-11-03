using Godot;
using UnitSport.Map;
using UnitSport.Terrain.Format;

namespace UnitSport.Ui;

/// <summary>
/// The pan, zoom and LV95-to-screen maths of a map of Switzerland (#1224), shared by the MapSetup tile
/// picker (<see cref="MapCanvas"/>) and the adventure map (<c>Adventure/Places/AdventureMap</c>): a centre
/// in LV95 metres, a zoom step (screen pixels per kilometre), the view's size. Also draws what both maps
/// draw the same way: a lattice-aligned texture sampled for the view (<see cref="DrawSampled"/>) and the
/// canton borders in one <see cref="CanvasItem.DrawMultiline"/> call. Nothing here allocates per redraw.
/// </summary>
public sealed class MapView
{
    public static readonly int[] DefaultSteps = [2, 3, 4, 6, 8, 12, 16, 24];

    private readonly int[] _steps;

    public MapView(int[]? steps = null) => _steps = steps ?? DefaultSteps;

    public double CentreE { get; set; } = 2_660_000;
    public double CentreN { get; set; } = 1_180_000;
    public int Zoom { get; set; }
    public Vector2 Size { get; set; }

    /// <summary>Screen pixels to one kilometre.</summary>
    public int PxPerKm => _steps[Math.Clamp(Zoom, 0, _steps.Length - 1)];
    /// <summary>Screen pixels to one metre.</summary>
    public double Ppm => PxPerKm / 1000.0;
    public int Steps => _steps.Length;

    public Vector2 ToScreen(double e, double n) => new(
        (float)((e - CentreE) / 1000.0 * PxPerKm + Size.X / 2),
        (float)((CentreN - n) / 1000.0 * PxPerKm + Size.Y / 2));

    public (double E, double N) ToLv95(Vector2 p) => (
        CentreE + (p.X - Size.X / 2) / PxPerKm * 1000.0,
        CentreN - (p.Y - Size.Y / 2) / PxPerKm * 1000.0);

    /// <summary>The view's bounds in LV95 metres (west, south, east, north).</summary>
    public (double W, double S, double E, double N) Bounds()
    {
        double halfW = Size.X / 2.0 / PxPerKm * 1000.0, halfH = Size.Y / 2.0 / PxPerKm * 1000.0;
        return (CentreE - halfW, CentreN - halfH, CentreE + halfW, CentreN + halfH);
    }

    /// <summary>Moves the map by a pointer's travel in pixels (a drag).</summary>
    public void Pan(Vector2 pixels)
    {
        CentreE -= pixels.X / (double)PxPerKm * 1000.0;
        CentreN += pixels.Y / (double)PxPerKm * 1000.0;
    }

    /// <summary>Zooms by steps keeping the point under <paramref name="at"/> where it is. False when already at the end.</summary>
    public bool ZoomAt(Vector2 at, int steps)
    {
        int next = Math.Clamp(Zoom + steps, 0, _steps.Length - 1);
        if (next == Zoom) return false;
        var (keepE, keepN) = ToLv95(at);
        Zoom = next;
        var (nowE, nowN) = ToLv95(at);
        CentreE += keepE - nowE;
        CentreN += keepN - nowN;
        return true;
    }

    /// <summary>Centres on a point, optionally at the first zoom step of at least <paramref name="pxPerKm"/>.</summary>
    public void FocusOn(double e, double n, int? pxPerKm = null)
    {
        CentreE = e;
        CentreN = n;
        if (pxPerKm is { } want) Zoom = Array.FindIndex(_steps, z => z >= want) is var i and >= 0 ? i : _steps.Length - 1;
    }

    /// <summary>
    /// Draws one lattice-aligned texture into the view by sampling the sub-rectangle the view covers, so
    /// panning and zooming never rebuild anything. The source rectangle is clamped to the texture and the
    /// destination narrowed to match, which keeps the lattice's edge from smearing across the view.
    /// </summary>
    public void DrawSampled(CanvasItem on, Texture2D? texture, (double MinE, double MaxN, double Spacing, int Cols, int Rows) lattice)
    {
        if (texture == null || lattice.Cols == 0) return;
        if (SampleRects(lattice) is not var (dest, src)) return;
        on.DrawTextureRectRegion(texture, dest, src);
    }

    /// <summary>The screen rectangle and the texture region (in lattice cells) of a lattice for this view; null when off screen.</summary>
    public (Rect2 Dest, Rect2 Src)? SampleRects((double MinE, double MaxN, double Spacing, int Cols, int Rows) lattice)
    {
        var (w, s, e, n) = Bounds();
        double x0 = (w - lattice.MinE) / lattice.Spacing, x1 = (e - lattice.MinE) / lattice.Spacing;
        double y0 = (lattice.MaxN - n) / lattice.Spacing, y1 = (lattice.MaxN - s) / lattice.Spacing;
        double cx0 = Math.Max(0, x0), cx1 = Math.Min(lattice.Cols, x1);
        double cy0 = Math.Max(0, y0), cy1 = Math.Min(lattice.Rows, y1);
        if (cx1 <= cx0 || cy1 <= cy0) return null;
        float sx = (float)((cx0 - x0) / (x1 - x0) * Size.X), sxEnd = (float)((cx1 - x0) / (x1 - x0) * Size.X);
        float sy = (float)((cy0 - y0) / (y1 - y0) * Size.Y), syEnd = (float)((cy1 - y0) / (y1 - y0) * Size.Y);
        return (new Rect2(sx, sy, sxEnd - sx, syEnd - sy), new Rect2((float)cx0, (float)cy0, (float)(cx1 - cx0), (float)(cy1 - cy0)));
    }

    // ---- canton borders -----------------------------------------------------------------------------

    private static Vector2[]? _borders;
    private Vector2[] _borderScreen = [];

    /// <summary>The canton borders as segments (LV95 metres), baked once from which canton each tile's centre is in.</summary>
    public static Vector2[] Borders(CountryData country)
    {
        if (_borders != null) return _borders;
        var points = new List<Vector2>();
        for (int eKm = CountryData.MinE; eKm <= CountryData.MaxE; eKm++)
            for (int nKm = CountryData.MinN; nKm <= CountryData.MaxN; nKm++)
            {
                var here = country.CantonAt(new TileId(eKm, nKm));
                if (here == null) continue;
                // a border is where the canton east or north differs: each boundary drawn once
                if (country.CantonAt(new TileId(eKm + 1, nKm))?.Id != here.Id)
                {
                    points.Add(new Vector2((eKm + 1) * 1000f, nKm * 1000f));
                    points.Add(new Vector2((eKm + 1) * 1000f, (nKm + 1) * 1000f));
                }
                if (country.CantonAt(new TileId(eKm, nKm + 1))?.Id != here.Id)
                {
                    points.Add(new Vector2(eKm * 1000f, (nKm + 1) * 1000f));
                    points.Add(new Vector2((eKm + 1) * 1000f, (nKm + 1) * 1000f));
                }
            }
        return _borders = points.ToArray();
    }

    /// <summary>Canton borders in one <see cref="CanvasItem.DrawMultiline"/> call, off-screen segments culled.</summary>
    public void DrawBorders(CanvasItem on, CountryData? country, Color color)
    {
        if (country == null) return;
        var borders = Borders(country);
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
        // DrawMultiline takes the whole array, and slicing it would allocate on every redraw: park the tail off screen
        var parked = new Vector2(-1e4f, -1e4f);
        for (int i = kept; i < _borderScreen.Length; i++) _borderScreen[i] = parked;
        on.DrawMultiline(_borderScreen, color);
    }
}
