using Godot;
using UnitSport.Combat;

namespace UnitSport.Avatar;

/// <summary>
/// Aura-battle poses (#907): the waiting and planning stances, a stumble, the verdict's victory and
/// defeat, and each move of <see cref="AuraRules"/>, drawn by the dance layer like a fight pose.
/// An aura pose is a <see cref="DanceParams.Move"/> at <see cref="AuraMoves"/> + the replicated
/// <c>FootPlayer.AuraPose</c> (an <see cref="AuraStance"/>, or a move at <see cref="AuraStance.MoveBase"/>).
/// For a move, <see cref="DanceParams.BarPhase"/> is how far through its beats the performer is
/// (0..1), so every peer hits the same frame of it on the same downbeat; for a stance it loops.
/// Moves the dances already have (the Griddy) are those dances, beat for beat.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary><see cref="DanceParams.Move"/> values from here on are aura poses: <c>AuraMoves + FootPlayer.AuraPose</c>.</summary>
    public const int AuraMoves = 5000;

    private static DanceMove AuraResolve(int pose)
    {
        var move = AuraRules.MoveOf(pose);
        if (move != AuraMove.None)
            return move switch
            {
                AuraMove.StandTall => DanceMove.AuraStandTall,
                AuraMove.ArmsCrossed => DanceMove.AuraArmsCrossed,
                AuraMove.PointToSky => DanceMove.AuraPointToSky,
                AuraMove.StareDown => DanceMove.AuraStareDown,
                AuraMove.Sunglasses => DanceMove.AuraSunglasses,
                AuraMove.Yawn => DanceMove.AuraYawn,
                AuraMove.SlowWalk => DanceMove.AuraSlowWalk,
                AuraMove.Griddy => DanceMove.Griddy,
                AuraMove.Siuuu => DanceMove.AuraSiuuu,
                AuraMove.Mewing => DanceMove.AuraMewing,
                AuraMove.SixSeven => DanceMove.AuraSixSeven,
                AuraMove.Lasso => DanceMove.AuraLasso,
                _ => DanceMove.AuraCutTheRope,
            };
        return (AuraStance)pose switch
        {
            AuraStance.Planning => DanceMove.AuraPlanning,
            AuraStance.Stumble => DanceMove.AuraStumble,
            AuraStance.Victory => DanceMove.AuraVictory,
            AuraStance.Defeat => DanceMove.AuraDefeat,
            _ => DanceMove.AuraWaiting,
        };
    }

    private static void EvalAura(DanceMove move, in Beat t, bool mv, ref DanceCh ch)
    {
        float p = t.R;
        switch (move)
        {
            case DanceMove.AuraWaiting: AuraCrossed(ref ch, DSin(p), 0f); break;
            case DanceMove.AuraPlanning: AuraPlanning(ref ch, p); break;
            case DanceMove.AuraStumble: AuraStumble(ref ch, p); break;
            case DanceMove.AuraVictory: FightVictory(ref ch, p); break;
            case DanceMove.AuraDefeat: AuraDefeat(ref ch, p); break;
            case DanceMove.AuraStandTall: AuraStandTall(ref ch, p); break;
            case DanceMove.AuraArmsCrossed: AuraCrossed(ref ch, 0f, DSm(p * 3f)); break;
            case DanceMove.AuraPointToSky: AuraPointToSky(ref ch, p); break;
            case DanceMove.AuraStareDown: AuraStareDown(ref ch, p); break;
            case DanceMove.AuraSunglasses: AuraSunglasses(ref ch, p); break;
            case DanceMove.AuraYawn: AuraYawn(ref ch, p); break;
            case DanceMove.AuraSlowWalk: AuraSlowWalk(ref ch, p); break;
            case DanceMove.AuraSiuuu: AuraSiuuu(ref ch, p); break;
            case DanceMove.AuraMewing: AuraMewing(ref ch, p); break;
            case DanceMove.AuraSixSeven: AuraSixSeven(ref ch, p); break;
            case DanceMove.AuraLasso: AuraLasso(ref ch, p); break;
            case DanceMove.AuraCutTheRope: AuraCutTheRope(ref ch, p); break;
        }
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    /// <summary>Weight settled, chin up: the base the calm moves start from.</summary>
    private static void AuraTall(ref DanceCh ch)
    {
        ch.Theta = -0.04f;
        ch.ThN = -0.12f;
        Planted(ref ch, 0.14f, 0.2f);
    }

    /// <summary>Arms folded across the chest; <paramref name="sway"/> a slow weight shift, <paramref name="lean"/> a lean back.</summary>
    private static void AuraCrossed(ref DanceCh ch, float sway, float lean)
    {
        AuraTall(ref ch);
        ch.Px = 0.02f * sway;
        ch.Phi = 0.03f * sway;
        ch.Theta = -0.04f - 0.06f * lean;
        ch.ThN = -0.12f - 0.08f * lean;
        Aim(ref ch, -1f, -0.14f, -0.20f, 0.20f, Elbow(-1f));
        Aim(ref ch, 1f, -0.12f, -0.16f, 0.22f, Elbow(1f));
    }

    /// <summary>Thinking it over: one hand on the chin, the other holding the elbow; the head tilts.</summary>
    private static void AuraPlanning(ref DanceCh ch, float p)
    {
        AuraTall(ref ch);
        ch.ThN = 0.10f;
        ch.PhH = 0.12f * DSin(p);
        Aim(ref ch, -1f, -0.08f, 0.02f, 0.20f, new Vector3(-0.5f, -0.8f, 0f));
        Aim(ref ch, 1f, -0.14f, -0.24f, 0.16f, Elbow(1f));
    }

    /// <summary>A missed cut: a stagger sideways, arms out for balance, recovering over the move.</summary>
    private static void AuraStumble(ref DanceCh ch, float p)
    {
        float k = 1f - DSm(p * 1.6f);
        Planted(ref ch, 0.16f, 0.2f);
        ch.Px = 0.08f * k;
        ch.Phi = -0.22f * k;
        ch.Theta = 0.12f * k;
        ch.ThN = 0.2f * k;
        Aim(ref ch, -1f, 0.30f * k + 0.05f, -0.10f * k - 0.40f * (1f - k), 0.05f, HOut(-1f));
        Aim(ref ch, 1f, 0.32f * k + 0.05f, -0.05f * k - 0.40f * (1f - k), 0.05f, HOut(1f));
        Lift(ref ch, 1f, 0.10f * k);
    }

    /// <summary>Lost: head down, hands in the pockets.</summary>
    private static void AuraDefeat(ref DanceCh ch, float p)
    {
        Planted(ref ch, 0.11f, 0.1f);
        ch.Theta = 0.10f;
        ch.ThN = 0.40f + 0.04f * DSin(p);
        ch.ShL = ch.ShR = -0.03f;
        Aim(ref ch, -1f, 0.02f, -0.46f, 0.02f, HLow(-1f));
        Aim(ref ch, 1f, 0.02f, -0.46f, 0.02f, HLow(1f));
    }

    /// <summary>Stand tall: chest out, shoulders back, fists on the hips.</summary>
    private static void AuraStandTall(ref DanceCh ch, float p)
    {
        AuraTall(ref ch);
        float up = DSm(p * 3f);
        ch.Py = 0.02f * up;
        ch.Theta = -0.08f * up;
        ch.ShL = ch.ShR = -0.02f * up;
        Akimbo(ref ch, -1f);
        Akimbo(ref ch, 1f);
    }

    /// <summary>Point to the sky: one finger up, slowly, the gaze following it.</summary>
    private static void AuraPointToSky(ref DanceCh ch, float p)
    {
        AuraTall(ref ch);
        float up = DSm(p * 2f);
        Aim(ref ch, 1f, Mathf.Lerp(0.05f, 0.06f, up), Mathf.Lerp(-0.30f, 0.48f, up), Mathf.Lerp(0.15f, 0.08f, up), HUp(1f));
        Akimbo(ref ch, -1f);
        ch.ThN = -0.12f - 0.30f * up;
        ch.Theta = -0.04f - 0.06f * up;
    }

    /// <summary>The stare down: a slow lean in, chin low, eyes up at the rival; arms loose at the sides.</summary>
    private static void AuraStareDown(ref DanceCh ch, float p)
    {
        Planted(ref ch, 0.15f, 0.15f);
        float lean = DSm(p * 2f) * (1f - DSm((p - 0.8f) * 5f));
        ch.Theta = 0.22f * lean;
        ch.ThN = 0.10f * lean;
        ch.Pz = 0.04f * lean;
        Aim(ref ch, -1f, 0.08f, -0.47f, 0.04f, HLow(-1f));
        Aim(ref ch, 1f, 0.08f, -0.47f, 0.04f, HLow(1f));
    }

    /// <summary>Sunglasses: a hand to the face, the shades pushed up the nose, then a nod.</summary>
    private static void AuraSunglasses(ref DanceCh ch, float p)
    {
        AuraTall(ref ch);
        float reach = DSm(p * 3f), push = DSm((p - 0.35f) * 4f), nod = DSin(DRamp((p - 0.6f) * 2.5f) * 0.5f);
        Aim(ref ch, 1f, Mathf.Lerp(0.06f, -0.12f, reach), Mathf.Lerp(-0.40f, 0.16f + 0.04f * push, reach),
            Mathf.Lerp(0.05f, 0.22f, reach) * (1f - 0.3f * DSm((p - 0.7f) * 4f)), Elbow(1f));
        Akimbo(ref ch, -1f);
        ch.ThN = -0.10f + 0.18f * nod;
    }

    /// <summary>The yawn: arms stretched up, head back, a hand patting the mouth. Unimpressed.</summary>
    private static void AuraYawn(ref DanceCh ch, float p)
    {
        AuraTall(ref ch);
        float up = DSm(p * 3f) * (1f - DSm((p - 0.55f) * 4f)), pat = DSm((p - 0.6f) * 4f);
        Aim(ref ch, -1f, 0.10f, Mathf.Lerp(-0.40f, 0.44f, up), 0.04f, HUp(-1f));
        Aim(ref ch, 1f, Mathf.Lerp(0.10f, -0.10f, pat), Mathf.Lerp(-0.40f, 0.44f, up) * (1f - pat) + 0.12f * pat,
            Mathf.Lerp(0.04f, 0.22f, pat), pat > 0.5f ? Elbow(1f) : HUp(1f));
        ch.ThN = -0.30f * up;
        ch.Theta = -0.08f * up;
    }

    /// <summary>The slow walk: an easy strut on the spot, shoulders rolling, arms swinging low.</summary>
    private static void AuraSlowWalk(ref DanceCh ch, float p)
    {
        // two steps a bar, four bars a move at most: the stride runs on the progress twice per beat pair
        float c = p * 4f, sw = DSin(c * 0.5f), lift = Mathf.Max(0f, DSin(c));
        ch.Psi = 0.12f * sw;
        ch.Phi = 0.04f * sw;
        ch.Theta = -0.03f;
        ch.ThN = -0.10f;
        ch.Py = -0.015f * Mathf.Abs(DSin(c));
        Foot(ref ch, -1f, -0.11f, AnkleHeight + 0.06f * Mathf.Max(0f, -sw) * lift, 0.14f * sw, 0.15f, 0.4f);
        Foot(ref ch, 1f, 0.11f, AnkleHeight + 0.06f * Mathf.Max(0f, sw) * lift, -0.14f * sw, 0.15f, 0.4f);
        Aim(ref ch, -1f, 0.08f, -0.46f, -0.10f * sw, HLow(-1f));
        Aim(ref ch, 1f, 0.08f, -0.46f, 0.10f * sw, HLow(1f));
    }

    /// <summary>
    /// Siuuu: a run-up on the spot, the jump with a half twist, and the landing — feet apart, arms
    /// thrown down and out, chest up — held to the end.
    /// </summary>
    private static void AuraSiuuu(ref DanceCh ch, float p)
    {
        Planted(ref ch, 0.13f, 0.15f);
        if (p < 0.35f)
        {
            float c = p / 0.35f * 4f, sw = DSin(c * 0.5f);
            ch.Theta = 0.12f;
            ch.Py = -0.02f * Mathf.Abs(DSin(c));
            Foot(ref ch, -1f, -0.10f, AnkleHeight + 0.10f * Mathf.Max(0f, -sw), 0.10f * sw, 0.1f, 1f);
            Foot(ref ch, 1f, 0.10f, AnkleHeight + 0.10f * Mathf.Max(0f, sw), -0.10f * sw, 0.1f, 1f);
            Aim(ref ch, -1f, 0.10f, -0.30f, 0.20f * sw, HLow(-1f));
            Aim(ref ch, 1f, 0.10f, -0.30f, -0.20f * sw, HLow(1f));
            return;
        }
        if (p < 0.55f)
        {
            float j = (p - 0.35f) / 0.2f, air = DSin(j * 0.5f);
            ch.Py = 0.30f * air;
            ch.Psi = 0.9f * DSin(j * 0.5f) * (1f - j);
            Lift(ref ch, -1f, 0.25f * air);
            Lift(ref ch, 1f, 0.25f * air);
            Aim(ref ch, -1f, 0.20f, 0.10f, 0.10f, HOut(-1f));
            Aim(ref ch, 1f, 0.20f, 0.10f, 0.10f, HOut(1f));
            return;
        }
        // the landing, held: wide, arms down and out, chest and chin up
        float land = DSm((p - 0.55f) * 8f);
        Foot(ref ch, -1f, -0.24f, AnkleHeight, 0.02f, 0.4f, 0f);
        Foot(ref ch, 1f, 0.24f, AnkleHeight, 0.02f, 0.4f, 0f);
        ch.Py = -0.10f * land;
        ch.Theta = -0.18f * land;
        ch.ThN = -0.30f * land;
        Aim(ref ch, -1f, 0.34f, -0.30f, 0.10f, HOut(-1f));
        Aim(ref ch, 1f, 0.34f, -0.30f, 0.10f, HOut(1f));
    }

    /// <summary>Mewing: chin up, a finger traced along the jawline, slowly, twice.</summary>
    private static void AuraMewing(ref DanceCh ch, float p)
    {
        AuraTall(ref ch);
        float reach = DSm(p * 4f), trace = DSin(p * 2f);
        ch.ThN = -0.25f * reach;
        ch.PhH = 0.08f * reach;
        Aim(ref ch, 1f, Mathf.Lerp(0.06f, -0.04f + 0.06f * trace, reach), Mathf.Lerp(-0.40f, 0.10f, reach),
            Mathf.Lerp(0.05f, 0.16f, reach), Elbow(1f));
        Aim(ref ch, -1f, 0.08f, -0.46f, 0.03f, HLow(-1f));
    }

    /// <summary>Six seven: both palms up, weighing the air, the hands see-sawing on each half beat.</summary>
    private static void AuraSixSeven(ref DanceCh ch, float p)
    {
        AuraTall(ref ch);
        float saw = DSin(p * 4f);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            Aim(ref ch, s, 0.16f, -0.22f + 0.10f * s * saw, 0.30f, HLow(s));
        }
        ch.Phi = 0.04f * saw;
        ch.PhH = -0.10f * saw;
        ch.Py = -0.02f * Mathf.Abs(saw);
    }

    /// <summary>The lasso: the rope swung round over the head, thrown at the rival, then hauled in hand over hand.</summary>
    private static void AuraLasso(ref DanceCh ch, float p)
    {
        Planted(ref ch, 0.16f, 0.2f);
        ch.Theta = 0.04f;
        if (p < 0.5f)
        {
            // two turns of the rope over the head
            float a = p / 0.5f * 2f;
            Aim(ref ch, 1f, 0.10f + 0.12f * DCos(a), 0.44f, 0.08f + 0.12f * DSin(a), HUp(1f));
            Akimbo(ref ch, -1f);
            ch.ThN = -0.15f;
            return;
        }
        if (p < 0.62f)
        {
            float th = DSm((p - 0.5f) / 0.12f);
            Aim(ref ch, 1f, Mathf.Lerp(0.10f, 0.02f, th), Mathf.Lerp(0.44f, 0.12f, th), Mathf.Lerp(0.08f, 0.48f, th), HOut(1f));
            Akimbo(ref ch, -1f);
            ch.Theta = 0.04f + 0.12f * th;
            return;
        }
        // hauling in: hand over hand
        float h = (p - 0.62f) / 0.38f * 3f, pull = DSin(h);
        ch.Theta = -0.10f;
        ch.Px = -0.02f * pull;
        Aim(ref ch, 1f, -0.02f, 0.02f + 0.06f * pull, 0.36f - 0.14f * pull, Elbow(1f));
        Aim(ref ch, -1f, -0.02f, 0.02f - 0.06f * pull, 0.36f + 0.14f * pull, Elbow(-1f));
        Foot(ref ch, 1f, 0.16f, AnkleHeight, 0.10f, 0.2f, 0f);
        Foot(ref ch, -1f, -0.16f, AnkleHeight, -0.14f, 0.2f, 0f);
    }

    /// <summary>Cut the rope: scissor fingers raised, two snips, then the arms fall free.</summary>
    private static void AuraCutTheRope(ref DanceCh ch, float p)
    {
        AuraTall(ref ch);
        float up = DSm(p * 4f) * (1f - DSm((p - 0.75f) * 4f)), snip = Mathf.Abs(DSin(p * 4f));
        Aim(ref ch, 1f, Mathf.Lerp(0.06f, 0.0f, up), Mathf.Lerp(-0.40f, 0.20f, up), Mathf.Lerp(0.05f, 0.34f, up), Elbow(1f));
        Aim(ref ch, -1f, Mathf.Lerp(0.06f, 0.02f + 0.04f * snip, up), Mathf.Lerp(-0.40f, 0.18f, up), Mathf.Lerp(0.05f, 0.30f, up), Elbow(-1f));
        ch.ThN = -0.10f - 0.10f * up;
        float fall = DSm((p - 0.75f) * 4f);
        ch.ShL = ch.ShR = -0.03f * fall;
    }
}
