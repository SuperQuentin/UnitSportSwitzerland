using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Emotes (#404): any dance or gesture, picked on the emote wheel at any time, with or without
/// music. An emote is a <see cref="DanceParams.Move"/> at <see cref="EmoteMoves"/> + its catalog
/// index, outside every style's table, so the same dance layer draws it; the new moves of #404
/// live here too (spec: docs/notes/avatar/dance-moves.md).
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary><see cref="DanceParams.Move"/> values from here on are emotes: <c>EmoteMoves + catalog index</c>.</summary>
    public const int EmoteMoves = 2000;

    /// <summary>How many emotes one page of the wheel shows.</summary>
    public const int EmotesPerPage = 10;

    /// <summary>The wheel's pages, in catalog order: page p is emotes p*10 .. p*10+9.</summary>
    public static readonly string[] EmotePages = { "Emotes", "Dances", "More dances" };

    /// <summary>The catalog. Append only: the index is replicated (<c>FootPlayer.DanceId</c> - 2).</summary>
    private static readonly (string Name, DanceMove Move)[] EmoteTable =
    {
        // Emotes
        ("Wave", DanceMove.Wave), ("Cheer", DanceMove.Cheer), ("Salute", DanceMove.Salute),
        ("Shrug", DanceMove.Shrug), ("Clap", DanceMove.ClapBackbeat), ("Dab", DanceMove.Dab),
        ("Fist pump", DanceMove.FistPump), ("Headbang", DanceMove.Headbang),
        ("Air guitar", DanceMove.AirGuitar), ("Arm wave", DanceMove.ArmWave),
        // Dances
        ("Floss", DanceMove.Floss), ("Macarena", DanceMove.Macarena), ("YMCA", DanceMove.Ymca),
        ("Chicken dance", DanceMove.ChickenDance), ("Cabbage patch", DanceMove.CabbagePatch),
        ("Swim", DanceMove.SwimDance), ("Robot", DanceMove.Robot), ("Moonwalk", DanceMove.Moonwalk),
        ("Griddy", DanceMove.Griddy), ("Carlton", DanceMove.Carlton),
        // More dances
        ("Gangnam style", DanceMove.GangnamStyle), ("Orange justice", DanceMove.OrangeJustice),
        ("Running man", DanceMove.RunningMan), ("Twerk", DanceMove.Twerk), ("Sprinkler", DanceMove.Sprinkler),
        ("Disco point", DanceMove.DiscoPoint), ("T-step", DanceMove.TStep),
        ("Shoulder lean", DanceMove.ShoulderLean), ("Pogo", DanceMove.Pogo), ("Rat dance", DanceMove.RatSwing),
    };

    public static int EmoteCount => EmoteTable.Length;

    public static string EmoteName(int index) =>
        (uint)index < (uint)EmoteTable.Length ? EmoteTable[index].Name : "";

    private static DanceMove EmoteMove(int index) =>
        EmoteTable[Mathf.PosMod(index, EmoteTable.Length)].Move;

    // ---- the #404 moves ----------------------------------------------------------------------

    /// <summary>One YMCA letter for the arm on side <paramref name="s"/>: Y, M (hands on the head), C (both arms curved to +X), A.</summary>
    private static void YmcaLetter(int k, float s, out Vector3 off, out Vector3 hint)
    {
        switch (k & 3)
        {
            case 0: off = new Vector3(s * 0.30f, 0.38f, 0.05f); hint = HUp(s); break;
            case 1: off = new Vector3(-s * 0.10f, 0.34f, 0.04f); hint = new Vector3(s, 0.2f, 0f); break;
            case 2:
                if (s > 0f) { off = new Vector3(0.28f, 0.28f, 0.12f); hint = HUp(s); }
                else { off = new Vector3(0.24f, -0.04f, 0.30f); hint = HLow(s); }
                break;
            // A: straight arms meeting in a peak high over the head (M's hands rest on it, elbows out)
            default: off = new Vector3(-s * 0.15f, 0.47f, 0.06f); hint = HUp(s); break;
        }
    }

    /// <summary>YMCA: a letter on every beat, snapped in over 0.3 beat and held; the body leans into the C.</summary>
    private static void Ymca(ref DanceCh ch, in Beat t, bool mv)
    {
        float u = DSm(t.B / 0.3f), dip = DDip(t.B);
        int prev = (t.K + 3) & 3;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            YmcaLetter(prev, s, out var o0, out var h0);
            YmcaLetter(t.K, s, out var o1, out var h1);
            SetArm(ref ch, s, o0.Lerp(o1, u), h0.Lerp(h1, u));
        }
        float c = t.K == 2 ? u : t.K == 3 ? 1f - u : 0f;
        ch.Phi = 0.12f * c; ch.PhH = 0.10f * c;
        ch.Py = -0.03f * dip; ch.Theta = 0.02f; ch.ThN = -0.12f;
        Planted(ref ch, 0.14f, 0.15f);
        if (mv) MovingScale(ref ch, 0.7f, 1f);
    }

    /// <summary>One beat of the chicken dance: 0 beak, 1 wings, 2 tail wiggle, 3 claps; two of each per beat.</summary>
    private static void ChickenSection(ref DanceCh ch, int k, float b)
    {
        float twice = DDip(DFrac(2f * b));
        Planted(ref ch, 0.13f, 0.2f);
        switch (k & 3)
        {
            case 0:
                Aim(ref ch, -1f, -0.06f, 0.02f + 0.04f * twice, 0.42f, HLow(-1f));
                Aim(ref ch, 1f, -0.06f, 0.02f + 0.04f * twice, 0.42f, HLow(1f));
                ch.ThN = 0.05f * twice; ch.Py = -0.02f;
                break;
            case 1:
                for (int i = 0; i < 2; i++)
                {
                    float s = i * 2f - 1f;
                    Aim(ref ch, s, 0.06f, -0.06f, 0.10f, new Vector3(s, -0.2f + twice, -0.4f));
                }
                ch.ShL = ch.ShR = 0.04f * twice; ch.Py = -0.03f;
                break;
            case 2:
                float wig = DSin(2f * b);
                Aim(ref ch, -1f, 0.06f, -0.06f, 0.10f, new Vector3(-1f, 0.4f, -0.4f));
                Aim(ref ch, 1f, 0.06f, -0.06f, 0.10f, new Vector3(1f, 0.4f, -0.4f));
                ch.Py = -0.16f; ch.Theta = 0.30f; ch.ThN = -0.25f;
                ch.Px = 0.06f * wig; ch.Tilt = 0.02f * wig;
                Planted(ref ch, 0.16f, 0.3f);
                break;
            default:
                ClapArms(ref ch, twice * twice);
                ch.Py = -0.02f * twice;
                break;
        }
    }

    /// <summary>The chicken dance: beak, wings, tail feathers, claps, a beat each, flowing over 0.15 beat.</summary>
    private static void ChickenDance(ref DanceCh ch, in Beat t, bool mv)
    {
        var cur = ch;
        ChickenSection(ref cur, t.K, t.B);
        if (t.B < 0.15f)
        {
            var prev = ch;
            ChickenSection(ref prev, (t.K + 3) & 3, 0.999f);
            cur = Mix(prev, cur, DSm(t.B / 0.15f));
        }
        ch = cur;
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    /// <summary>Cabbage patch: fists together stirring a flat circle in front of the chest, one every two beats, the hips the other way.</summary>
    private static void CabbagePatch(ref DanceCh ch, in Beat t, bool mv)
    {
        float a = Mathf.Tau * t.C * 0.5f, sa = Mathf.Sin(a), ca = Mathf.Cos(a), dip = DDip(t.B);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            // the fists' common point, relative to the midline at shoulder height, a fist's width apart
            float x = 0.16f * sa + s * 0.05f, y = -0.12f + 0.04f * ca, z = 0.30f + 0.08f * ca;
            SetArm(ref ch, s, new Vector3(x - s * 0.18f, y, z), HOut(s));
        }
        ch.Px = -0.05f * sa; ch.Py = -0.08f - 0.03f * dip;
        ch.Theta = 0.10f; ch.Phi = 0.06f * sa; ch.Psi = 0.15f * sa; ch.ThN = 0.06f * dip;
        Planted(ref ch, 0.18f, 0.25f);
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    /// <summary>The swim (1960s): a front-crawl windmill, one arm per beat, the head turning to breathe.</summary>
    private static void SwimDance(ref DanceCh ch, in Beat t, bool mv)
    {
        float dip = DDip(t.B);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float q = Mathf.Tau * DFrac(t.C * 0.5f + (s < 0f ? 0f : 0.5f));
            float up = 0.5f + 0.5f * Mathf.Cos(q);
            // up, forward, down past the hip, back behind: 0.44 m from the shoulder all round
            SetArm(ref ch, s, new Vector3(s * 0.08f, 0.44f * Mathf.Cos(q), 0.44f * Mathf.Sin(q)), HLow(s).Lerp(HUp(s), up));
        }
        ch.Px = 0.04f * t.D; ch.Py = -0.04f * dip;
        ch.Theta = 0.18f; ch.Psi = 0.15f * t.D; ch.PhH = 0.15f * t.D; ch.ThN = 0.05f * dip;
        Planted(ref ch, 0.13f, 0.15f);
        HeelUp(ref ch, -1f, 0.03f * Mathf.Max(0f, t.D)); HeelUp(ref ch, 1f, 0.03f * Mathf.Max(0f, -t.D));
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    /// <summary>Hello: the figure's right hand raised beside the head, the forearm waving once a beat.</summary>
    private static void Wave(ref DanceCh ch, in Beat t, bool mv)
    {
        AimChain(ref ch, -1f, 0.25f, 0.30f, Mathf.Pi * 0.5f + 0.45f * DSin(t.C), 0f);
        Aim(ref ch, 1f, 0.04f, -0.49f, 0.02f, HLow(1f));
        ch.Phi = 0.03f; ch.PhH = -0.08f * DSin(t.C * 0.5f); ch.ThN = -0.05f;
        ch.Py = -0.01f * DDip(t.B);
        Planted(ref ch, 0.12f, 0.15f);
        if (mv) MovingScale(ref ch, 0.8f, 1f);
    }

    /// <summary>Cheer: both arms up in a V pumping on the beat, a little jump on beats 1 and 3.</summary>
    private static void Cheer(ref DanceCh ch, in Beat t, bool mv)
    {
        float air = (t.K & 1) == 0 ? Air(t.B, 0.2f, 0.85f) : 0f, h = 0.10f * air, pump = DDip(t.B);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            SetArm(ref ch, s, new Vector3(s * 0.26f, 0.34f + 0.06f * (1f - pump), 0.06f), HUp(s));
        }
        ch.Py = h - 0.05f * Give(t.B, 0.03f, 0.1f);
        ch.Theta = -0.04f; ch.ThN = -0.25f; ch.ShL = ch.ShR = 0.03f * air;
        Foot(ref ch, -1f, -0.12f, AnkleHeight + h, 0f, 0.15f, air);
        Foot(ref ch, 1f, 0.12f, AnkleHeight + h, 0f, 0.15f, air);
        if (mv) { MovingScale(ref ch, 0.5f, 1f); ch.Py = Mathf.Clamp(h, 0f, 0.04f); }
    }

    /// <summary>A salute, held: the figure's right hand at the brow, elbow out, heels together. No groove.</summary>
    private static void Salute(ref DanceCh ch, in Beat t, bool mv)
    {
        Aim(ref ch, -1f, -0.02f, 0.24f, 0.16f, new Vector3(-1f, 0.3f, 0f));
        Aim(ref ch, 1f, 0.04f, -0.49f, 0.02f, HLow(1f));
        ch.Theta = -0.03f; ch.ThN = -0.05f;
        Planted(ref ch, 0.08f, 0.35f);
        if (mv) MovingScale(ref ch, 0.8f, 1f);
    }

    /// <summary>A shrug once a bar: shoulders up and hands out over beat 1, held, let go over beats 3-4.</summary>
    private static void Shrug(ref DanceCh ch, in Beat t, bool mv)
    {
        float sh = DSm(t.C / 0.5f) * (1f - DSm((t.C - 2.2f) / 0.8f));
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            SetArm(ref ch, s, new Vector3(s * 0.20f, -0.40f + 0.10f * sh, 0.18f + 0.06f * sh), HOut(s));
        }
        ch.ShL = ch.ShR = 0.07f * sh;
        ch.Theta = -0.03f * sh; ch.ThN = -0.05f * sh; ch.PhH = 0.15f * sh;
        Planted(ref ch, 0.12f, 0.15f);
        if (mv) MovingScale(ref ch, 0.8f, 1f);
    }
}
