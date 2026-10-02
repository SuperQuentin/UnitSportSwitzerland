using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// How a swimmer is drawn (#301). Replicated as a number in <c>FootPlayer.Anim.Z</c>: append only.
/// </summary>
public enum SwimStyle
{
    /// <summary>Upright at the surface, sculling hands and an egg-beater kick: treading water.</summary>
    Tread = 0,
    /// <summary>Front crawl at the surface: alternate arms over the back, a flutter kick, the body rolling.</summary>
    Crawl = 1,
    /// <summary>Under water: a breaststroke pull, a frog kick and a glide.</summary>
    Under = 2,
}

/// <summary>
/// The swimming figure (#301): three strokes as joint rigs, computed from a phase like the gait.
///
/// <para>
/// Authored upright like every pose (origin at the feet, +Z the way the face looks): the swimmer's
/// <c>BodyPose</c> lays it down. Laid prone, author +Y is the way it swims and author +Z points at
/// the bed, so a crawl's pull sweeps the hand through +Z (under the body) and its recovery through
/// −Z (over the back, out of the water). Bone lengths are the standing rig's.
/// </para>
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary>A swimming figure at <paramref name="phase"/> (0..1) of its stroke cycle.</summary>
    public static ArrayMesh BuildSwim(HumanPalette palette, SwimStyle style, float phase,
        Headwear hat = Headwear.None, ArrayMesh? into = null)
    {
        var scratch = ScratchFor(into);
        AppendRig(scratch, palette, SwimRig(style, phase), includeLegs: true, helmet: false, hat);
        return into == null ? scratch.Build() : scratch.BuildInto(into);
    }

    /// <summary>Mount points (eye, hands, back) of the swimming figure, flipped to face −Z like the mesh.</summary>
    public static GaitMounts MountsForSwim(SwimStyle style, float phase) => MountsForRig(SwimRig(style, phase));

    /// <summary>The swimming figure's joints, author space, in <see cref="Joint"/> order (a probe measures them).</summary>
    public static Vector3[] SwimJoints(SwimStyle style, float phase) => JointsOf(SwimRig(style, phase));

    /// <summary>
    /// Stroke cycles per second at a swimming speed (m/s). A crawl quickens with speed (a sprint
    /// is ~0.9 cycles a second, an easy pace ~0.65); treading water keeps a steady slow beat.
    /// </summary>
    public static float SwimRate(SwimStyle style, float speed) => style switch
    {
        SwimStyle.Crawl => Mathf.Clamp(0.42f + 0.2f * speed, 0.5f, 1.0f),
        SwimStyle.Under => Mathf.Clamp(0.32f + 0.14f * speed, 0.35f, 0.7f),
        _ => 0.55f,
    };

    /// <summary>Advances a stroke cycle by <paramref name="dt"/>; every style keeps moving (a swimmer never stands still).</summary>
    public static float AdvanceSwim(float phase, SwimStyle style, float speed, float dt) =>
        Mathf.PosMod(phase + SwimRate(style, speed) * dt, 1f);

    private const float ShoulderY = 1.445f, HipY = 0.965f, HipJointY = 0.935f;

    private static Rig SwimRig(SwimStyle style, float phase)
    {
        phase = Mathf.PosMod(phase, 1f);
        var shoulderL = new Vector3(-0.180f, ShoulderY, 0);
        var shoulderR = new Vector3(0.180f, ShoulderY, 0);
        var hipL = new Vector3(-0.090f, HipJointY, 0);
        var hipR = new Vector3(0.090f, HipJointY, 0);

        Vector3 wristL, wristR, hintL, hintR, ankleL, ankleR, kneeHintL, kneeHintR, toeL, toeR;
        float roll = 0f;
        switch (style)
        {
            case SwimStyle.Crawl:
            {
                (wristL, hintL) = CrawlArm(shoulderL, -1f, phase);
                (wristR, hintR) = CrawlArm(shoulderR, 1f, phase + 0.5f);
                // six-beat flutter: three kicks a leg per arm cycle, from the hip, knees soft
                ankleL = FlutterAnkle(-1f, phase);
                ankleR = FlutterAnkle(1f, phase + 1f / 6f);
                kneeHintL = kneeHintR = Vector3.Back;
                toeL = ankleL + new Vector3(0, -0.135f, -0.035f);   // pointed: along the leg
                toeR = ankleR + new Vector3(0, -0.135f, -0.035f);
                // the body rolls toward the arm that pulls, away from the one recovering
                roll = 0.42f * Mathf.Sin(Mathf.Tau * phase);
                break;
            }
            case SwimStyle.Under:
            {
                (wristL, hintL) = BreastArm(-1f, phase);
                (wristR, hintR) = BreastArm(1f, phase);
                (ankleL, kneeHintL) = FrogAnkle(-1f, phase);
                (ankleR, kneeHintR) = FrogAnkle(1f, phase);
                toeL = ankleL + new Vector3(-0.02f, -0.13f, -0.04f);
                toeR = ankleR + new Vector3(0.02f, -0.13f, -0.04f);
                break;
            }
            default:
            {
                // sculling: hands flat at chest height in front, sweeping out and in
                float sweep = Mathf.Sin(Mathf.Tau * phase);
                wristL = new Vector3(-(0.30f + 0.12f * sweep), 1.20f, 0.28f - 0.04f * sweep);
                wristR = new Vector3(0.30f + 0.12f * sweep, 1.20f, 0.28f - 0.04f * sweep);
                hintL = new Vector3(-1f, -0.3f, -0.5f);
                hintR = new Vector3(1f, -0.3f, -0.5f);
                // egg-beater: each foot circles under its knee, the two half a turn apart
                ankleL = EggBeater(-1f, phase);
                ankleR = EggBeater(1f, phase + 0.5f);
                kneeHintL = new Vector3(-0.6f, 0, 1f);
                kneeHintR = new Vector3(0.6f, 0, 1f);
                toeL = ankleL + new Vector3(-0.03f, -0.06f, 0.13f);
                toeR = ankleR + new Vector3(0.03f, -0.06f, 0.13f);
                break;
            }
        }

        var elbowL = Limb.Solve(shoulderL, wristL, UpperArmLength, ForearmLength, hintL);
        var elbowR = Limb.Solve(shoulderR, wristR, UpperArmLength, ForearmLength, hintR);
        var kneeL = Limb.Solve(hipL, ankleL, ThighLength, ShinLength, kneeHintL);
        var kneeR = Limb.Solve(hipR, ankleR, ThighLength, ShinLength, kneeHintR);

        var rig = new Rig(
            HeadTop: new(0, 1.780f, 0.01f), HeadBase: new(0, 1.590f, 0.005f),
            Neck: new(0, 1.525f, 0), Chest: new(0, 1.345f, 0),
            Waist: new(0, 1.090f, 0), Hip: new(0, HipY, 0),
            ShoulderL: shoulderL, ElbowL: elbowL, WristL: wristL,
            ShoulderR: shoulderR, ElbowR: elbowR, WristR: wristR,
            HipL: hipL, KneeL: kneeL, AnkleL: ankleL, ToeL: toeL,
            HipR: hipR, KneeR: kneeR, AnkleR: ankleR, ToeR: toeR,
            TorsoLean: 0f);
        return roll == 0f ? rig : Rolled(rig, roll);
    }

    /// <summary>One crawl arm: the pull under the body (bent at the elbow mid-way), the recovery over the back.</summary>
    private static (Vector3 Wrist, Vector3 Hint) CrawlArm(Vector3 shoulder, float side, float phase)
    {
        float q = Mathf.PosMod(phase, 1f);
        const float pull = 0.56f;
        float theta, reach;
        Vector3 hint;
        if (q < pull)
        {
            // overhead (0) through under the chest (pi/2) to the hip (pi): the catch keeps the elbow high
            theta = Mathf.Pi * q / pull;
            reach = 0.50f - 0.09f * Mathf.Sin(theta);
            hint = new Vector3(side * 0.7f, 0f, -0.5f);
        }
        else
        {
            // out at the hip and forward over the back, elbow leading, hand close in
            theta = Mathf.Pi + Mathf.Pi * (q - pull) / (1f - pull);
            reach = 0.50f - 0.16f * Mathf.Sin(theta - Mathf.Pi);
            hint = new Vector3(side * 0.5f, 0f, -1f);
        }
        float wide = q < pull ? 0.02f : 0.10f * Mathf.Sin(theta - Mathf.Pi);
        var wrist = shoulder + new Vector3(side * wide, reach * Mathf.Cos(theta), reach * Mathf.Sin(theta));
        return (wrist, hint);
    }

    /// <summary>A flutter-kick ankle: the leg nearly straight below the hip, swinging a little either side.</summary>
    private static Vector3 FlutterAnkle(float side, float phase)
    {
        float k = Mathf.Sin(Mathf.Tau * 3f * phase);
        return new Vector3(side * 0.10f, HipJointY - 0.815f, 0.13f * k);
    }

    /// <summary>An egg-beater ankle: circling below and a little out from its knee.</summary>
    private static Vector3 EggBeater(float side, float phase)
    {
        float a = Mathf.Tau * phase;
        return new Vector3(side * (0.24f + 0.06f * Mathf.Cos(a)), 0.42f + 0.10f * Mathf.Sin(a), -0.05f + 0.12f * Mathf.Cos(a));
    }

    /// <summary>
    /// A breaststroke arm: glide with the hands together overhead, sweep out and round to the chest,
    /// shoot forward again. The pull comes first and the kick after it, then the glide.
    /// </summary>
    private static (Vector3 Wrist, Vector3 Hint) BreastArm(float side, float phase)
    {
        var glide = new Vector3(side * 0.06f, ShoulderY + 0.49f, 0.04f);
        var wide = new Vector3(side * 0.42f, ShoulderY + 0.30f, 0.14f);
        var low = new Vector3(side * 0.30f, ShoulderY, 0.26f);
        var chest = new Vector3(side * 0.08f, ShoulderY - 0.07f, 0.22f);
        Vector3 wrist;
        if (phase < 0.12f) wrist = glide;
        else if (phase < 0.30f) wrist = glide.Lerp(wide, Ease((phase - 0.12f) / 0.18f));
        else if (phase < 0.42f) wrist = wide.Lerp(low, Ease((phase - 0.30f) / 0.12f));
        else if (phase < 0.50f) wrist = low.Lerp(chest, Ease((phase - 0.42f) / 0.08f));
        else if (phase < 0.64f) wrist = chest.Lerp(glide, Ease((phase - 0.50f) / 0.14f));
        else wrist = glide;
        return (wrist, new Vector3(side * 0.8f, 0f, -0.6f));
    }

    /// <summary>A frog kick: legs straight and together, heels drawn up to the seat, swept round and back together.</summary>
    private static (Vector3 Ankle, Vector3 KneeHint) FrogAnkle(float side, float phase)
    {
        var straight = new Vector3(side * 0.08f, HipJointY - 0.83f, -0.02f);
        var drawn = new Vector3(side * 0.20f, HipJointY - 0.48f, -0.24f);
        var wide = new Vector3(side * 0.40f, HipJointY - 0.70f, -0.08f);
        Vector3 ankle;
        if (phase < 0.42f) ankle = straight;
        else if (phase < 0.58f) ankle = straight.Lerp(drawn, Ease((phase - 0.42f) / 0.16f));
        else if (phase < 0.68f) ankle = drawn.Lerp(wide, Ease((phase - 0.58f) / 0.10f));
        else if (phase < 0.78f) ankle = wide.Lerp(straight, Ease((phase - 0.68f) / 0.10f));
        else ankle = straight;
        // the knees go out and down (toward the bed) as the heels come up
        return (ankle, new Vector3(side * 0.8f, 0f, 1f));
    }

    private static float Ease(float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>The whole figure turned about its long axis (author Y), the legs a little less than the shoulders.</summary>
    private static Rig Rolled(in Rig r, float roll)
    {
        var upper = new Basis(Vector3.Up, roll);
        var lower = new Basis(Vector3.Up, roll * 0.4f);
        Vector3 U(Vector3 v) => upper * v;
        Vector3 L(Vector3 v) => lower * v;
        return r with
        {
            HeadTop = U(r.HeadTop), HeadBase = U(r.HeadBase), Neck = U(r.Neck), Chest = U(r.Chest),
            Waist = U(r.Waist), Hip = L(r.Hip),
            ShoulderL = U(r.ShoulderL), ElbowL = U(r.ElbowL), WristL = U(r.WristL),
            ShoulderR = U(r.ShoulderR), ElbowR = U(r.ElbowR), WristR = U(r.WristR),
            HipL = L(r.HipL), KneeL = L(r.KneeL), AnkleL = L(r.AnkleL), ToeL = L(r.ToeL),
            HipR = L(r.HipR), KneeR = L(r.KneeR), AnkleR = L(r.AnkleR), ToeR = L(r.ToeR),
        };
    }
}
