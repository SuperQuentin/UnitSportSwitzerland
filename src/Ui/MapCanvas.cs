using Godot;
using UnitSport.Core;
using UnitSport.Map;
using UnitSport.Terrain.Format;

namespace UnitSport.Ui;

/// <summary>
/// The map of Switzerland you pick tiles on: shaded relief (<see cref="MapRelief"/>) with what is
/// downloaded, built and selected drawn over it, panned and zoomed with mouse, keyboard or pad.
///
/// <para>
/// <b>It redraws only when something changes.</b> Nothing here runs per frame unless the view is
/// actually moving, so <c>_Draw</c> can afford its labels and borders
/// (<c>docs/notes/general/perf-no-per-frame-allocations.md</c>). The three layers are a sampled
/// region of the relief texture, a 1 px-per-kilometre status image rebuilt only when the selection
/// or what is on disk changes, and the vectors — canton borders in a single
/// <see cref="CanvasItem.DrawMultiline"/> call, then labels, the landing marker and the cursor.
/// </para>
/// </summary>
public partial class MapCanvas : Control
{
    /// <summary>What a left-drag does. Shown as buttons in the panel, cycled with <c>map_tool</c>.</summary>
    public enum Tool
    {
        /// <summary>Drag a rectangle; it is applied when the button comes up.</summary>
        Rectangle,
        /// <summary>Paint tiles under the pointer as it moves.</summary>
        Brush,
        /// <summary>Paint tiles away again.</summary>
        Erase,
    }

    private static readonly int[] ZoomSteps = [2, 3, 4, 6, 8, 12, 16, 24];

    // Tile status, over the relief. Alpha rather than solid: the relief has to stay readable
    // underneath, or the map stops looking like the country as soon as you own some of it.
    private static readonly Color Outside = new(0.02f, 0.025f, 0.04f, 0.62f);
    private static readonly Color Downloaded = new(0.24f, 0.45f, 0.92f, 0.34f);
    private static readonly Color Built = new(0.16f, 0.80f, 0.80f, 0.30f);
    private static readonly Color Selected = new(1.00f, 0.75f, 0.12f, 0.46f);
    private static readonly Color BusyTint = new(1.00f, 0.95f, 0.62f, 0.60f);

    private readonly CountryData _country;
    private readonly Selection _selection;
    private LocalState _local;

    /// <summary>Tiles the running download is on right now (phase 4 fills this; empty until then).</summary>
    public HashSet<TileId> Working { get; } = new();

    // view: the centre in LV95 metres, and how many screen pixels one kilometre takes
    private double _centreE = 2_660_000, _centreN = 1_180_000;
    private int _zoom = 0;
    private int PxPerKm => ZoomSteps[_zoom];

    private Tool _tool = Tool.Rectangle;
    private TileId? _anchor;
    private bool _panning;
    private bool _painting;
    private TileId? _hovered;

    // The status layer, rebuilt on change into one reused buffer rather than per pixel.
    private readonly byte[] _statusPixels = new byte[CountryData.Width * CountryData.Height * 4];
    private Image? _statusImage;
    private ImageTexture? _statusTexture;
    private int _statusVersion = -1;
    private int _statusLocalStamp = -1;

    // Canton borders, in LV95 metres, baked once; and the screen-space buffer DrawMultiline takes.
    private Vector2[]? _borders;
    private Vector2[] _borderScreen = [];

    private Font _font = null!;
    private bool _reliefDrawn;

    /// <summary>Raised whenever the selection changed, so the screen can refresh its estimate.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when the landing marker is moved, in LV95 metres (the landing role, #515 phase 5).</summary>
    public event Action<double, double>? LandingMoved;

    /// <summary>The landing marker, or null when this map is not picking one.</summary>
    public (double E, double N)? Landing { get; private set; }

