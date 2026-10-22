using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Playtest.Recording;
using UnitSport.Ui;

namespace UnitSport.Playtest.TopView;

/// <summary>
/// The live top view (#844, docs/notes/general/topview.md): Shift+F12 shows a top-down square
/// bottom left, centred on the local player, with the last 10 s of every traced thing from the
/// recorder's ring, drawn from the same <see cref="TopViewScene"/> as the offline PNG. Shift + wheel
/// zooms (25 to 400 m across). Ctrl+F12 stops it on the moment it shows: Ctrl + wheel then scrubs
/// the last minute, and Ctrl+F12 again goes back to live. The game never pauses.
/// Redrawn when the ring has a new frame (10 Hz), only while shown. The ground under it (#851) is
/// read on a worker for a square twice the view's, and read again when the view leaves it.
/// </summary>
public partial class TopViewOverlay : CanvasLayer
{
    private const int Px = 420, Margin = 14;
    private const double Trail = 10;

    private readonly PlaytestRecorder _recorder;
    private Canvas _canvas = null!;
    private double _span = 150;
    /// <summary>The time shown when stopped; null while live.</summary>
    private double? _held;
    private double _drawnFor = double.NaN;
    private TopGround? _ground;
    private ImageTexture? _groundTexture;
    private bool _loading;
    private const int GroundPx = 512;

    public TopViewOverlay(PlaytestRecorder recorder)
    {
        _recorder = recorder;
        Name = "TopViewOverlay";
        Layer = 15;
    }

    public bool Shown
    {
        get => _canvas.Visible;
        set
        {
            _canvas.Visible = value;
            if (!value) _held = null;
            _drawnFor = double.NaN;
        }
    }

