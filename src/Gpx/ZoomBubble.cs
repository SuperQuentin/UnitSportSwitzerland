using Godot;
using UnitSport.Terrain;
using UnitSport.Core;

namespace UnitSport.Gpx;

/// <summary>
/// A comic speech-bubble callout that pops up whenever the active camera — whichever mode it is
/// in — has drifted far enough from the runner that they are a few pixels on screen. The bubble
/// holds a live close-up of the runner from a second camera, and its tail points at where they
/// really are in the main picture.
///
/// <para>
/// Replaces a red "HERE" arrow, which told you where the runner was but still left them too small
/// to see. The inset is a <see cref="SubViewport"/> that shares the main world (no
/// <c>OwnWorld3D</c>), so it draws the same streamed terrain; the runner is already a streaming
/// anchor, so what it looks at is always loaded. It renders only while the bubble is up.
/// </para>
///
/// <para>
/// Layer 6: above the lens (5), whose barrel distortion would otherwise bend the bubble, and below
/// the HUD (10), so the controls are never covered.
/// </para>
/// </summary>
public partial class ZoomBubble : CanvasLayer, Core.IOriginShiftAware
{
    /// <summary>Distance beyond which the runner counts as hard to spot.</summary>
    private const float ShowBeyond = 35f;

    /// <summary>Hysteresis: comes back below this, so a runner near one threshold does not flicker it.</summary>
    private const float HideBelow = 25f;

    private const float FadeRate = 4f;

    /// <summary>Disc radius in canvas pixels (the UI canvas, a fixed 1152x648 scaled to the window).</summary>
    private const float Radius = 98f;

    /// <summary>Gap between the disc and the runner's screen point that the tail spans.</summary>
    private const float TailLength = 70f;

    /// <summary>Keep-out from the screen edge for the disc.</summary>
    private const float Margin = 24f;

    /// <summary>Close-up camera: this far behind and above the runner, along their heading.</summary>
    private const float CamBack = 4.5f, CamUp = 1.6f, CamMinClearance = 1.5f;

    // Toon palette: a warm plum instead of black reads softer, cream instead of white reads
    // friendlier. The accent ring takes the runner's own leaderboard colour.
    private static readonly Color Ink = new(0.17f, 0.10f, 0.24f);
    private static readonly Color Paper = new(1.00f, 0.97f, 0.88f);
    private static readonly Color Shadow = new(0.10f, 0.05f, 0.15f, 0.38f);
    private static readonly Color Shine = new(1f, 1f, 1f, 0.55f);
    private static readonly Color Sparkle = new(1f, 0.93f, 0.45f);

    private ChunkManager _chunks = null!;
    private SubViewport _view = null!;
    private Camera3D _zoomCam = null!;
    private TextureRect _inset = null!;
    private Node2D _art = null!;
    private Node2D _front = null!;     // shine and sparkles, drawn over the inset
    private Color _accent = new(1f, 0.45f, 0.55f);
    private float _time;               // drives the idle wobble and the sparkle twinkle

    private float _shown;              // 0..1, eased
    private Vector2 _center, _tip;
    private bool _placed;              // false until the first visible frame, so it snaps into place
    private Vector3 _camPos;

    /// <summary>The origin moved (#185): the inset camera's eased position follows.</summary>
    public void OnOriginShifted(Core.OriginShift shift) => _camPos = shift.Point(_camPos);
    private bool _camPlaced;

    /// <summary>Master on/off. Disabling eases the bubble out rather than snapping it away.</summary>
    public bool Enabled { get; set; } = true;

    public static ZoomBubble Create(ChunkManager chunks) => new()
    {
        Name = "ZoomBubble",
        Layer = 6,
        _chunks = chunks,
    };