    public MapCanvas(CountryData country, Selection selection, LocalState local)
    {
        _country = country;
        _selection = selection;
        _local = local;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        // Nearest, not linear: a selected tile has to read as a crisp 1 km square rather than a
        // blurred blob, and the hard edges suit the game's look anyway.
        TextureFilter = TextureFilterEnum.Nearest;
    }

    public Tool CurrentTool
    {
        get => _tool;
        set { _tool = value; QueueRedraw(); }
    }

    /// <summary>The tile under the pointer, for the panel's read-out.</summary>
    public TileId? Hovered => _hovered;

    /// <summary>Where the view is centred, in LV95 metres.</summary>
    public (double E, double N) ViewCentre => (_centreE, _centreN);

    /// <summary>The current scale: screen pixels to one kilometre.</summary>
    public int Scale => PxPerKm;

    /// <summary>Zooms about the middle of the view, as the keyboard and pad do.</summary>
    public void Zoom(int steps) => ZoomAt(Size / 2, steps);

    public override void _Ready()
    {
        _font = UiTheme.Font;
        Resized += QueueRedraw;
    }

    /// <summary>Centres the view on a point, optionally zooming in to it (a search result).</summary>
    public void FocusOn(double e, double n, int? pxPerKm = null)
    {
        _centreE = e;
        _centreN = n;
        if (pxPerKm is { } want) _zoom = Array.FindIndex(ZoomSteps, z => z >= want) is var i and >= 0 ? i : ZoomSteps.Length - 1;
        QueueRedraw();
    }

    public void SetLanding(double e, double n)
    {
        Landing = (e, n);
        QueueRedraw();
    }

    /// <summary>After a download has written tiles: rescan what is on disk and redraw.</summary>
    public void Rescan(LocalState local)
    {
        _local = local;
        _statusLocalStamp = -1;
        QueueRedraw();
    }

    // ---- coordinates -------------------------------------------------------------------------

    private Vector2 ToScreen(double e, double n) => new(
        (float)((e - _centreE) / 1000.0 * PxPerKm + Size.X / 2),
        (float)((_centreN - n) / 1000.0 * PxPerKm + Size.Y / 2));

    private (double E, double N) ToWorld(Vector2 p) => (
        _centreE + (p.X - Size.X / 2) / PxPerKm * 1000.0,
        _centreN - (p.Y - Size.Y / 2) / PxPerKm * 1000.0);

    private TileId TileAt(Vector2 p)
    {
        var (e, n) = ToWorld(p);
        return TileId.FromLv95(e, n);
    }

    /// <summary>The view's world bounds in LV95 metres (west, south, east, north).</summary>
    private (double W, double S, double E, double N) Bounds()
    {
        double halfW = Size.X / 2.0 / PxPerKm * 1000.0, halfH = Size.Y / 2.0 / PxPerKm * 1000.0;
        return (_centreE - halfW, _centreN - halfH, _centreE + halfW, _centreN + halfH);
    }

    // ---- drawing -----------------------------------------------------------------------------

    public override void _Draw()
    {
        var full = new Rect2(Vector2.Zero, Size);
        DrawRect(full, new Color(0.05f, 0.06f, 0.08f));

        if (MapRelief.TryGet() is { } relief) DrawSampled(relief, MapRelief.Lattice());
        DrawSampled(StatusTexture(), (CountryData.MinE * 1000.0, (CountryData.MaxN + 1) * 1000.0, 1000.0,
            CountryData.Width, CountryData.Height));

        DrawBorders();
        DrawPlaceLabels();
        DrawRubberBand();
        DrawLandingMarker();
        DrawHoverCell();
    }