    public override void _Ready()
    {
        _canvas = new Canvas(this)
        {
            ClipContents = true,
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = Margin, OffsetRight = Margin + Px, OffsetTop = -Margin - Px, OffsetBottom = -Margin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        AddChild(_canvas);
    }

    public override void _Process(double delta)
    {
        if (!Shown) return;
        double key = _held ?? _recorder.Ring.Last?.T ?? 0;
        if (key == _drawnFor) return;
        _drawnFor = key;
        _canvas.QueueRedraw();
    }

    public override void _Input(InputEvent e)
    {
        if (UiFocus.TextEntryActive) return;
        if (e.IsActionPressed(PlayerInput.PlaytestTopView, exactMatch: true) && !e.IsEcho())
        {
            Shown = !Shown;
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed(PlayerInput.PlaytestTopViewScrub, exactMatch: true) && !e.IsEcho())
        {
            Shown = true;
            _held = _held == null ? _recorder.Ring.Last?.T : null;
            _drawnFor = double.NaN;
            GetViewport().SetInputAsHandled();
        }
        else if (Shown && e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel
                 && (wheel.ShiftPressed || wheel.CtrlPressed && _held != null))
        {
            int dir = wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
            if (wheel.CtrlPressed && _held is { } held && _recorder.Ring.Count > 0)
                _held = Math.Clamp(held - dir * 0.5, _recorder.Ring[0].T, _recorder.Ring.Last!.T);
            else _span = Math.Clamp(_span * (dir > 0 ? 0.8 : 1.25), 25, 400);
            _drawnFor = double.NaN;
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Toggled from the playtest panel (a pad, VR).</summary>
    public void Toggle() => Shown = !Shown;

    private TopViewShapes? Shapes()
    {
        var ring = _recorder.Ring;
        if (ring.Count == 0) return null;
        double at = _held ?? ring.Last!.T;
        var frames = ring.Window(at - Trail, at);
        EN? centre = null;
        int me = _recorder.LocalId;
        for (int i = frames.Count - 1; i >= 0 && centre == null; i--)
            if (frames[i].TryGet(me, out var s)) centre = new EN(s.E, s.N);
        var shapes = TopViewScene.Build(new TopViewInput
        {
            Frames = frames, Entities = _recorder.Entities, T0 = at - Trail, T1 = at, At = at,
            Around = centre, Radius = centre == null ? null : _span / 2, Notes = _recorder.Notes, MaxThings = 80,
        });
        double behind = (ring.Last!.T - at);
        shapes.Title = _held == null
            ? $"LIVE  {_span:0} m  (Shift+wheel zoom, Ctrl+F12 stop)"
            : $"STOPPED -{behind.ToString("0.0", CultureInfo.InvariantCulture)} s  (Ctrl+wheel scrub, Ctrl+F12 live)";
        return shapes;
    }

    private static Color C(TopColor c, float alpha = 1) => new Color(c.Hex) with { A = alpha };

    /// <summary>The ground read for a square holding <paramref name="s"/>'s, or null while there is none (one is then being read).</summary>
    private TopGround? GroundFor(TopViewShapes s)
    {
        var g = _ground;
        bool holds = g != null && s.Min.E >= g.Min.E && s.Min.N >= g.Min.N && s.Min.E + s.Side <= g.Min.E + g.Side
            && s.Min.N + s.Side <= g.Min.N + g.Side && g.Side <= 3 * s.Side;
        if (!holds && !_loading && _recorder.Ground?.Source is { } source) _ = Load(source, s);
        return holds ? g : null;
    }

    private async Task Load(Terrain.IChunkSource source, TopViewShapes s)
    {
        _loading = true;
        double side = s.Side * 2;
        var min = new EN(s.Min.E + s.Side / 2 - side / 2, s.Min.N + s.Side / 2 - side / 2);
        try
        {
            var ground = await Task.Run(() => TopViewGround.LoadAsync(source, min, side, GroundPx));
            // back on the main thread
            if (ground.Tiles > 0)
            {
                _groundTexture = ImageTexture.CreateFromImage(Image.CreateFromData(GroundPx, GroundPx, false, Image.Format.Rgba8, ground.Rgba));
                _ground = ground;
            }
        }
        catch (Exception e) { GD.PushWarning($"[topview] live ground: {e.GetBaseException().Message}"); }
        _loading = false;
        _drawnFor = double.NaN;
    }

    /// <summary>The square itself: draws the shapes in <see cref="Control._Draw"/>.</summary>
    private sealed partial class Canvas(TopViewOverlay owner) : Control
    {
        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, new Vector2(Px, Px)), new Color(0.06f, 0.07f, 0.09f, 0.82f));
            var font = ThemeDB.FallbackFont;
            if (owner.Shapes() is not { } s)
            {
                DrawString(font, new Vector2(10, 22), "Top view: nothing recorded yet", fontSize: 13, modulate: Colors.White);
                return;
            }
            float k = (float)(Px / s.Side);
            Vector2 P(EN p) => new((float)((p.E - s.Min.E) * k), (float)((s.Min.N + s.Side - p.N) * k));
            bool Inside(Vector2 v) => v.X >= -20 && v.Y >= -20 && v.X <= Px + 20 && v.Y <= Px + 20;

            // the ground, dimmed so the white labels and the tracks stay on top of it
            if (owner.GroundFor(s) is { } g && owner._groundTexture is { } texture)
            {
                float side = (float)(g.Side * k);
                DrawTextureRect(texture, new Rect2(P(new EN(g.Min.E, g.Min.N + g.Side)), new Vector2(side, side)), false, new Color(0.62f, 0.62f, 0.62f));
                foreach (var r in g.Roads)
                {
                    var pts = r.Points.Select(P).ToArray();
                    if (!pts.Any(Inside)) continue;
                    float w = Math.Max(1.2f, (float)(r.Width * k));
                    if (r.Casing != null) DrawPolyline(pts, (new Color(r.Casing) * 0.62f) with { A = 1 }, w + 2);
                    DrawPolyline(pts, (new Color(r.Fill) * 0.62f) with { A = r.Dashed ? 0.6f : 1 }, w);
                }
            }

            // a grid every scale-bar length
            double bar = TopViewScene.ScaleBar(s.Side);
            var grid = new Color(1, 1, 1, 0.07f);
            for (double e = Math.Ceiling(s.Min.E / bar) * bar; e <= s.Min.E + s.Side; e += bar)
                DrawLine(new Vector2((float)((e - s.Min.E) * k), 0), new Vector2((float)((e - s.Min.E) * k), Px), grid);
            for (double n = Math.Ceiling(s.Min.N / bar) * bar; n <= s.Min.N + s.Side; n += bar)
                DrawLine(new Vector2(0, (float)((s.Min.N + s.Side - n) * k)), new Vector2(Px, (float)((s.Min.N + s.Side - n) * k)), grid);

            foreach (var t in s.Tracks)
            {
                var pts = t.Points.Select(P).ToArray();
                if (t.Minor)
                {
                    if (pts.Length > 1) DrawPolyline(pts, C(t.Color, 0.35f), 1);
                    continue;
                }
                if (pts.Length > 1) DrawPolyline(pts, C(t.Color, 0.85f), 2);
                foreach (var tick in t.Ticks) DrawCircle(P(tick), 2, C(t.Color));
            }
            foreach (var p in s.Pins) DrawCircle(P(p.At), 5, new Color("#e03131"));
            foreach (var v in s.Vectors) DrawLine(P(v.From), P(v.To), new Color(1, 1, 1, 0.7f), 1.5f);
            foreach (var b in s.Boxes)
            {
                var fwd = EN.Bearing(b.Heading);
                var right = new EN(fwd.N, -fwd.E);
                var corners = new[] { (1, 1), (1, -1), (-1, -1), (-1, 1) }
                    .Select(c => P(b.Center + fwd * (c.Item1 * b.Along / 2) + right * (c.Item2 * b.Across / 2))).ToArray();
                if (!corners.Any(Inside)) continue;
                DrawColoredPolygon(corners, C(b.Color, 0.35f));
                DrawPolyline([.. corners, corners[0]], C(b.Color), 1.5f);
                DrawLine(corners[0], corners[1], C(b.Color), 3);
                if (b.Label.Length > 0) DrawString(font, P(b.Center) + new Vector2(6, -6), b.Label, fontSize: 11, modulate: new Color(1, 1, 1, 0.85f));
            }
            foreach (var a in s.Arrows)
            {
                var at = P(a.At);
                float h = (float)a.Heading;
                Vector2 D(float ang, float r) => at + new Vector2(MathF.Sin(ang), -MathF.Cos(ang)) * r;
                DrawColoredPolygon([D(h, 9), D(h + 2.5f, 7), at, D(h - 2.5f, 7)], C(a.Color));
                DrawString(font, at + new Vector2(8, 14), a.Label, fontSize: 11, modulate: C(a.Color));
            }
            float barPx = (float)(bar * k);
            DrawRect(new Rect2(12, Px - 20, barPx, 5), Colors.White);
            DrawString(font, new Vector2(12, Px - 26), $"{bar:0.#} m", fontSize: 11, modulate: Colors.White);
            DrawString(font, new Vector2(Px - 22, 22), "N", fontSize: 13, modulate: Colors.White);
            DrawString(font, new Vector2(10, 18), s.Title, fontSize: 12, modulate: Colors.White);
        }
    }
}
