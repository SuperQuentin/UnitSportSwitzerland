using Godot;
using UnitSport.Combat;

namespace UnitSport.Avatar;

/// <summary>
/// Schwingen poses (#1269): the grip, the leans and the brace, the six throws, the thrown body, Bodenarbeit on top and underneath,
/// the winner's brush and the loser's dusting, drawn by the dance layer like a fight pose. A pose is a <see cref="DanceParams.Move"/>
/// at <see cref="SchwingMoves"/> + the replicated <c>FootPlayer.SchwingPose</c> (a <see cref="SchwingStance"/>, or a throw at
/// <see cref="SchwingStance.ThrowBase"/>). For a throw <see cref="DanceParams.BarPhase"/> is how far through its second the wrestler is
/// (the first 35 % is the telegraph the rival answers), so both peers show the same frame; for a stance it is a slow loop.
/// Each pose is one body: the pair is two of them standing 0.9 m apart on the bout's line, facing, the poses authored together.
/// A lying pose is laid flat by the body (<c>FootPlayer</c>'s down rotation), the pose only places the limbs.
/// The right hand (rig s = -1) is on the rival's belt, the left (s = +1) on the rolled trouser leg.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary><see cref="DanceParams.Move"/> values from here on are Schwingen poses: <c>SchwingMoves + FootPlayer.SchwingPose</c>.</summary>
    public const int SchwingMoves = 6000;

    private static DanceMove SchwingResolve(int pose)
    {
        var t = SchwingRules.ThrowOf(pose);
        if (t != SchwingThrow.None)
            return t switch
            {
                SchwingThrow.Kurz => DanceMove.SchwingKurz,
                SchwingThrow.Uebersprung => DanceMove.SchwingUebersprung,
                SchwingThrow.Hueften => DanceMove.SchwingHueften,
                SchwingThrow.Brienzer => DanceMove.SchwingBrienzer,
                SchwingThrow.Gammen => DanceMove.SchwingGammen,
                _ => DanceMove.SchwingWyberhaken,
            };
        return (SchwingStance)pose switch
        {
            SchwingStance.LeanIn => DanceMove.SchwingLeanIn,
            SchwingStance.LeanOut => DanceMove.SchwingLeanOut,
            SchwingStance.LeanLeft => DanceMove.SchwingLeanLeft,
            SchwingStance.LeanRight => DanceMove.SchwingLeanRight,
            SchwingStance.Brace => DanceMove.SchwingBrace,
            SchwingStance.Down => DanceMove.SchwingDown,
            SchwingStance.GroundTop => DanceMove.SchwingGroundTop,
            SchwingStance.GroundBottom => DanceMove.SchwingGroundBottom,
            SchwingStance.Stumble => DanceMove.SchwingStumble,
            SchwingStance.Brush => DanceMove.SchwingBrush,
            SchwingStance.Dusted => DanceMove.SchwingDusted,
            _ => DanceMove.SchwingGrip,
        };
    }

    private static void EvalSchwing(DanceMove move, in Beat t, bool mv, ref DanceCh ch)
    {
        float p = t.R;
        switch (move)
        {
            case DanceMove.SchwingGrip: SchwingGrip(ref ch, DSin(p), 0.22f, 0f, 0f, 0f); break;
            case DanceMove.SchwingLeanIn: SchwingGrip(ref ch, DSin(p * 2f), 0.42f, 0f, 0f, 0.05f); break;
            case DanceMove.SchwingLeanOut: SchwingGrip(ref ch, DSin(p * 2f), -0.04f, 0f, 0f, -0.05f); break;
            case DanceMove.SchwingLeanLeft: SchwingGrip(ref ch, DSin(p * 2f), 0.22f, 0.26f, 0.07f, 0f); break;
            case DanceMove.SchwingLeanRight: SchwingGrip(ref ch, DSin(p * 2f), 0.22f, -0.26f, -0.07f, 0f); break;
            case DanceMove.SchwingBrace: SchwingBrace(ref ch, DSin(p)); break;
            case DanceMove.SchwingDown: SchwingLying(ref ch, p, false); break;
            case DanceMove.SchwingGroundBottom: SchwingLying(ref ch, p, true); break;
            case DanceMove.SchwingGroundTop: SchwingKneeling(ref ch, p); break;
            case DanceMove.SchwingStumble: SchwingStumble(ref ch, p); break;
            case DanceMove.SchwingBrush: SchwingBrush(ref ch, p); break;
            case DanceMove.SchwingDusted: SchwingDusted(ref ch, p); break;
            case DanceMove.SchwingKurz: SchwingThrowPose(ref ch, SchwingThrow.Kurz, p); break;
            case DanceMove.SchwingUebersprung: SchwingThrowPose(ref ch, SchwingThrow.Uebersprung, p); break;
            case DanceMove.SchwingHueften: SchwingThrowPose(ref ch, SchwingThrow.Hueften, p); break;
            case DanceMove.SchwingBrienzer: SchwingThrowPose(ref ch, SchwingThrow.Brienzer, p); break;
            case DanceMove.SchwingGammen: SchwingThrowPose(ref ch, SchwingThrow.Gammen, p); break;
            case DanceMove.SchwingWyberhaken: SchwingThrowPose(ref ch, SchwingThrow.Wyberhaken, p); break;
        }
        if (mv) MovingScale(ref ch, 0.4f, 1f);
    }

    /// <summary>
    /// The grip: knees bent, chest forward, the right hand reaching to the rival's belt, the left down to the rolled trouser leg.
    /// <paramref name="lean"/> is the chest's pitch, <paramref name="roll"/> a sideways tilt with <paramref name="shift"/> the hips going
    /// the other way, <paramref name="reach"/> how far the arms stretch.
    /// </summary>
    private static void SchwingGrip(ref DanceCh ch, float sway, float lean, float roll, float shift, float reach)
    {
        ch.Theta = lean;
        ch.Phi = roll;
        ch.Px = shift + 0.012f * sway;
        ch.Py = -0.10f - 0.012f * sway;
        ch.ThN = -0.22f;
        ch.Psi = 0f;
        Aim(ref ch, -1f, 0.04f, -0.12f, 0.50f + reach, new Vector3(-0.5f, -0.6f, 0f));
        Aim(ref ch, 1f, 0.10f, -0.50f, 0.36f + reach, new Vector3(0.6f, -0.6f, 0f));
        Foot(ref ch, 1f, 0.20f, AnkleHeight, 0.10f, 0.12f, 0f);
        Foot(ref ch, -1f, -0.20f, AnkleHeight, -0.10f, 0.12f, 0f);
    }

    /// <summary>Braced: dropped low, hips back, the knees wide, both hands clamped on the grip.</summary>
    private static void SchwingBrace(ref DanceCh ch, float sway)
    {
        SchwingGrip(ref ch, sway, 0.34f, 0f, 0f, -0.02f);
        ch.Py = -0.30f;
        ch.Pz = -0.08f;
        Foot(ref ch, 1f, 0.28f, AnkleHeight, 0.14f, 0.2f, 0f);
        Foot(ref ch, -1f, -0.28f, AnkleHeight, -0.14f, 0.2f, 0f);
    }

    /// <summary>
    /// A throw (<paramref name="p"/> 0..1 over its second): the telegraph (the first 35 %) is the wind-up each throw shows differently,
    /// then the throw itself, then the recovery.
    /// </summary>
    private static void SchwingThrowPose(ref DanceCh ch, SchwingThrow throwKind, float p)
    {
        float wind = DSm(p / 0.35f);
        float go = DSm((p - 0.35f) / 0.3f);
        float back = DSm((p - 0.7f) / 0.3f);
        float e = wind * (1f - go) + go * (1f - back);   // 0 grip, 1 at the throw
        SchwingGrip(ref ch, 0f, 0.22f, 0f, 0f, 0f);
        switch (throwKind)
        {
            case SchwingThrow.Kurz:
                // pulls in, then lifts: the knees dip, the chest comes back up and over
                ch.Py = -0.10f - 0.20f * wind * (1f - go) + 0.04f * go;
                ch.Theta = Mathf.Lerp(0.22f, 0.50f, wind) - 0.7f * go * (1f - back);
                Aim(ref ch, -1f, 0.04f, Mathf.Lerp(-0.12f, 0.05f, e), Mathf.Lerp(0.50f, 0.30f, e), new Vector3(-0.5f, -0.4f, 0f));
                Aim(ref ch, 1f, 0.10f, Mathf.Lerp(-0.50f, -0.25f, e), Mathf.Lerp(0.36f, 0.22f, e), new Vector3(0.6f, -0.4f, 0f));
                break;
            case SchwingThrow.Uebersprung:
                // steps a leg over the rival's, hips turning
                ch.Psi = 0.55f * e;
                ch.Phi = 0.16f * e;
                Foot(ref ch, 1f, Mathf.Lerp(0.20f, -0.02f, e), AnkleHeight + 0.22f * e, Mathf.Lerp(0.10f, 0.45f, e), 0.2f, 1f);
                break;
            case SchwingThrow.Hueften:
                // hip under the rival, the turn of the whole chest
                ch.Psi = Mathf.Lerp(0f, 0.95f, e);
                ch.Phi = 0.28f * go;
                ch.Py = -0.10f - 0.22f * wind * (1f - go);
                ch.Theta = Mathf.Lerp(0.22f, 0.55f, e);
                Aim(ref ch, -1f, 0.10f, Mathf.Lerp(-0.12f, 0.10f, e), Mathf.Lerp(0.50f, 0.20f, e), new Vector3(-0.5f, -0.4f, 0f));
                break;
            case SchwingThrow.Brienzer:
                // the arm over the neck, then back and down
                Aim(ref ch, -1f, Mathf.Lerp(0.04f, 0.12f, e), Mathf.Lerp(-0.12f, 0.42f, e), Mathf.Lerp(0.50f, 0.38f, e), new Vector3(-0.2f, 0.4f, -0.2f));
                ch.Theta = Mathf.Lerp(0.22f, -0.30f, go * (1f - back)) + 0.2f * wind * (1f - go);
                ch.Phi = -0.18f * e;
                break;
            case SchwingThrow.Gammen:
                // the leg hooked behind the rival's, kicking forward
                Foot(ref ch, -1f, Mathf.Lerp(-0.20f, -0.12f, e), AnkleHeight + 0.30f * e, Mathf.Lerp(-0.10f, 0.50f, e), 0.1f, 1f);
                ch.Theta = Mathf.Lerp(0.22f, 0.08f, e);
                ch.Psi = 0.25f * e;
                break;
            default:
                // the inside leg: the left foot hooks across, the body leaning away
                Foot(ref ch, 1f, Mathf.Lerp(0.20f, -0.10f, e), AnkleHeight + 0.26f * e, Mathf.Lerp(0.10f, 0.48f, e), 0.0f, 1f);
                ch.Theta = Mathf.Lerp(0.22f, -0.10f, e);
                ch.Phi = -0.14f * e;
                break;
        }
    }

    /// <summary>
    /// Flat on the back in the sawdust (the body lays itself down): arms thrown wide. <paramref name="bridging"/> is Bodenarbeit
    /// underneath: both hands up pushing at the rival, bucking on a loop.
    /// </summary>
    private static void SchwingLying(ref DanceCh ch, float p, bool bridging)
    {
        ch.Theta = 0f;
        ch.ThN = 0.12f;
        ch.Py = -0.04f;
        float buck = bridging ? DSin(p * 3f) : 0f;
        ch.Phi = 0.06f * buck;
        if (bridging)
        {
            Aim(ref ch, -1f, 0.10f, 0.30f + 0.06f * buck, 0.40f, new Vector3(-0.5f, 0.2f, 0f));
            Aim(ref ch, 1f, 0.10f, 0.30f - 0.06f * buck, 0.40f, new Vector3(0.5f, 0.2f, 0f));
        }
        else
        {
            Aim(ref ch, -1f, 0.40f, -0.10f, 0.10f, HOut(-1f));
            Aim(ref ch, 1f, 0.40f, -0.10f, 0.10f, HOut(1f));
        }
        Foot(ref ch, 1f, 0.16f, AnkleHeight, 0.04f, 0.2f, 0f);
        Foot(ref ch, -1f, -0.16f, AnkleHeight, 0.04f, 0.2f, 0f);
    }

    /// <summary>Bodenarbeit on top: down on the knees over the rival, one hand on the trousers, the other driving the shoulder down.</summary>
    private static void SchwingKneeling(ref DanceCh ch, float p)
    {
        float push = DSin(p * 3f);
        ch.Py = -0.46f;
        ch.Theta = 0.70f;
        ch.ThN = -0.35f;
        ch.Px = 0.02f * push;
        Aim(ref ch, -1f, 0.06f, -0.34f, 0.46f + 0.05f * push, new Vector3(-0.5f, -0.6f, 0f));
        Aim(ref ch, 1f, 0.12f, -0.36f, 0.34f - 0.05f * push, new Vector3(0.6f, -0.6f, 0f));
        // the knees low and apart behind the hands
        Foot(ref ch, 1f, 0.22f, AnkleHeight + 0.14f, -0.30f, 0.1f, 1f);
        Foot(ref ch, -1f, -0.22f, AnkleHeight + 0.14f, -0.30f, 0.1f, 1f);
    }

    /// <summary>An answered throw: the thrower is pulled forward off its feet, arms out, recovering over 0.6 s.</summary>
    private static void SchwingStumble(ref DanceCh ch, float p)
    {
        float k = 1f - DSm(p);
        SchwingGrip(ref ch, 0f, 0.22f + 0.5f * k, 0.12f * k, 0.05f * k, 0.10f * k);
        ch.Py -= 0.12f * k;
        Aim(ref ch, -1f, Mathf.Lerp(0.04f, 0.30f, k), Mathf.Lerp(-0.12f, 0.05f, k), Mathf.Lerp(0.50f, 0.40f, k), HOut(-1f));
        Aim(ref ch, 1f, Mathf.Lerp(0.10f, 0.30f, k), Mathf.Lerp(-0.50f, -0.1f, k), Mathf.Lerp(0.36f, 0.30f, k), HOut(1f));
    }

    /// <summary>The winner brushing the sawdust off the loser's back: bent over, one hand sweeping, the other on the knee.</summary>
    private static void SchwingBrush(ref DanceCh ch, float p)
    {
        float sweep = DSin(p * 5f);
        ch.Theta = 0.55f;
        ch.ThN = -0.2f;
        ch.Py = -0.10f;
        Aim(ref ch, -1f, 0.10f + 0.08f * sweep, -0.40f, 0.42f, new Vector3(-0.5f, -0.6f, 0f));
        Aim(ref ch, 1f, 0.12f, -0.52f, 0.12f, HLow(1f));
        Planted(ref ch, 0.16f, 0.12f);
    }

    /// <summary>The loser, back on its feet: hands patting the sawdust off the thighs, head a little down.</summary>
    private static void SchwingDusted(ref DanceCh ch, float p)
    {
        float pat = DSin(p * 4f);
        ch.Theta = 0.18f;
        ch.ThN = 0.2f;
        Aim(ref ch, -1f, 0.08f, -0.46f + 0.05f * pat, 0.08f, HLow(-1f));
        Aim(ref ch, 1f, 0.08f, -0.46f - 0.05f * pat, 0.08f, HLow(1f));
        Planted(ref ch, 0.14f, 0.15f);
    }
}
