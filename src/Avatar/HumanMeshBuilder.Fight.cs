using Godot;
using UnitSport.Combat;

namespace UnitSport.Avatar;

/// <summary>
/// Fist-fight poses (#495): a fighter's stance, its moves and its reactions, drawn by the dance
/// layer like an emote. A fight pose is a <see cref="DanceParams.Move"/> at <see cref="FightMoves"/>
/// + the replicated <c>FootPlayer.FightPose</c> (a <see cref="FightStance"/>, or a move at
/// <see cref="FightStance.MoveBase"/>). For a move, <see cref="DanceParams.BarPhase"/> is how far
/// through it the fighter is (0..1 of its <see cref="MoveDef.Total"/>), so the strike lands on its
/// active frames on every peer; for a stance it is a slow loop (the bob). Orthodox stance: the
/// figure's left hand and foot (rig-R, s = +1) lead, the chest twisted to put that shoulder forward.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary><see cref="DanceParams.Move"/> values from here on are fight poses: <c>FightMoves + FootPlayer.FightPose</c>.</summary>
    public const int FightMoves = 3000;

    private const float Lead = 1f, Rear = -1f;

    private static DanceMove FightResolve(int pose)
    {
        var move = FightRules.MoveOf((byte)Mathf.Clamp(pose, 0, 255));
        if (move != Combat.FightMove.None)
            return move switch
            {
                Combat.FightMove.Jab => DanceMove.FightJab,
                Combat.FightMove.Kick => DanceMove.FightKick,
                Combat.FightMove.LowJab => DanceMove.FightLowJab,
                Combat.FightMove.Sweep => DanceMove.FightSweep,
                Combat.FightMove.JumpKick => DanceMove.FightJumpKick,
                Combat.FightMove.Uppercut => DanceMove.FightUppercut,
                Combat.FightMove.StringKick => DanceMove.FightStringKick,
                _ => DanceMove.FightFinisher,
            };
        return (FightStance)pose switch
        {
            FightStance.GuardHigh => DanceMove.FightGuardHigh,
            FightStance.Crouch => DanceMove.FightCrouch,
            FightStance.GuardLow => DanceMove.FightGuardLow,
            FightStance.Air => DanceMove.FightAir,
            FightStance.HitStun or FightStance.Down => DanceMove.FightHit,
            FightStance.Dazed => DanceMove.FightDazed,
            FightStance.Victory => DanceMove.FightVictory,
            FightStance.BlockStun => DanceMove.FightBlockStun,
            _ => DanceMove.FightStand,
        };
    }

    /// <summary>A strike's extension over its progress: out over the startup, held through the active frames, back over the recovery.</summary>
    private static float Ext(Combat.FightMove move, float p)
    {
        var d = FightRules.Def(move);
        float a = d.Startup / d.Total, b = (d.Startup + d.Active) / d.Total;
        if (p < a) return DSm(p / a);
        if (p < b) return 1f;
        return 1f - DSm((p - b) / Mathf.Max(0.01f, 1f - b));
    }

    private static Vector3 Elbow(float s) => new(s * 0.5f, -0.7f, -0.1f);

    private static void EvalFight(DanceMove move, in Beat t, bool mv, ref DanceCh ch)
    {
        float p = t.R;
        switch (move)
        {
            case DanceMove.FightStand: FightGuard(ref ch, DSin(p * 2f), 0f, false); break;
            case DanceMove.FightGuardHigh: FightGuard(ref ch, 0f, 0f, true); break;
            case DanceMove.FightCrouch: FightGuard(ref ch, DSin(p * 2f), 1f, false); break;
            case DanceMove.FightGuardLow: FightGuard(ref ch, 0f, 1f, true); break;
            case DanceMove.FightAir: FightAir(ref ch); break;
            case DanceMove.FightHit: FightHit(ref ch, p); break;
            case DanceMove.FightBlockStun: FightGuard(ref ch, 0f, 0f, true); ch.Theta -= 0.12f; ch.ThN += 0.1f; break;
            case DanceMove.FightDazed: FightDazed(ref ch, p); break;
            case DanceMove.FightVictory: FightVictory(ref ch, p); break;
            case DanceMove.FightJab: FightJab(ref ch, Ext(Combat.FightMove.Jab, p), false); break;
            case DanceMove.FightLowJab: FightJab(ref ch, Ext(Combat.FightMove.LowJab, p), true); break;
            case DanceMove.FightKick: FightKick(ref ch, Ext(Combat.FightMove.Kick, p)); break;
            case DanceMove.FightSweep: FightSweep(ref ch, Ext(Combat.FightMove.Sweep, p), p); break;
            case DanceMove.FightJumpKick: FightJumpKick(ref ch, Ext(Combat.FightMove.JumpKick, p)); break;
            case DanceMove.FightUppercut: FightUppercut(ref ch, Ext(Combat.FightMove.Uppercut, p), false); break;
            case DanceMove.FightFinisher: FightUppercut(ref ch, Ext(Combat.FightMove.Finisher, p), true); break;
            case DanceMove.FightStringKick: FightRoundhouse(ref ch, Ext(Combat.FightMove.StringKick, p)); break;
        }
        // stepping in the stance: the legs keep the gait, the guard stays up
        if (mv) MovingScale(ref ch, 0.8f, 1f);
    }

    /// <summary>
    /// The stance: side-on, knees bent, fists at the chin, a bob on <paramref name="bob"/> (-1..1);
    /// <paramref name="crouch"/> 1 is down low; <paramref name="block"/> covers up behind the forearms.
    /// </summary>
    private static void FightGuard(ref DanceCh ch, float bob, float crouch, bool block)
    {
        ch.Psi = -0.38f;
        ch.Theta = 0.10f + 0.14f * crouch;
        ch.ThN = block ? 0.18f : -0.06f;
        ch.Py = -0.06f - 0.015f * bob - 0.28f * crouch;
        if (block)
        {
            // forearms up in front of the face (high) or the body (low)
            float up = crouch > 0.5f ? 0.02f : 0.20f;
            Aim(ref ch, Lead, -0.12f, up, 0.20f, Elbow(Lead));
            Aim(ref ch, Rear, -0.14f, up - 0.02f, 0.16f, Elbow(Rear));
        }
        else
        {
            Aim(ref ch, Lead, -0.06f, 0.10f - 0.06f * crouch, 0.30f, Elbow(Lead));
            Aim(ref ch, Rear, -0.10f, 0.14f - 0.06f * crouch, 0.17f, Elbow(Rear));
        }
        Foot(ref ch, Lead, 0.13f, AnkleHeight, 0.20f, 0.1f, 0f);
        Foot(ref ch, Rear, -0.15f, AnkleHeight, -0.20f, 0.5f, 0f);
        HeelUp(ref ch, Rear, 0.04f * (1f - crouch));
    }

    /// <summary>Jab: the lead fist straight out and back, the lead shoulder rolled in behind it. Low: from the crouch, at the belly.</summary>
    private static void FightJab(ref DanceCh ch, float e, bool low)
    {
        FightGuard(ref ch, 0f, low ? 1f : 0f, false);
        ch.Psi = -0.38f - 0.22f * e;
        ch.Theta += 0.06f * e;
        Aim(ref ch, Lead, Mathf.Lerp(-0.06f, 0.0f, e), Mathf.Lerp(low ? 0.04f : 0.10f, low ? -0.16f : 0.06f, e),
            Mathf.Lerp(0.30f, 0.49f, e), new Vector3(0.6f, -0.6f, 0f));
    }

    /// <summary>Front kick: the rear knee up and the foot driven out at the belly, the body leaning back to balance.</summary>
    private static void FightKick(ref DanceCh ch, float e)
    {
        FightGuard(ref ch, 0f, 0f, false);
        ch.Theta = 0.10f - 0.28f * e;
        ch.Psi = -0.38f + 0.2f * e;
        // the knee comes up first, then the foot goes out
        float chamber = DSm(Mathf.Min(1f, e * 1.6f)), out_ = DSm(Mathf.Max(0f, e * 1.6f - 0.6f));
        var ankle = new Vector3(-0.15f, AnkleHeight, -0.20f)
            .Lerp(new Vector3(-0.10f, 0.45f, 0.20f), chamber)
            .Lerp(new Vector3(-0.06f, 0.78f, 0.62f), out_);
        Foot(ref ch, Rear, ankle.X, ankle.Y, ankle.Z, 0f, 1f);
    }

    /// <summary>Sweep: down on the lead leg, the rear leg swung low around the front, the chest turning with it.</summary>
    private static void FightSweep(ref DanceCh ch, float e, float p)
    {
        FightGuard(ref ch, 0f, 1f, false);
        ch.Py = -0.36f;
        ch.Theta = 0.30f;
        // the arc runs on the progress, so the foot carries on round instead of going back the way it came
        float a = Mathf.Lerp(Mathf.DegToRad(200f), Mathf.DegToRad(80f), DSm(Mathf.Min(1f, p * 1.5f)));
        float r = 0.62f * Mathf.Max(e, 0.35f);
        Foot(ref ch, Rear, -0.09f + r * Mathf.Cos(a), AnkleHeight + 0.03f, r * Mathf.Sin(a), 1.2f * e, 0f);
        ch.Psi = Mathf.Lerp(-0.38f, 0.55f, e);
        // the lead hand down to the floor for balance
        Aim(ref ch, Lead, 0.10f, -0.40f * e, 0.26f, HLow(Lead));
    }

    /// <summary>Jump kick: the lead knee tucked, the rear leg driven out and down at the head.</summary>
    private static void FightJumpKick(ref DanceCh ch, float e)
    {
        FightAir(ref ch);
        ch.Theta = 0.05f - 0.22f * e;
        Foot(ref ch, Rear, -0.06f, Mathf.Lerp(0.45f, 0.62f, e), Mathf.Lerp(0.15f, 0.66f, e), 0f, 1f);
    }

    /// <summary>
    /// Uppercut: knees dip, then the rear fist rips up the middle as the legs drive up and the chest
    /// turns through. The finisher is the two-fisted version, off the ground.
    /// </summary>
    private static void FightUppercut(ref DanceCh ch, float e, bool both)
    {
        FightGuard(ref ch, 0f, 0f, false);
        float load = 1f - e;
        ch.Py = -0.06f - 0.14f * load * DSm(Mathf.Min(1f, e * 4f)) + (both ? 0.10f * e : 0.02f * e);
        ch.Theta = Mathf.Lerp(0.22f, -0.12f, e);
        ch.Psi = Mathf.Lerp(-0.38f, both ? 0f : 0.35f, e);
        ch.ThN = -0.2f * e;
        Aim(ref ch, Rear, Mathf.Lerp(-0.06f, -0.04f, e), Mathf.Lerp(-0.22f, 0.44f, e), Mathf.Lerp(0.18f, 0.20f, e), HOut(Rear));
        if (both) Aim(ref ch, Lead, Mathf.Lerp(-0.06f, -0.04f, e), Mathf.Lerp(-0.22f, 0.44f, e), Mathf.Lerp(0.18f, 0.20f, e), HOut(Lead));
        if (both && e > 0.5f)
        {
            Lift(ref ch, Lead, 0.12f * e);
            Lift(ref ch, Rear, 0.12f * e);
        }
    }

    /// <summary>Roundhouse, the string's last hit: the hips turn over, the rear shin swung round at the ribs, the body leaning away.</summary>
    private static void FightRoundhouse(ref DanceCh ch, float e)
    {
        FightGuard(ref ch, 0f, 0f, false);
        ch.Psi = Mathf.Lerp(-0.38f, 0.6f, e);
        ch.Phi = 0.28f * e;
        ch.Theta = 0.05f;
        var ankle = new Vector3(-0.15f, AnkleHeight, -0.20f).Lerp(new Vector3(-0.30f, 0.92f, 0.46f), DSm(e));
        Foot(ref ch, Rear, ankle.X, ankle.Y, ankle.Z, 1.4f * e, 1f);
        HeelUp(ref ch, Lead, 0.05f * e);
    }

    /// <summary>In the air: knees tucked under, the guard up.</summary>
    private static void FightAir(ref DanceCh ch)
    {
        FightGuard(ref ch, 0f, 0f, false);
        Foot(ref ch, Lead, 0.12f, AnkleHeight + 0.30f, 0.18f, 0.1f, 1f);
        Foot(ref ch, Rear, -0.12f, AnkleHeight + 0.22f, -0.05f, 0.3f, 1f);
    }

    /// <summary>Hit: the head snaps back and the arms fly open, recovering over the stun (<paramref name="p"/> 0..1).</summary>
    private static void FightHit(ref DanceCh ch, float p)
    {
        FightGuard(ref ch, 0f, 0f, false);
        float k = 1f - DSm(p);
        ch.Theta = 0.10f - 0.36f * k;
        ch.ThN = -0.06f - 0.45f * k;
        ch.Py -= 0.05f * k;
        Aim(ref ch, Lead, Mathf.Lerp(-0.06f, 0.24f, k), Mathf.Lerp(0.10f, -0.05f, k), Mathf.Lerp(0.30f, 0.08f, k), HOut(Lead));
        Aim(ref ch, Rear, Mathf.Lerp(-0.10f, 0.24f, k), Mathf.Lerp(0.14f, -0.08f, k), Mathf.Lerp(0.17f, 0.05f, k), HOut(Rear));
    }

    /// <summary>Dazed, waiting to be finished: arms hanging, head down, swaying on the spot.</summary>
    private static void FightDazed(ref DanceCh ch, float p)
    {
        float sw = DSin(p * 2f);
        ch.Px = 0.05f * sw;
        ch.Py = -0.08f;
        ch.Phi = 0.12f * sw;
        ch.Theta = 0.18f;
        ch.ThN = 0.35f;
        ch.PhH = 0.25f * sw;
        Aim(ref ch, Lead, 0.06f, -0.48f, 0.06f, HLow(Lead));
        Aim(ref ch, Rear, 0.06f, -0.48f, 0.04f, HLow(Rear));
        Planted(ref ch, 0.14f, 0.3f);
    }

    /// <summary>Victory: one fist pumped at the sky, the other on the hip.</summary>
    private static void FightVictory(ref DanceCh ch, float p)
    {
        float pump = DDip(DFrac(p * 2f));
        Aim(ref ch, Rear, 0.08f, 0.40f + 0.06f * (1f - pump), 0.06f, HUp(Rear));
        Akimbo(ref ch, Lead);
        ch.Theta = -0.06f;
        ch.ThN = -0.2f;
        Planted(ref ch, 0.13f, 0.2f);
    }
}