    public override void _Ready()
    {
        _view = new SubViewport
        {
            Name = "ZoomView",
            Size = new Vector2I(256, 256),
            OwnWorld3D = false,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        _zoomCam = new Camera3D
        {
            Name = "ZoomCamera",
            Fov = 34f,
            Near = 0.1f,
            Far = 900f,
            Current = true,   // current within its own viewport only
        };
        _view.AddChild(_zoomCam);
        AddChild(_view);

        // drawn first, under the inset: the tail and the ring merge into one outlined shape
        _art = new Node2D { Name = "BubbleArt" };
        _art.Draw += DrawArt;
        AddChild(_art);

        _inset = new TextureRect
        {
            Name = "Inset",
            Texture = _view.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = new ShaderMaterial
            {
                Shader = GD.Load<Shader>("res://shaders/zoom_bubble.gdshader"),
            },
        };
        AddChild(_inset);

        _front = new Node2D { Name = "BubbleShine" };
        _front.Draw += DrawFront;
        AddChild(_front);

        HideNow();
    }

    /// <summary>Hides the bubble at once and stops the inset rendering.</summary>
    public void HideNow()
    {
        _shown = 0;
        _time = 0;
        _placed = false;
        _camPlaced = false;
        Visible = false;
        if (_view != null) _view.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    /// <summary>
    /// Screen seconds the bubble stays away after a camera cut. Long enough that the new shot
    /// reads first, short enough that the bubble is still clearly "for" it.
    /// </summary>
    private const float CutHoldOff = 0.4f;

    private float _holdOff;

    /// <summary>
    /// The camera just cut to a different shot. Everything on screen jumped, so the bubble's
    /// eased position would visibly slide across the frame to catch up with the runner's new
    /// spot. Instead it vanishes on the cut itself, stays away for a beat, then pops in fresh
    /// wherever the new shot puts the runner.
    /// </summary>
    public void OnCameraCut()
    {
        HideNow();
        _holdOff = CutHoldOff;
    }

    /// <summary>
    /// Called once a frame with the focused runner and the camera the picture is being drawn
    /// from. <paramref name="dt"/> is screen seconds; pass 0 to snap instead of ease (seeking).
    /// </summary>
    public void UpdateFrame(Runner runner, Camera3D camera, float dt)
    {
        var head = runner.HeadWorld;
        float dist = head.DistanceTo(camera.GlobalPosition);
        bool want = Enabled && (_shown > 0.01f ? dist > HideBelow : dist > ShowBeyond);
        if (_holdOff > 0)
        {
            _holdOff -= dt;
            want = false;
        }

        _shown = dt > 0
            ? Mathf.MoveToward(_shown, want ? 1f : 0f, dt * FadeRate)
            : (want ? 1f : 0f);

        if (_shown <= 0.01f)
        {
            if (Visible) HideNow();
            return;
        }
        Visible = true;
        _view.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

        PlaceZoomCamera(runner, dt);
        PlaceOnScreen(head, camera, dt);

        _accent = runner.Tint;
        _time += dt;

        var (sx, sy) = PopScale();
        _inset.Position = _center - new Vector2(Radius * sx, Radius * sy);
        _inset.Size = new Vector2(2 * Radius * sx, 2 * Radius * sy);
        _art.QueueRedraw();
        _front.QueueRedraw();
    }

    /// <summary>
    /// Chase-style framing a few metres behind the runner. Eased, because the heading carries
    /// GPS jitter and a close-up magnifies every twitch of it.
    /// </summary>
    private void PlaceZoomCamera(Runner runner, float dt)
    {
        var head = runner.HeadWorld;
        var heading = runner.Heading with { Y = 0 };
        if (heading.LengthSquared() < 1e-4f) heading = Vector3.Forward;
        heading = heading.Normalized();

        var want = head - heading * CamBack + Vector3.Up * CamUp;
        // never under a slope rising behind the runner
        if (_chunks.TryGetHeight(want, out float ground))
            want.Y = Mathf.Max(want.Y, ground + CamMinClearance);

        _camPos = !_camPlaced || dt <= 0
            ? want
            : _camPos.Lerp(want, MathX.Damp(5f, dt));
        _camPlaced = true;

        var target = head + Vector3.Down * 0.7f;
        var dir = target - _camPos;
        if (dir.LengthSquared() < 1e-6f) return;
        dir = dir.Normalized();
        var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.999f ? Vector3.Forward : Vector3.Up;
        // right = forward x up — the other order is a reflection (see PlaybackCamera.Aim)
        var right = dir.Cross(up).Normalized();
        _zoomCam.GlobalTransform = new Transform3D(
            new Basis(right, right.Cross(dir).Normalized(), -dir), _camPos);
    }

    /// <summary>
    /// Puts the disc beside the runner's screen point, flipping sides near an edge. A runner off
    /// screen or behind the lens pins the disc to the edge nearest them, tail pointing outward.
    /// </summary>
    private void PlaceOnScreen(Vector3 head, Camera3D camera, float dt)
    {
        var screen = GetViewport().GetVisibleRect().Size;
        var lo = new Vector2(Margin + Radius, Margin + Radius);
        var hi = screen - lo;

        Vector2 center, tip;
        bool behind = camera.IsPositionBehind(head);
        var p = behind ? Vector2.Zero : camera.UnprojectPosition(head);
        bool onScreen = !behind && p.X >= 0 && p.Y >= 0 && p.X <= screen.X && p.Y <= screen.Y;

        if (onScreen)
        {
            var off = new Vector2(1f, -1f).Normalized() * (Radius + TailLength);
            if (p.X + off.X > hi.X) off.X = -off.X;
            if (p.Y + off.Y < lo.Y) off.Y = -off.Y;
            center = (p + off).Clamp(lo, hi);
            // stop a little short of the runner so the tip never covers them
            var toward = p - center;
            tip = p - toward.Normalized() * 8f;
        }
        else
        {
            // direction of the runner in the camera's own frame, flattened onto the screen
            var local = camera.GlobalTransform.Basis.Inverse() * (head - camera.GlobalPosition);
            var dir = new Vector2(local.X, -local.Y);
            if (dir.LengthSquared() < 1e-6f) dir = Vector2.Down;
            dir = dir.Normalized();

            var mid = screen * 0.5f;
            // push out from the middle until the disc meets the keep-out rectangle
            float tx = Mathf.Abs(dir.X) > 1e-4f ? (hi.X - mid.X) / Mathf.Abs(dir.X) : float.MaxValue;
            float ty = Mathf.Abs(dir.Y) > 1e-4f ? (hi.Y - mid.Y) / Mathf.Abs(dir.Y) : float.MaxValue;
            center = mid + dir * Mathf.Min(tx, ty);
            tip = center + dir * (Radius + TailLength * 0.6f);
        }

        if (!_placed || dt <= 0)
        {
            _center = center;
            _placed = true;
        }
        else
        {
            _center = _center.Lerp(center, MathX.Damp(8f, dt));
        }
        _tip = tip;
    }

    /// <summary>
    /// Toon pop: overshoots past full size and springs back (ease-out-back), then breathes with a
    /// slight squash and stretch, so the bubble reads as a soft living thing rather than a panel.
    /// </summary>
    private (float X, float Y) PopScale()
    {
        const float c1 = 2.2f, c3 = c1 + 1f;
        float t = _shown - 1f;
        float pop = Mathf.Max(0f, 1f + c3 * t * t * t + c1 * t * t);
        float breathe = 0.022f * Mathf.Sin(_time * 2.6f) * Mathf.Clamp(_shown * 2f - 1f, 0f, 1f);
        return (pop * (1f + breathe), pop * (1f - breathe));
    }

    /// <summary>
    /// A plain triangular tail from a base across the bubble's centre out to the tip.
    /// <paramref name="border"/> offsets both long sides outward by exactly that many pixels,
    /// so the outline is as thick along the tail as it is round the disc: the tip moves out by
    /// border / sin(half-angle) and the base corners by border / cos(half-angle), which keeps
    /// each side parallel to the unbordered one.
    /// </summary>
    private static Vector2[] Tail(Vector2 center, Vector2 tip, float halfWidth, float border)
    {
        var d = tip - center;
        float len = d.Length();
        if (len < 1e-3f) return System.Array.Empty<Vector2>();
        var axis = d / len;
        var side = new Vector2(-axis.Y, axis.X);

        float half = Mathf.Atan2(halfWidth, len);
        float tipOut = border / Mathf.Max(Mathf.Sin(half), 0.05f);
        float baseOut = border / Mathf.Cos(half);
        return new[]
        {
            center + side * (halfWidth + baseOut),
            tip + axis * tipOut,
            center - side * (halfWidth + baseOut),
        };
    }

    private static void DrawEllipse(Node2D on, Vector2 c, float rx, float ry, Color col)
    {
        const int n = 48;
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.Tau / n;
            pts[i] = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }
        on.DrawColoredPolygon(pts, col);
    }

    private void DrawArt()
    {
        var (sx, sy) = PopScale();
        float rx = Radius * sx, ry = Radius * sy;
        if (rx < 1f) return;

        // the tail grows out of the bubble with the pop, but never overshoots past the runner
        float grow = Mathf.Min(1f, sx);
        var tip = _center + (_tip - _center) * grow;
        float halfW = Radius * 0.62f * grow;
        var shadowOff = new Vector2(7f, 10f);

        // soft drop shadow under everything
        // one outline thickness for disc and tail alike (ink 17 - paper 10), so they read as one shape
        const float border = 7f;
        // pull the tail back so the OUTLINE's point, not the cream one, lands by the runner
        var along = tip - _center;
        float reach = along.Length();
        if (reach > 1e-3f)
        {
            float halfAngle = Mathf.Atan2(halfW, reach);
            tip -= along / reach * (border / Mathf.Max(Mathf.Sin(halfAngle), 0.05f));
        }
        var shadowTail = Tail(_center + shadowOff, tip + shadowOff, halfW, border);
        if (shadowTail.Length > 2) _art.DrawColoredPolygon(shadowTail, Shadow);
        DrawEllipse(_art, _center + shadowOff, rx + 17f, ry + 17f, Shadow);

        // chunky plum outline, cream body, then a runner-coloured ring hugging the picture
        var inkTail = Tail(_center, tip, halfW, border);
        if (inkTail.Length > 2) _art.DrawColoredPolygon(inkTail, Ink);
        DrawEllipse(_art, _center, rx + 10f + border, ry + 10f + border, Ink);

        var paperTail = Tail(_center, tip, halfW, 0f);
        if (paperTail.Length > 2) _art.DrawColoredPolygon(paperTail, Paper);
        DrawEllipse(_art, _center, rx + 10f, ry + 10f, Paper);

        DrawEllipse(_art, _center, rx + 5f, ry + 5f, _accent);
    }

    /// <summary>Glassy shine across the top-left and a few twinkling sparkles.</summary>
    private void DrawFront()
    {
        var (sx, sy) = PopScale();
        float rx = Radius * sx, ry = Radius * sy;
        if (rx < 1f) return;

        // curved highlight hugging the upper-left edge, plus a dot below it
        const int n = 16;
        var arc = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = Mathf.DegToRad(Mathf.Lerp(200f, 255f, i / (float)(n - 1)));
            arc[i] = _center + new Vector2(Mathf.Cos(a) * rx * 0.80f, Mathf.Sin(a) * ry * 0.80f);
        }
        _front.DrawPolyline(arc, Shine, 9f, true);
        float da = Mathf.DegToRad(188f);
        _front.DrawCircle(
            _center + new Vector2(Mathf.Cos(da) * rx * 0.80f, Mathf.Sin(da) * ry * 0.80f), 3.6f, Shine);

        // sparkles just outside the rim, twinkling out of phase
        DrawSparkle(_center + new Vector2(rx * 0.92f, -ry * 0.92f), 20f, 0f);
        DrawSparkle(_center + new Vector2(rx * 1.28f, -ry * 0.38f), 12f, 1.9f);
        DrawSparkle(_center + new Vector2(-rx * 1.08f, ry * 0.82f), 10f, 3.4f);
    }

    /// <summary>A four-point cartoon star with an ink outline.</summary>
    private void DrawSparkle(Vector2 at, float size, float phase)
    {
        float twinkle = 0.65f + 0.35f * Mathf.Sin(_time * 4.2f + phase);
        float appear = Mathf.Clamp(_shown * 1.6f - 0.6f, 0f, 1f);   // after the pop lands
        float r = size * twinkle * appear;
        if (r < 0.8f) return;

        _front.DrawColoredPolygon(Star(at, r + 4.5f, r * 0.34f + 3f), Ink);
        _front.DrawColoredPolygon(Star(at, r, r * 0.26f), Sparkle);

        static Vector2[] Star(Vector2 at, float outer, float inner)
        {
            var pts = new Vector2[8];
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.Pi / 4f - Mathf.Pi / 2f;
                pts[i] = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (i % 2 == 0 ? outer : inner);
            }
            return pts;
        }
    }
}
