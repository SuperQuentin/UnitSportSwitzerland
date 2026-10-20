using System;

namespace UnitSport.Player;

/// <summary>
/// The ski force model (#899), pure so it is unit-tested (docs/plans/ski-physics.md). The skis'
/// horizontal velocity is held in their own frame: <see cref="State.U"/> along them and
/// <see cref="State.W"/> across (+ to the left). The heading is turned by the caller
/// (<c>Rideable.SteerByLean</c>, or a pivot for a hockey stop); the velocity lags it, and the edges
/// decide what happens to the difference:
/// <list type="bullet">
/// <item><b>carve</b>: while the slip angle is inside <see cref="Tuning.CarveWindow"/>, the edges turn
/// the sideways speed into speed along the skis at no cost, up to their grip;</item>
/// <item><b>skid</b>: beyond it, or past the grip, the sideways speed is scrubbed off by edge
/// friction, which is how a skier brakes.</item>
/// </list>
/// Gravity pulls down the fall line (both components). Off snow the skis barely slide, and the
/// rider moves by striding: each new press of forward is one push, holding it gives nothing.
/// </summary>
public static class SkiDynamics
{
    public const float G = 9.81f;

    public readonly record struct Tuning(
        float Mass, float Mu, float OffSnowMu, float DragArea, float TuckDragArea, float AirDensity,
        float CarveWindow, float GripBase, float GripEdge, float SkidMu, float PloughDecel,
        float StridePush, float StrideCap, float OffSnowPush, float OffSnowCap, float StrideGap)
    {
        /// <summary>Close to the numbers: μ 0.05 groomed, edges up to ~0.95 g, a skid at 0.55 g.</summary>
        public static readonly Tuning Sim = new(
            Mass: 82f, Mu: 0.05f, OffSnowMu: 0.30f, DragArea: 0.62f, TuckDragArea: 0.30f, AirDensity: 1.05f,
            CarveWindow: 0.12f, GripBase: 0.35f, GripEdge: 0.6f, SkidMu: 0.55f, PloughDecel: 4.5f,
            StridePush: 1.1f, StrideCap: 4.5f, OffSnowPush: 0.7f, OffSnowCap: 1.4f, StrideGap: 0.15f);

        /// <summary>The Game profile: more grip, a wider carve window, a gentler scrub, quicker strides.</summary>
        public static readonly Tuning Game = Sim with
        {
            Mu = 0.04f, CarveWindow = 0.22f, GripBase = 0.5f, GripEdge = 0.7f, SkidMu = 0.45f, PloughDecel = 5.5f,
            StridePush = 1.4f, StrideCap = 5.5f, OffSnowPush = 0.8f, OffSnowCap = 1.6f,
        };

        /// <summary>
        /// A snowboard (#922): one wide edge with a tighter sidecut (a narrower carve window, more
        /// bite at full edge), more frontal area standing sideways, no plough (brake is dragging the
        /// back edge), and one foot strapped in: a push with the free foot is shorter and slower
        /// than a skate on skis, and off snow it is a hobble.
        /// </summary>
        public static readonly Tuning Board = Sim with
        {
            DragArea = 0.70f, TuckDragArea = 0.42f,
            CarveWindow = 0.10f, GripBase = 0.30f, GripEdge = 0.68f, SkidMu = 0.60f, PloughDecel = 1.5f,
            StridePush = 0.8f, StrideCap = 2.5f, OffSnowPush = 0.5f, OffSnowCap = 1.0f, StrideGap = 0.25f,
        };

        /// <summary>The board in the Game profile.</summary>
        public static readonly Tuning BoardGame = Board with
        {
            Mu = 0.04f, CarveWindow = 0.20f, GripBase = 0.45f, GripEdge = 0.78f, SkidMu = 0.50f,
            StridePush = 1.0f, StrideCap = 3.2f, OffSnowPush = 0.6f, OffSnowCap = 1.2f,
        };
    }

    /// <summary>
    /// A board's edge grip against a ski's: the toe side is held on the balls of the feet, the
    /// heel side only by the highbacks and the shins, so it slips sooner.
    /// </summary>
    public static float BoardEdgeGrip(bool toeSide) => toeSide ? 1.1f : 0.9f;

