using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The free-climbing figure (#1225): hands and feet on a wall plane in front of the chest, the two
/// diagonals (left hand with right foot, right hand with left foot) reaching in turn. Authored
/// upright like every pose (origin at the feet, author +Z the way the face looks, so the wall is at
/// +Z); the climber's <c>BodyPose</c> leans it into the face. Posed on a <see cref="SkinnedFigure"/>
/// by its bones, never rebuilt (<c>no-mesh-rebuild-per-frame</c>). See <c>wall-climb-pose.md</c>.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary>How far in front of the chest (author +Z) the hands and feet sit: the wall plane.</summary>
    public const float WallClimbReach = 0.35f;

    /// <summary>Metres climbed for one full limb cycle (each diagonal pair reaches up once).</summary>
    public const float WallClimbMetresPerCycle = 0.7f;

    /// <summary>A figure climbing a wall at limb <paramref name="phase"/> (0..1); <paramref name="jump"/> 0..1 stretches it for a climb-jump.</summary>
    public static void PoseWallClimb(SkinnedFigure f, float phase, float jump) => PoseRig(f, WallClimbRig(phase, jump), Vector3.Zero);

    /// <summary>Mount points of the wall-climbing figure, flipped to face −Z like the mesh.</summary>
    public static GaitMounts MountsForWallClimb(float phase, float jump) => MountsForRig(WallClimbRig(phase, jump));

    /// <summary>
    /// Advances the limb phase by the distance climbed (<paramref name="speed"/> m/s along the face,
    /// signed: down runs the cycle backwards); holding still changes nothing. Wrapped to 0..1.
    /// </summary>
    public static float AdvanceWallClimb(float phase, float speed, float dt) =>
        Mathf.PosMod(phase + speed * dt / WallClimbMetresPerCycle, 1f);

    private static Rig WallClimbRig(float phase, float jump)
    {
        phase = Mathf.PosMod(phase, 1f);
        jump = Mathf.Clamp(jump, 0f, 1f);
        float j = Ease(jump);
        const float z = WallClimbReach;
        var shoulderL = new Vector3(-0.180f, ShoulderY, 0.030f);
        var shoulderR = new Vector3(0.180f, ShoulderY, 0.030f);
        var hipL = new Vector3(-0.090f, HipJointY, 0);
        var hipR = new Vector3(0.090f, HipJointY, 0);

        // diagonal pairs: left hand + right foot at phase, right hand + left foot half a cycle on
        var wristL = ClimbHand(-1f, phase, z).Lerp(new Vector3(-0.10f, ShoulderY + 0.50f, 0.14f), j);
        var wristR = ClimbHand(1f, phase + 0.5f, z).Lerp(new Vector3(0.10f, ShoulderY + 0.50f, 0.14f), j);
        var ankleR = ClimbFoot(1f, phase, z).Lerp(new Vector3(0.09f, 0.14f, 0.16f), j);
        var ankleL = ClimbFoot(-1f, phase + 0.5f, z).Lerp(new Vector3(-0.09f, 0.14f, 0.16f), j);
        // the toes point into the wall; in the push they point down
        var toeL = ankleL + new Vector3(0, -0.05f - 0.07f * j, 0.10f - 0.07f * j);
        var toeR = ankleR + new Vector3(0, -0.05f - 0.07f * j, 0.10f - 0.07f * j);

        var elbowL = Limb.Solve(shoulderL, wristL, UpperArmLength, ForearmLength, new Vector3(-1f, -0.3f, -0.6f));
        var elbowR = Limb.Solve(shoulderR, wristR, UpperArmLength, ForearmLength, new Vector3(1f, -0.3f, -0.6f));
        var kneeL = Limb.Solve(hipL, ankleL, ThighLength, ShinLength, new Vector3(-0.5f, 0f, 1f));
        var kneeR = Limb.Solve(hipR, ankleR, ThighLength, ShinLength, new Vector3(0.5f, 0f, 1f));

        float up = 0.03f * j;   // stretched a little taller
        return new Rig(
            HeadTop: new(0, 1.775f + up, 0.060f), HeadBase: new(0, 1.585f + up, 0.045f),
            Neck: new(0, 1.520f + up, 0.035f), Chest: new(0, 1.340f + up, 0.030f),
            Waist: new(0, 1.090f, 0.010f), Hip: new(0, HipY, 0),
            ShoulderL: shoulderL, ElbowL: elbowL, WristL: wristL,
            ShoulderR: shoulderR, ElbowR: elbowR, WristR: wristR,
            HipL: hipL, KneeL: kneeL, AnkleL: ankleL, ToeL: toeL,
            HipR: hipR, KneeR: kneeR, AnkleR: ankleR, ToeR: toeR,
            TorsoLean: 0f);
    }

    /// <summary>
    /// A hand on the wall: reaching up from the low hold to the high one over the first half of its
    /// cycle (lifted off the face), then planted, sliding back down relative to the rising body.
    /// </summary>
    private static Vector3 ClimbHand(float side, float phase, float z)
    {
        float q = Mathf.PosMod(phase, 1f);
        const float low = 1.22f, high = 1.80f;
        if (q < 0.5f)
        {
            float t = Ease(q / 0.5f);
            return new Vector3(side * (0.24f - 0.04f * t), Mathf.Lerp(low, high, t), z - 0.10f * Mathf.Sin(Mathf.Pi * t));
        }
        float p = (q - 0.5f) / 0.5f;
        return new Vector3(side * 0.20f, Mathf.Lerp(high, low, p), z);
    }

    /// <summary>A foot on the wall: the knee comes up to the next hold (first half), then it pushes while planted.</summary>
    private static Vector3 ClimbFoot(float side, float phase, float z)
    {
        float q = Mathf.PosMod(phase, 1f);
        const float low = 0.17f, high = 0.60f;
        // the ankle is behind the toe, which touches the wall plane
        float az = z - 0.09f;
        if (q < 0.5f)
        {
            float t = Ease(q / 0.5f);
            return new Vector3(side * (0.15f + 0.04f * Mathf.Sin(Mathf.Pi * t)), Mathf.Lerp(low, high, t), az - 0.08f * Mathf.Sin(Mathf.Pi * t));
        }
        float p = (q - 0.5f) / 0.5f;
        return new Vector3(side * 0.15f, Mathf.Lerp(high, low, p), az);
    }
}
