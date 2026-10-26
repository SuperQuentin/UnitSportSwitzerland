using Godot;
using UnitSport.Player;

namespace UnitSport.Ui;

/// <summary>
/// The stamina ring (#1225, <c>docs/notes/ui/stamina-ring.md</c>): a small ring beside the body's
/// right shoulder (third person) or low right of the crosshair (first person), drawn by one shader on
/// this <see cref="ColorRect"/>. Shown only while the meter is not full, fades out
/// <see cref="FadeSeconds"/> after it fills, not drawn at full or when infinite. Uniforms are set only
/// when they change; no allocation per frame. The owner's HUD only (it lives on <c>PlayerFeel</c>'s layer).
/// </summary>
public sealed partial class StaminaRing : ColorRect
{
    /// <summary>Fade-out after the meter fills (s).</summary>
    public const float FadeSeconds = 1.5f;
    /// <summary>Side of the ring (px).</summary>
    public const float Side = 34f;

    private static readonly StringName FillP = "fill", PendingP = "pending", OuterArcP = "outer_arc",
        OuterFillP = "outer_fill", OuterPendingP = "outer_pending", OuterSegsP = "outer_segs", WindedP = "winded";

    private FootPlayer? _player;
    private readonly bool _inWorld;
    private readonly ShaderMaterial _mat;
    private float _alpha;
    private float _fill = -1f, _pending = -1f, _arc = -1f, _outerFill = -1f, _outerPending = -1f, _segs = -1f, _winded = -1f;
    private Vector2 _at = new(-1000f, -1000f);

    /// <summary>The body whose meter this shows (VR: set each frame from the rig).</summary>
    public FootPlayer? Player { set => _player = value; }

    /// <summary>Drawn at all now (the VR quad follows it).</summary>
    public bool Shown => Visible;

    /// <param name="inWorld">Not placed on the screen: a VR wrist copy inside a SubViewport (always at the origin).</param>
    public StaminaRing(FootPlayer? player, bool inWorld = false)
    {
        _player = player;
        _inWorld = inWorld;
        Name = "StaminaRing";
        MouseFilter = MouseFilterEnum.Ignore;
        Size = new Vector2(Side, Side);
        CustomMinimumSize = Size;
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/stamina_ring.gdshader") };
        Material = _mat;
        Visible = false;
    }

    /// <summary>A body-anchored screen point pushed out of the centre 20 % of the screen (to the side it is on) and kept on screen.</summary>
    public static Vector2 ClampedOutOfCentre(Vector2 at, Vector2 screen)
    {
        var half = screen * 0.1f;
        var c = screen * 0.5f;
        var d = at - c;
        if (Mathf.Abs(d.X) < half.X && Mathf.Abs(d.Y) < half.Y)
            at.X = c.X + (d.X < 0f ? -(half.X + Side * 0.5f) : half.X + Side * 0.5f);
        float m = Side * 0.5f + 4f;
        return new Vector2(Mathf.Clamp(at.X, m, screen.X - m), Mathf.Clamp(at.Y, m, screen.Y - m));
    }

    /// <summary>Once a frame, from <c>PlayerFeel</c>. Reads the meter struct; sets what changed.</summary>
    public void Tick(float dt)
    {
        if (_player == null) { if (Visible) Visible = false; return; }
        var m = _player.StaminaMeter;
        bool wants = !m.Infinite && (!m.IsFull || m.Winded);
        if (m.Infinite) _alpha = 0f;
        else if (wants) _alpha = 1f;
        else _alpha = Mathf.MoveToward(_alpha, 0f, dt / FadeSeconds);
        bool show = _alpha > 0.001f;
        if (Visible != show) Visible = show;
        if (!show) return;
        if (Modulate.A != _alpha) Modulate = new Color(1f, 1f, 1f, _alpha);

        // points -> shares of the inner 100 and of the outer rest
        float b = Stamina.Base, extra = Mathf.Max(0f, m.Max - b), p = m.Points;
        float cost = _player.IsClimbing ? m.CostNow(StaminaKind.ClimbJump) : 0f;
        float inner = Mathf.Min(p, b), outer = Mathf.Max(0f, p - b);
        float outerCost = Mathf.Min(cost, outer), innerCost = Mathf.Min(cost - outerCost, inner);
        // the outer ring spans up to 125 points as a share of the circle, in 10-point segments
        float arc = extra <= 0f ? 0f : Mathf.Min(1f, extra / 125f);
        Set(FillP, ref _fill, inner / b);
        Set(PendingP, ref _pending, innerCost / b);
        Set(OuterArcP, ref _arc, arc);
        Set(OuterFillP, ref _outerFill, extra <= 0f ? 0f : outer / extra);
        Set(OuterPendingP, ref _outerPending, extra <= 0f ? 0f : outerCost / extra);
        Set(OuterSegsP, ref _segs, Mathf.Ceil(extra / Stamina.StepPoints));
        Set(WindedP, ref _winded, m.Winded ? 1f : 0f);

        if (_inWorld) return;
        // where: beside the right shoulder, or fixed low right of the crosshair
        var screen = GetViewportRect().Size;
        Vector2 at;
        var cam = _player.CameraOrNull;
        if (_player.IsFirstPerson || cam == null)
            at = screen * 0.5f + new Vector2(58f, 66f);
        else
        {
            var right = cam.GlobalBasis.X;
            right.Y = 0f;
            right = right.LengthSquared() > 1e-4f ? right.Normalized() : Vector3.Right;
            var world = _player.GlobalPosition + Vector3.Up * 1.3f + right * 0.5f;
            at = cam.IsPositionBehind(world) ? screen * 0.5f + new Vector2(58f, 66f)
                : ClampedOutOfCentre(cam.UnprojectPosition(world), screen);
        }
        if (at.DistanceSquaredTo(_at) > 0.25f)
        {
            _at = at;
            Position = at - Size * 0.5f;
        }
    }

    /// <summary>
    /// <c>--stamina-shot [points]</c> (a dev aid, windowed): after the world is up, holds the meter at that many
    /// points (default 62), saves <c>test_output/stamina_ring_third.png</c> and <c>_first.png</c>, quits.
    /// </summary>
    public static void RunShotIfAsked(FootPlayer player, StaminaRing ring)
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--stamina-shot");
        if (at < 0 || !player.IsMultiplayerAuthority()) return;
        float points = at + 1 < args.Length && float.TryParse(args[at + 1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float p) ? p : 62f;
        _ = ring.Shot(points);
    }

    private async System.Threading.Tasks.Task Shot(float points)
    {
        var tree = GetTree();
        async System.Threading.Tasks.Task Frames(double seconds)
        {
            // frames, not the wall clock: a shot taken a few seconds' worth of frames in
            for (int frames = (int)(seconds * 60); frames > 0; frames--)
            {
                _player!.DebugSetStamina(points);
                await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
        void Save(string name)
        {
            string dir = ProjectSettings.GlobalizePath("res://test_output");
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, name);
            GetViewport().GetTexture().GetImage().SavePng(file);
            GD.Print($"[stamina-shot] wrote {file}");
        }
        await Frames(12.0);   // the world and the figure are up
        _player!.DebugThirdPerson(true);
        await Frames(2.0);
        Save("stamina_ring_third.png");
        _player!.DebugThirdPerson(false);
        await Frames(2.0);
        Save("stamina_ring_first.png");
        tree.Quit();
    }

    private void Set(StringName name, ref float cache, float v)
    {
        if (Mathf.Abs(cache - v) < 0.0005f) return;
        cache = v;
        _mat.SetShaderParameter(name, v);
    }
}