    /// <summary>
    /// Draws one lattice-aligned texture into the view by sampling the sub-rectangle the view
    /// covers, so panning and zooming never rebuild anything. The source rectangle is clamped to
    /// the texture and the destination narrowed to match, which is what keeps the edge of the
    /// lattice from smearing across the panel.
    /// </summary>
    private void DrawSampled(Texture2D? texture, (double MinE, double MaxN, double Spacing, int Cols, int Rows) lattice)
    {
        if (texture == null || lattice.Cols == 0) return;
        var (w, s, e, n) = Bounds();

        double x0 = (w - lattice.MinE) / lattice.Spacing, x1 = (e - lattice.MinE) / lattice.Spacing;
        double y0 = (lattice.MaxN - n) / lattice.Spacing, y1 = (lattice.MaxN - s) / lattice.Spacing;

        double cx0 = Math.Max(0, x0), cx1 = Math.Min(lattice.Cols, x1);
        double cy0 = Math.Max(0, y0), cy1 = Math.Min(lattice.Rows, y1);
        if (cx1 <= cx0 || cy1 <= cy0) return;

        // the clamped source rectangle, back in screen pixels
        float sx = (float)((cx0 - x0) / (x1 - x0) * Size.X), sxEnd = (float)((cx1 - x0) / (x1 - x0) * Size.X);
        float sy = (float)((cy0 - y0) / (y1 - y0) * Size.Y), syEnd = (float)((cy1 - y0) / (y1 - y0) * Size.Y);

        DrawTextureRectRegion(texture, new Rect2(sx, sy, sxEnd - sx, syEnd - sy),
            new Rect2((float)cx0, (float)cy0, (float)(cx1 - cx0), (float)(cy1 - cy0)));
    }

    /// <summary>
    /// The status layer: one pixel per kilometre tile, which is exactly one tile, so it never
    /// disagrees with what a click selects. Rebuilt only when the selection, the running download
    /// or what is on disk changed.
    /// </summary>
    private Texture2D? StatusTexture()
    {
        int localStamp = _local.Downloaded.Count * 397 + _local.Built.Count * 31 + Working.Count;
        if (_statusTexture != null && _statusVersion == _selection.Version && _statusLocalStamp == localStamp)
            return _statusTexture;

        for (int y = 0; y < CountryData.Height; y++)
        {
            for (int x = 0; x < CountryData.Width; x++)
            {
                var tile = new TileId(CountryData.MinE + x, CountryData.MaxN - y);
                var colour = !_country.Covered(tile) ? Outside
                    : Working.Contains(tile) ? BusyTint
                    : _selection.Contains(tile) ? Selected
                    : _local.Built.Contains(tile) ? Built
                    : _local.Downloaded.Contains(tile) ? Downloaded
                    : new Color(0, 0, 0, 0);
                int k = (y * CountryData.Width + x) * 4;
                _statusPixels[k + 0] = (byte)(colour.R * 255);
                _statusPixels[k + 1] = (byte)(colour.G * 255);
                _statusPixels[k + 2] = (byte)(colour.B * 255);
                _statusPixels[k + 3] = (byte)(colour.A * 255);
            }
        }

        if (_statusImage == null)
        {
            _statusImage = Image.CreateFromData(CountryData.Width, CountryData.Height, false, Image.Format.Rgba8, _statusPixels);
            _statusTexture = ImageTexture.CreateFromImage(_statusImage);
        }
        else
        {
            _statusImage.SetData(CountryData.Width, CountryData.Height, false, Image.Format.Rgba8, _statusPixels);
            _statusTexture!.Update(_statusImage);
        }
        _statusVersion = _selection.Version;
        _statusLocalStamp = localStamp;
        return _statusTexture;
    }