    /// <summary>
    /// Whether a board's lean (<c>RideMotion.Bank</c>, − to the right) is onto its toe edge, for a
    /// rider whose chest faces the right of the travel (regular, or goofy riding switch).
    /// </summary>
    public static bool ToeSide(float bank, bool facingRight) => (bank < 0f) == facingRight;

    /// <summary>A hop turn's swing in the air, rad/s (#954): a quarter turn in about 0.2 s.</summary>
    public const float HopTurnRate = 7f;

    /// <summary>
    /// The most a stop swings the skis or the board off the travel, rad (~83°): square across it,
    /// on the edges, not past it into riding backwards. The hop turn and the hockey stop both hold here.
    /// </summary>
    public const float StopAngle = 1.45f;

    /// <summary>
    /// A stop's swing of the heading, at <paramref name="rate"/> rad/s toward <paramref name="side"/>
    /// (+1 turns left, as the yaw): the slip after <paramref name="dt"/>, never swung past
    /// <see cref="StopAngle"/> (already past it, it is left alone). The heading turns by the old slip
    /// minus the new one; the velocity does not turn at all.
    /// </summary>
    public static float SwingAcross(float slip, float side, float rate, float dt)
    {
        float next = slip - side * rate * dt;
        if (MathF.Abs(next) <= StopAngle) return next;
        // stop at the quarter turn; one already beyond it (a skid that came round) is not pushed further
        return MathF.Abs(slip) >= StopAngle ? slip : MathF.CopySign(StopAngle, next);
    }

    /// <summary>Speed above which travelling tail first counts as riding switch, m/s.</summary>
    public const float SwitchSpeed = 1.5f;

    /// <summary>
    /// A board is the same at both ends: travelling tail first (the slip past a quarter turn, with
    /// a margin so it does not flicker at 90°) it is ridden switch, the tail becoming the nose. The
    /// heading turns half a turn and the slip with it, so the velocity does not change. True when
    /// it swapped.
    /// </summary>
    public static bool Switch(ref float yaw, ref float slip, float speed)
    {
        if (speed < SwitchSpeed || MathF.Abs(slip) < MathF.PI / 2f + 0.2f) return false;
        yaw = Wrap(yaw + MathF.PI);
        slip = Wrap(slip - MathF.PI);
        return true;
    }

    private static float Wrap(float a)
    {
        a %= 2f * MathF.PI;
        return a > MathF.PI ? a - 2f * MathF.PI : a < -MathF.PI ? a + 2f * MathF.PI : a;
    }

    /// <summary>What is under the skis this step.</summary>
    /// <param name="OnSnow">Snow or ice underfoot; off it the skis barely slide.</param>
    /// <param name="SlopeAlong">Gravity's pull down the slope along the skis, m/s² (+ forward).</param>
    /// <param name="SlopeAcross">The same across them, m/s² (+ to the left).</param>
    /// <param name="Friction">The snow's friction against a groomed run's (#887 <c>SnowState.FrictionScale</c>).</param>
    /// <param name="Grip">The snow's edge grip against a groomed run's (<c>SnowState.GripScale</c>).</param>
    public readonly record struct Ground(bool OnSnow, float SlopeAlong, float SlopeAcross, float Friction = 1f, float Grip = 1f);

    /// <param name="Throttle">0..1; a rise past 0.5 is one stride.</param>
    /// <param name="Brake">0..1: the snowplough.</param>
    /// <param name="Edge">0..1: how far the skis are tipped onto their edges (the bank over its maximum).</param>
    public readonly record struct Input(float Throttle, float Brake, bool Tuck, float Edge);

    public struct State
    {
        public float U, W;
        public float StrideCooldown;
        public bool ThrottleDown;
        /// <summary>The last step's way of turning, for sound and the HUD: true while skidding.</summary>
        public bool Skidding;

        public readonly float Speed => MathF.Sqrt(U * U + W * W);

        /// <summary>Travel off the skis' heading, radians, + to the left (<c>RideMotion.Slip</c>).</summary>
        public readonly float Slip => U * U + W * W < 1e-6f ? 0f : MathF.Atan2(W, U);

        public static State From(float speed, float slip, State keep = default) =>
            keep with { U = speed * MathF.Cos(slip), W = speed * MathF.Sin(slip) };
    }

