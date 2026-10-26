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
        /// <summary>
        /// The default (#799), as on any web map: drag moves the map, a click puts the marker down.
        /// Nothing is selected by accident.
        /// </summary>
        Move,
        /// <summary>Drag a rectangle; it is applied when the button comes up.</summary>
        Rectangle,
        /// <summary>Paint tiles under the pointer as it moves.</summary>
        Brush,
        /// <summary>Paint tiles away again.</summary>
        Erase,
    }

    private static readonly int[] ZoomSteps = MapView.DefaultSteps;

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
    /// <summary>Pan, zoom and the LV95-to-screen maths, shared with the adventure map (#1224).</summary>
    private readonly MapView _view = new();
    private int PxPerKm => _view.PxPerKm;

    private Tool _tool = Tool.Move;
    private TileId? _anchor;
    private bool _panning;
    // the Move tool's left press: where it went down, and whether it has turned into a drag
    private Vector2? _pressAt;
    private bool _dragging;
    // the last pointer position seen: a pan moves by the difference, not by Relative, which the VR
    // panel's pointer (XR/XrUi) does not fill in
    private Vector2 _lastPointer;
    private bool _painting;
    private TileId? _hovered;

    // The status layer, rebuilt on change into one reused buffer rather than per pixel.
    private readonly byte[] _statusPixels = new byte[CountryData.Width * CountryData.Height * 4];
    private Image? _statusImage;
    private ImageTexture? _statusTexture;
    private int _statusVersion = -1;
    private int _statusLocalStamp = -1;

    // Canton borders, in LV95 metres, baked once; and the screen-space buffer DrawMultiline takes.

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
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public Tool CurrentTool
    {
        get => _tool;
        set
        {
            _tool = value;
            MouseDefaultCursorShape = value == Tool.Move ? CursorShape.PointingHand : CursorShape.Cross;
            QueueRedraw();
        }
    }

    /// <summary>The tile under the pointer, for the panel's read-out.</summary>
    public TileId? Hovered => _hovered;

    /// <summary>Where the view is centred, in LV95 metres.</summary>
    public (double E, double N) ViewCentre => (_view.CentreE, _view.CentreN);

    /// <summary>The current scale: screen pixels to one kilometre.</summary>
    public int Scale => PxPerKm;

    /// <summary>Zooms about the middle of the view, as the keyboard and pad do.</summary>
    public void Zoom(int steps) => ZoomAt(Size / 2, steps);

    public override void _Ready()
    {
        _font = UiTheme.Font;
        Resized += QueueRedraw;

        // + and - on the map itself: the zoom a VR hand (no wheel, the shoulders are its trigger),
        // a touchpad or a newcomer can find without being told
        var zoom = UiKit.VBox(6);
        zoom.MouseFilter = MouseFilterEnum.Ignore;   // only the buttons take a click; around them is still the map
        zoom.AnchorLeft = zoom.AnchorRight = 1;
        zoom.OffsetLeft = -56;
        zoom.OffsetRight = -12;
        zoom.OffsetTop = 12;
        foreach (var (label, steps, tip) in new[] { ("+", +1, "Zoom in"), ("−", -1, "Zoom out") })
        {
            var button = UiKit.Button(label, minWidth: 44);
            button.CustomMinimumSize = new Vector2(44, 44);
            button.AddThemeFontSizeOverride("font_size", 22);
            button.TooltipText = tip;
            button.FocusMode = FocusModeEnum.None;   // the map keeps the keyboard and the stick
            button.Pressed += () => Zoom(steps);
            zoom.AddChild(button);
        }
        AddChild(zoom);
        ZoomButtons = zoom;
    }

    /// <summary>The on-map + and − buttons (for <c>--mapcheck</c>).</summary>
    public Control? ZoomButtons { get; private set; }

    /// <summary>Centres the view on a point, optionally zooming in to it (a search result).</summary>
    public void FocusOn(double e, double n, int? pxPerKm = null)
    {
        _view.FocusOn(e, n, pxPerKm);
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

    private Vector2 ToScreen(double e, double n) { _view.Size = Size; return _view.ToScreen(e, n); }

    private (double E, double N) ToWorld(Vector2 p) { _view.Size = Size; return _view.ToLv95(p); }

    private TileId TileAt(Vector2 p)
    {
        var (e, n) = ToWorld(p);
        return TileId.FromLv95(e, n);
    }

    /// <summary>The view's world bounds in LV95 metres (west, south, east, north).</summary>
    private (double W, double S, double E, double N) Bounds() { _view.Size = Size; return _view.Bounds(); }

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
        _view.Size = Size;
        _view.DrawSampled(this, texture, lattice);
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
        _view.Size = Size;
        _view.DrawBorders(this, _country, new Color(1, 1, 1, 0.16f));
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

    /// <summary>A pin: a dot on the spot with a stem and a head above it, so it reads at a glance as "here".</summary>
    private void DrawLandingMarker()
    {
        if (Landing is not { } landing) return;
        var at = ToScreen(landing.E, landing.N);
        var head = at - new Vector2(0, 22);
        var shadow = new Color(0, 0, 0, 0.7f);
        DrawCircle(at, 4.5f, shadow);
        DrawCircle(at, 3f, UiTheme.Amber);
        DrawLine(at, head, shadow, 4f);
        DrawLine(at, head, UiTheme.Amber, 2f);
        DrawCircle(head, 9f, shadow);
        DrawCircle(head, 7.5f, UiTheme.Amber);
        DrawCircle(head, 3f, new Color(0.08f, 0.06f, 0.02f));
    }

    /// <summary>The tile under the pointer, outlined, so a click is never a guess.</summary>
    private void DrawHoverCell()
    {
        if (_hovered is not { } t || _anchor != null || _tool == Tool.Move) return;
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
        var moved = motion.Position - _lastPointer;
        _lastPointer = motion.Position;
        // the Move tool: a press that travels more than a few pixels is a drag, not a click
        if (_pressAt is { } down && !_dragging && motion.Position.DistanceTo(down) > 5) _dragging = true;
        if (_panning || _dragging)
        {
            _view.Pan(moved);
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
        _lastPointer = button.Position;
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
                else FinishLeft(button.Position);
                break;
        }
    }

    /// <summary>
    /// A left press. The Move tool waits to see whether it is a click (the marker goes there, on
    /// release) or a drag (the map moves). A drawing tool starts drawing, unless Shift is held, which
    /// still puts the marker down without leaving the tool.
    /// </summary>
    private void StartLeft(InputEventMouseButton button)
    {
        if (_tool == Tool.Move)
        {
            _pressAt = button.Position;
            _dragging = false;
            return;
        }
        if (Landing != null && button.ShiftPressed)
        {
            PlaceMarker(button.Position);
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

    private void PlaceMarker(Vector2 at)
    {
        if (Landing == null) return;
        var (e, n) = ToWorld(at);
        Landing = (e, n);
        LandingMoved?.Invoke(e, n);
        QueueRedraw();
    }

    private void FinishLeft(Vector2? at = null)
    {
        if (_pressAt is { } down)
        {
            if (!_dragging) PlaceMarker(at ?? down);
            _pressAt = null;
            _dragging = false;
            return;
        }
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
            _view.CentreE += pan.X * metres;
            _view.CentreN -= pan.Y * metres;
            _hovered = TileAt(Size / 2);
            QueueRedraw();
        }

        // in VR the triggers press the shoulders too, so every pointer click would zoom: + and - instead
        if (!XR.XrSession.Active)
        {
            if (Input.IsActionJustPressed(PlayerInput.MapZoomIn)) ZoomAt(Size / 2, +1);
            if (Input.IsActionJustPressed(PlayerInput.MapZoomOut)) ZoomAt(Size / 2, -1);
        }

        if (Input.IsActionJustPressed("ui_accept"))
        {
            // Enter / pad A: the marker on the centre, the centre tile, or close the rectangle a first press opened
            var centre = TileAt(Size / 2);
            if (_tool == Tool.Move)
            {
                PlaceMarker(Size / 2);
            }
            else if (_tool == Tool.Rectangle)
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
        _view.Size = Size;
        if (!_view.ZoomAt(at, steps)) return;
        _hovered = TileAt(at);
        QueueRedraw();
    }
}