    /// <summary>
    /// Canton borders, as one <see cref="CanvasItem.DrawMultiline"/> call. Baked once from which
    /// canton each tile's centre falls in: a few thousand separate <c>DrawLine</c> calls a redraw
    /// would cost more than everything else on the screen together.
    /// </summary>
    private void DrawBorders()
    {
        _borders ??= BakeBorders();
        if (_borders.Length == 0) return;
        if (_borderScreen.Length != _borders.Length) _borderScreen = new Vector2[_borders.Length];

        var (w, s, e, n) = Bounds();
        int kept = 0;
        for (int i = 0; i + 1 < _borders.Length; i += 2)
        {
            // cull off-screen segments: at zoom 2 the whole country is on screen, at 24 almost none of it
            var a = _borders[i];
            var b = _borders[i + 1];
            if (Math.Max(a.X, b.X) < w || Math.Min(a.X, b.X) > e || Math.Max(a.Y, b.Y) < s || Math.Min(a.Y, b.Y) > n) continue;
            _borderScreen[kept++] = ToScreen(a.X, a.Y);
            _borderScreen[kept++] = ToScreen(b.X, b.Y);
        }
        if (kept == 0) return;
        // DrawMultiline takes the whole array, and slicing it would allocate on every redraw (a pan
        // redraws each frame): park the culled tail on one off-screen point, where it draws nothing.
        var parked = new Vector2(-1e4f, -1e4f);
        for (int i = kept; i < _borderScreen.Length; i++) _borderScreen[i] = parked;
        DrawMultiline(_borderScreen, new Color(1, 1, 1, 0.16f));
    }

    private Vector2[] BakeBorders()
    {
        var points = new List<Vector2>();
        for (int eKm = CountryData.MinE; eKm <= CountryData.MaxE; eKm++)
        {
            for (int nKm = CountryData.MinN; nKm <= CountryData.MaxN; nKm++)
            {
                var here = _country.CantonAt(new TileId(eKm, nKm));
                if (here == null) continue;
                // A border is where the canton to the east or to the north differs. Drawing only
                // those two sides per tile draws each boundary once instead of twice.
                if (_country.CantonAt(new TileId(eKm + 1, nKm))?.Id != here.Id)
                {
                    points.Add(new Vector2((eKm + 1) * 1000f, nKm * 1000f));
                    points.Add(new Vector2((eKm + 1) * 1000f, (nKm + 1) * 1000f));
                }
                if (_country.CantonAt(new TileId(eKm, nKm + 1))?.Id != here.Id)
                {
                    points.Add(new Vector2(eKm * 1000f, (nKm + 1) * 1000f));
                    points.Add(new Vector2((eKm + 1) * 1000f, (nKm + 1) * 1000f));
                }
            }
        }
        return points.ToArray();
    }

