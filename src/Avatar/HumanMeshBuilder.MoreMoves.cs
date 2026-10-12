using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Standing moves added in #728, in the note's channel terms (<c>docs/notes/avatar/dance-moves.md</c>):
/// each fills a <see cref="DanceCh"/> from the beat clock for the standing or the moving variant,
/// like every move in <see cref="HumanMeshBuilder"/>.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary>
    /// Body roll (Chill, Pop, HipHop): a wave down the body once every two beats, the head leading,
    /// then the chest pushing forward, then the hips rolling after it; one hand slides down the
    /// chest with the wave, the other hangs loose and out.
    /// </summary>
    private static void BodyRoll(ref DanceCh ch, in Beat t, bool mv)
    {
        float p = DFrac(t.C * 0.5f);
        float chest = DSin(p), hips = DSin(p - 0.22f), head = DSin(p + 0.12f);
        ch.Theta = 0.10f + 0.20f * chest;
        ch.Pz = -0.06f * hips;
        ch.Py = -0.04f - 0.03f * DDip(t.B);
        ch.Tilt = 0.012f * hips;
        ch.ThN = -0.16f * head;
        ch.Phi = 0.04f * t.D;
        // the figure's right hand (rig-L) rides down the front of the chest with the roll
        Aim(ref ch, -1f, -0.04f, -0.12f - 0.16f * p, 0.25f + 0.04f * chest, new Vector3(-1f, -0.4f, -0.3f));
        Aim(ref ch, 1f, 0.20f, -0.38f + 0.05f * chest, 0.06f, HLow(1f));
        Planted(ref ch, 0.13f, 0.15f);
        HeelUp(ref ch, -1f, 0.03f * Mathf.Max(0f, chest)); HeelUp(ref ch, 1f, 0.03f * Mathf.Max(0f, chest));
        if (mv) MovingScale(ref ch, 0.6f, 0.7f);
    }

    /// <summary>
    /// Charleston (Folk, Pop): the heels swivel in and out on every beat while one leg kicks
    /// forward and back, the other on the next beat; the arms swing straight, opposite the kick.
    /// </summary>
    private static void Charleston(ref DanceCh ch, in Beat t, bool mv)
    {
        float swivel = DCos(t.C);                                // toes in on the beat, out between
        float side = (t.K & 1) == 0 ? -1f : 1f;                  // the kicking leg this beat
        float kick = DSin(t.B);                                  // forward on the first half, back on the second
        ch.Py = -0.05f - 0.04f * DDip(t.B);
        ch.Theta = 0.06f - 0.06f * kick;
        ch.Px = -0.03f * side;
        ch.Phi = 0.05f * side * Mathf.Abs(kick);
        ch.ThN = 0.08f * DDip(t.B);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            bool kicking = s == side;
            float z = kicking ? 0.28f * kick : 0f;
            float y = AnkleHeight + (kicking ? 0.10f * Mathf.Abs(kick) : 0f);
            Foot(ref ch, s, s * 0.12f, y, z, -0.6f * swivel, kicking ? Mathf.Abs(kick) : 0f);
            // straight arms swinging like pendulums, against the kick
            Aim(ref ch, s, 0.10f, -0.44f + 0.10f * Mathf.Abs(kick), 0.08f - 0.30f * kick * side * s, HLow(s));
        }
        if (mv) MovingScale(ref ch, 0.5f, 0.6f);
    }

    /// <summary>
    /// Skank (Rock: ska and punk): running on the spot twice a beat, knees high, leaning into it,
    /// the fists punching forward and down in turn against the knees, the head nodding each step.
    /// </summary>
    private static void Skank(ref DanceCh ch, in Beat t, bool mv)
    {
        float step = DFrac(t.C * 2f);
        ch.Py = -0.06f - 0.03f * DDip(step);
        ch.Theta = 0.24f;
        ch.ThN = 0.14f * DDip(step);
        ch.Psi = 0.12f * DSin(t.C);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float lift = Mathf.Max(0f, DSin(t.C + (s < 0f ? 0f : 0.5f)));
            Foot(ref ch, s, s * 0.12f, AnkleHeight + 0.20f * lift, 0.10f * lift, 0.1f, lift);
            // this fist punches out while the other knee is up
            float punch = Mathf.Max(0f, DSin(t.C + (s < 0f ? 0.5f : 0f)));
            SetArm(ref ch, s, new Vector3(s * 0.14f, -0.32f, 0.05f).Lerp(new Vector3(-s * 0.02f, -0.18f, 0.42f), punch),
                new Vector3(s, -0.4f, -0.6f));
        }
        if (mv) { float th = ch.Theta; MovingScale(ref ch, 0.6f, 1f); ch.Theta = th; }
    }

    /// <summary>
    /// Air drums (Rock): sticks on the hi-hat, a hand on each eighth note, the right heel on the
    /// kick drum on 1 and 3, and a big crash up high on 4; the head banging along.
    /// </summary>
    private static void AirDrums(ref DanceCh ch, in Beat t, bool mv)
    {
        ch.Py = -0.07f - 0.02f * DDip(t.B);
        ch.Theta = 0.14f;
        ch.ThN = 0.22f * DDip(t.B);
        ch.Phi = 0.03f * t.D;
        bool crash = t.K == 3 && t.B < 0.6f;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float hit = Mathf.Exp(-DFrac(t.C * 2f + (s < 0f ? 0f : 0.5f)) * 7f);
            if (crash && s < 0f)
                Aim(ref ch, s, 0.32f, 0.18f - 0.25f * t.B, 0.30f, HOut(s));     // up to the cymbal and down through it
            else
                Aim(ref ch, s, 0.16f, -0.18f - 0.10f * hit, 0.34f, new Vector3(s, -0.5f, -0.5f));
        }
        Planted(ref ch, 0.15f, 0.15f);
        HeelUp(ref ch, 1f, 0.05f * (1f - Mathf.Exp(-DFrac(t.C * 0.5f) * 9f)));
        if (mv) MovingScale(ref ch, 0.6f, 0.9f);
    }

    /// <summary>
    /// Shuffle (Electronic, "cutting shapes"): each beat a heel kicks out to one side and back,
    /// the standing foot swivelling; the body bounces on every half beat while both arms sweep
    /// across the front together in wide flat arcs, crossing at the middle.
    /// </summary>
    private static void Shuffle(ref DanceCh ch, in Beat t, bool mv)
    {
        float side = (t.K & 1) == 0 ? -1f : 1f;
        float kick = Air(t.B, 0.05f, 0.6f);
        ch.Py = -0.05f - 0.04f * DDip(DFrac(t.C * 2f));
        ch.Theta = 0.10f;
        ch.Px = -0.03f * side * kick;
        ch.Psi = 0.18f * DSin(t.C * 0.5f);
        ch.ThN = 0.08f * DDip(DFrac(t.C * 2f));
        Foot(ref ch, side, side * (0.14f + 0.22f * kick), AnkleHeight + 0.06f * kick, 0.04f, side * 0.7f * kick, kick);
        Foot(ref ch, -side, -side * 0.12f, AnkleHeight, 0f, 0.5f * (1f - 2f * kick), 0f);
        float sweep = DSin(t.C * 0.5f);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            AimO(ref ch, s, 0.32f * sweep + s * 0.10f * Mathf.Abs(sweep), -0.10f + 0.06f * Mathf.Abs(sweep), 0.32f, new Vector3(s, -0.3f, -0.6f));
        }
        if (mv) MovingScale(ref ch, 0.5f, 0.8f);
    }

    /// <summary>
    /// The wop (HipHop, old school): a bounce on every beat, both arms swinging together from side
    /// to side at the waist with the elbows bent, the shoulders rolling with them and the hips going
    /// the other way.
    /// </summary>
    private static void Wop(ref DanceCh ch, in Beat t, bool mv)
    {
        float swing = DSin(t.C * 0.5f);
        float dip = DDip(t.B);
        ch.Py = -0.05f - 0.05f * dip;
        ch.Px = -0.05f * swing;
        ch.Theta = 0.10f + 0.05f * dip;
        ch.Psi = 0.22f * swing;
        ch.Phi = 0.06f * swing;
        ch.ShL = 0.035f * Mathf.Max(0f, swing);
        ch.ShR = 0.035f * Mathf.Max(0f, -swing);
        ch.ThN = 0.14f * dip;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            AimO(ref ch, s, 0.26f * swing + s * 0.16f, -0.26f + 0.06f * dip, 0.24f, new Vector3(s, -0.6f, -0.4f));
        }
        Planted(ref ch, 0.14f, 0.15f);
        HeelUp(ref ch, -1f, 0.03f * Mathf.Max(0f, swing)); HeelUp(ref ch, 1f, 0.03f * Mathf.Max(0f, -swing));
        if (mv) MovingScale(ref ch, 0.6f, 0.8f);
    }
}