    /// <summary>One step on the ground.</summary>
    public static void Step(ref State s, in Tuning t, in Ground g, in Input input, float dt)
    {
        // ---- the stride: a new press of forward, never a held one --------------------------------
        bool down = input.Throttle > 0.5f;
        s.StrideCooldown = MathF.Max(0f, s.StrideCooldown - dt);
        if (down && !s.ThrottleDown && s.StrideCooldown <= 0f && s.U > -0.5f)
        {
            float cap = g.OnSnow ? t.StrideCap : t.OffSnowCap;
            if (s.U < cap) s.U = MathF.Min(cap, s.U + (g.OnSnow ? t.StridePush : t.OffSnowPush));
            s.StrideCooldown = t.StrideGap;
        }
        s.ThrottleDown = down;

        // ---- gravity down the fall line ----------------------------------------------------------
        s.U += g.SlopeAlong * dt;
        s.W += g.SlopeAcross * dt;

        // ---- friction, drag and the plough, all against the velocity -----------------------------
        float v = s.Speed;
        if (v > 1e-4f)
        {
            float mu = g.OnSnow ? t.Mu * g.Friction : t.OffSnowMu;
            float drag = 0.5f * t.AirDensity * (input.Tuck ? t.TuckDragArea : t.DragArea) * v * v / t.Mass;
            float decel = mu * G + drag + input.Brake * t.PloughDecel;
            float scale = MathF.Max(0f, v - decel * dt) / v;
            s.U *= scale;
            s.W *= scale;
        }

        // ---- the edges: carve the sideways speed into the run, or skid it away -------------------
        float w = MathF.Abs(s.W);
        if (w < 1e-5f) { s.Skidding = false; return; }
        float edge = Math.Clamp(input.Edge, 0f, 1f);
        // only an edge carves: flat skis on a traverse slip down it
        float grip = G * (t.GripBase * edge + t.GripEdge * edge) * g.Grip;
        float slipAngle = MathF.Atan2(w, MathF.Abs(s.U));
        bool carving = g.OnSnow && edge > 0.1f && MathF.Abs(s.U) > 1f && slipAngle < t.CarveWindow;
        if (carving)
        {
            // the edges turn the sideways speed into speed along the run: the speed is kept
            float taken = MathF.Min(w, grip * dt);
            float w1 = w - taken;
            float along = MathF.Sqrt(s.U * s.U + w * w - w1 * w1);
            s.U = MathF.CopySign(along, s.U);
            s.W = MathF.CopySign(w1, s.W);
            s.Skidding = false;
        }
        else
        {
            // a skid: the edges scrape across the snow, set harder the more they bite
            float scrub = G * t.SkidMu * (0.5f + 0.5f * edge) * (g.OnSnow ? g.Grip : 2f);
            s.W = MathF.CopySign(MathF.Max(0f, w - scrub * dt), s.W);
            s.Skidding = w > 0.5f;
        }
    }

    /// <summary>In the air: drag only, the edges have nothing to hold.</summary>
    public static void Air(ref State s, in Tuning t, bool tuck, float dt)
    {
        float v = s.Speed;
        if (v < 1e-4f) return;
        float drag = 0.5f * t.AirDensity * (tuck ? t.TuckDragArea : t.DragArea) * v * v / t.Mass;
        float scale = MathF.Max(0f, v - drag * dt) / v;
        s.U *= scale;
        s.W *= scale;
    }

    /// <summary>
    /// A landing by its equivalent fall height, v⊥²/2g, the speed into the ground: 0 clean (legs
    /// take up to 1.5 m), 1 a stumble (to 3 m), 2 a fall.
    /// </summary>
    public static int Landing(float speedIntoGround)
    {
        float efh = speedIntoGround * speedIntoGround / (2f * G);
        return efh < 1.5f ? 0 : efh < 3f ? 1 : 2;
    }

    /// <summary>
    /// Gravity's pull along a slope, split along and across the skis, from the ground's normal
    /// (x, y, z, unit, y up) and the skis' heading (x, z, unit, flat). g·n.y·(n.x, n.z) is the
    /// horizontal part of gravity along the plane; across is + to the heading's left.
    /// </summary>
    public static (float Along, float Across) Slope(float nx, float ny, float nz, float hx, float hz)
    {
        float ax = G * ny * nx, az = G * ny * nz;
        // left of a heading (hx, hz) in Godot's frame (y up, -z forward) is (hz, -hx)
        return (ax * hx + az * hz, ax * hz - az * hx);
    }
}