    /// <summary>
    /// Place names, the biggest first and only as many as the zoom has room for, so the Plateau
    /// does not turn into a wall of text. Summits appear only once zoomed in past the towns.
    /// </summary>
    private void DrawPlaceLabels()
    {
        var (w, s, e, n) = Bounds();
        int budget = PxPerKm <= 2 ? 14 : PxPerKm <= 4 ? 28 : PxPerKm <= 8 ? 55 : 110;
        int size = PxPerKm <= 4 ? 10 : 11;
        int shown = 0;
        _labelled.Clear();

        foreach (var town in _country.Towns.OrderByDescending(t => t.Rank))
        {
            if (shown >= budget) break;
            if (town.Kind != PlaceKind.Town && PxPerKm < 8) continue;
            if (town.E < w || town.E > e || town.N < s || town.N > n) continue;

            var at = ToScreen(town.E, town.N);
            // Biggest first, and anything that would land on a name already drawn is dropped:
            // without this the Plateau is a wall of overlapping text at every zoom.
            if (Collides(at, town.Name.Length * size * 0.52f)) continue;
            _labelled.Add((at, town.Name.Length * size * 0.52f));
            DrawCircle(at, 1.6f, new Color(1, 1, 1, 0.65f));
            // one dark offset copy first: a label has to stay readable over snow and over lake alike
            DrawString(_font, at + new Vector2(5, 4), town.Name, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, 0.75f));
            DrawString(_font, at + new Vector2(4, 3), town.Name, HorizontalAlignment.Left, -1, size, new Color(1, 1, 1, 0.92f));
            shown++;
        }
    }

    /// <summary>Labels already placed this redraw: where, and how wide, for the declutter test.</summary>
    private readonly List<(Vector2 At, float Width)> _labelled = new();

    private bool Collides(Vector2 at, float width)
    {
        foreach (var (other, otherWidth) in _labelled)
            if (Math.Abs(at.Y - other.Y) < 13 && at.X < other.X + otherWidth + 8 && other.X < at.X + width + 8)
                return true;
        return false;
    }

    /// <summary>The rectangle being dragged, before it is applied.</summary>
    private void DrawRubberBand()
    {
        if (_anchor is not { } anchor || _hovered is not { } to) return;
        var a = ToScreen(Math.Min(anchor.E, to.E) * 1000.0, (Math.Max(anchor.N, to.N) + 1) * 1000.0);
        var b = ToScreen((Math.Max(anchor.E, to.E) + 1) * 1000.0, Math.Min(anchor.N, to.N) * 1000.0);
        var rect = new Rect2(a, b - a);
        DrawRect(rect, new Color(1, 0.75f, 0.12f, 0.18f));
        DrawRect(rect, _tool == Tool.Erase ? UiTheme.Bad : UiTheme.Amber, filled: false, width: 1.5f);
    }

    private void DrawLandingMarker()
    {
        if (Landing is not { } landing) return;
        var at = ToScreen(landing.E, landing.N);
        DrawLine(at - new Vector2(0, 11), at + new Vector2(0, 11), new Color(0, 0, 0, 0.7f), 3.5f);
        DrawLine(at - new Vector2(11, 0), at + new Vector2(11, 0), new Color(0, 0, 0, 0.7f), 3.5f);
        DrawLine(at - new Vector2(0, 10), at + new Vector2(0, 10), UiTheme.Amber, 1.6f);
        DrawLine(at - new Vector2(10, 0), at + new Vector2(10, 0), UiTheme.Amber, 1.6f);
        DrawArc(at, 7, 0, Mathf.Tau, 24, UiTheme.Amber, 1.6f);
    }

    /// <summary>The tile under the pointer, outlined, so a click is never a guess.</summary>
    private void DrawHoverCell()
    {
        if (_hovered is not { } t || _anchor != null) return;
        var a = ToScreen(t.E * 1000.0, (t.N + 1) * 1000.0);
        var b = ToScreen((t.E + 1) * 1000.0, t.N * 1000.0);
        DrawRect(new Rect2(a, b - a), new Color(1, 1, 1, 0.55f), filled: false, width: 1.2f);
    }

    // ---- input -------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion:
                OnMotion(motion);
                break;
            case InputEventMouseButton button:
                OnButton(button);
                break;
            case InputEventKey { Pressed: true } key:
                OnKey(key);
                break;
        }
    }

    private void OnMotion(InputEventMouseMotion motion)
    {
        if (_panning)
        {
            _centreE -= motion.Relative.X / (double)PxPerKm * 1000.0;
            _centreN += motion.Relative.Y / (double)PxPerKm * 1000.0;
            QueueRedraw();
            return;
        }

        var tile = TileAt(motion.Position);
        if (_hovered is { } old && old.E == tile.E && old.N == tile.N) return;
        _hovered = tile;
        if (_painting) Paint(tile);
        QueueRedraw();
    }

    private void OnButton(InputEventMouseButton button)
    {
        switch (button.ButtonIndex)
        {
            case MouseButton.WheelUp when button.Pressed:
                ZoomAt(button.Position, +1);
                break;
            case MouseButton.WheelDown when button.Pressed:
                ZoomAt(button.Position, -1);
                break;
            case MouseButton.Right or MouseButton.Middle:
                _panning = button.Pressed;
                break;
            case MouseButton.Left:
                GrabFocus();
                _hovered = TileAt(button.Position);
                if (button.Pressed) StartLeft(button);
                else FinishLeft();
                break;
        }
    }

    /// <summary>
    /// A left press: place the landing marker when this map is picking one and Shift is held,
    /// otherwise start the current tool. Shift is what keeps "where do I land" from fighting
    /// "what do I download" on the one map that does both.
    /// </summary>
    private void StartLeft(InputEventMouseButton button)
    {
        if (Landing != null && button.ShiftPressed)
        {
            var (e, n) = ToWorld(button.Position);
            Landing = (e, n);
            LandingMoved?.Invoke(e, n);
            QueueRedraw();
            return;
        }

        if (_tool == Tool.Rectangle)
        {
            _anchor = _hovered;
        }
        else
        {
            _painting = true;
            if (_hovered is { } t) Paint(t);
        }
        QueueRedraw();
    }

    private void FinishLeft()
    {
        if (_anchor is { } anchor && _hovered is { } to)
        {
            _selection.AddRect(anchor, to, remove: _tool == Tool.Erase);
            SelectionChanged?.Invoke();
        }
        _anchor = null;
        _painting = false;
        QueueRedraw();
    }

    private void Paint(TileId t)
    {
        bool changed = _tool == Tool.Erase ? _selection.Remove(t) : _selection.Add(t);
        if (changed) SelectionChanged?.Invoke();
    }

    private void OnKey(InputEventKey key)
    {
        switch (key.PhysicalKeycode)
        {
            case Key.Equal or Key.Plus or Key.KpAdd:
                ZoomAt(Size / 2, +1);
                break;
            case Key.Minus or Key.KpSubtract:
                ZoomAt(Size / 2, -1);
                break;
            default:
                return;
        }
        AcceptEvent();
    }

    /// <summary>
    /// Keyboard and pad panning, zooming and painting. Read here rather than as events so a held
    /// arrow or a leaned stick pans smoothly; when nothing is held this costs one
    /// <see cref="Input.GetVector"/> and no redraw.
    /// </summary>
    public override void _Process(double delta)
    {
        // The relief is built on a worker, and the canvas only redraws on change: without this
        // poll the map would stay blank until the player happened to move it.
        if (!_reliefDrawn && MapRelief.TryGet() != null)
        {
            _reliefDrawn = true;
            QueueRedraw();
        }

        if (!HasFocus()) return;

        // ui_* is arrows and the left stick alike, so one read covers keyboard and pad
        var pan = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        if (pan != Vector2.Zero)
        {
            // a constant screen speed, so panning feels the same whatever the zoom
            const double PixelsPerSecond = 620.0;
            double metres = PixelsPerSecond / PxPerKm * 1000.0 * delta;
            _centreE += pan.X * metres;
            _centreN -= pan.Y * metres;
            _hovered = TileAt(Size / 2);
            QueueRedraw();
        }

        if (Input.IsActionJustPressed(PlayerInput.MapZoomIn)) ZoomAt(Size / 2, +1);
        if (Input.IsActionJustPressed(PlayerInput.MapZoomOut)) ZoomAt(Size / 2, -1);

        if (Input.IsActionJustPressed("ui_accept"))
        {
            // Enter / pad A: the centre tile, or close the rectangle a first press opened
            var centre = TileAt(Size / 2);
            if (_tool == Tool.Rectangle)
            {
                if (_anchor == null) { _anchor = centre; _hovered = centre; }
                else { _hovered = centre; FinishLeft(); }
            }
            else
            {
                Paint(centre);
            }
            QueueRedraw();
        }
    }

    private void ZoomAt(Vector2 at, int steps)
    {
        int next = Math.Clamp(_zoom + steps, 0, ZoomSteps.Length - 1);
        if (next == _zoom) return;
        // keep the world point under the cursor under the cursor
        var (keepE, keepN) = ToWorld(at);
        _zoom = next;
        var (nowE, nowN) = ToWorld(at);
        _centreE += keepE - nowE;
        _centreN += keepN - nowN;
        _hovered = TileAt(at);
        QueueRedraw();
    }
}
