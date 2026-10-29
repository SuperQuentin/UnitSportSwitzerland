using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// One pose of the whole body while the sword is in play (#1371), as the numbers the figure's dance layer takes
/// (<c>HumanMeshBuilder.Sword.cs</c>). Only floats, so a pose is mixed field by field. Angles in radians, lengths in metres,
/// the sword arm is the figure's right and the arm targets are offsets from the shoulder in the chest's frame (outward,
/// up, forward). Positive <see cref="Psi"/> turns the right shoulder forward, positive <see cref="Theta"/> leans forward.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SwordPose
{
    /// <summary>Chest twist, lean forward, lean sideways.</summary>
    public float Psi, Theta, Phi;
    /// <summary>The hips: height (below standing is negative), sideways, forward.</summary>
    public float Py, Px, Pz;
    /// <summary>The head: nod (down is positive) and turn.</summary>
    public float Neck, HeadYaw;
    /// <summary>The sword hand's target: outward, up, forward from the right shoulder.</summary>
    public float Hx, Hy, Hz;
    /// <summary>The off hand's target (left shoulder), a counterweight.</summary>
    public float Ox, Oy, Oz;
    /// <summary>The lead (left) foot: sideways, height above the floor lift, forward.</summary>
    public float LeadX, LeadY, LeadZ;
    /// <summary>The rear (right) foot.</summary>
    public float RearX, RearY, RearZ;
    /// <summary>The whole body's extra turn about its own axis, radians (a spin slash): the owner turns the body with it.</summary>
    public float Spin;

    public static readonly int Count = Marshal.SizeOf<SwordPose>() / sizeof(float);

    public static SwordPose Lerp(in SwordPose a, in SwordPose b, float t)
    {
        SwordPose r = default;
        var ra = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in a), 1);
        var rb = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in b), 1);
        var fa = MemoryMarshal.Cast<SwordPose, float>(ra);
        var fb = MemoryMarshal.Cast<SwordPose, float>(rb);
        var fr = MemoryMarshal.Cast<SwordPose, float>(MemoryMarshal.CreateSpan(ref r, 1));
        for (int i = 0; i < fr.Length; i++) fr[i] = fa[i] + (fb[i] - fa[i]) * t;
        return r;
    }
}

/// <summary>
/// The sword's body poses as keyframe tables (#1371): each move has a coil (the wind-up's peak, as the blade goes live), a strike
/// (where the blow ends) and an over-shoot (the follow-through), between the ready stance at both ends. Three phases per move
/// - anticipation (eased in), strike (a fast ease-out, the hips leading through shoulder, elbow and wrist), follow-through and
/// settle (an ease-out, then a small overshoot back to the stance) - and two sets of swings by context (standing, moving). A
/// swing's seed jitters it a few degrees and a few percent of timing, the same on every peer that gets the same code. Data only:
/// nothing here looks at a transform, allocates or builds a mesh.
/// </summary>
public static class SwordPoses
{
    /// <summary>The sets a combo swing is drawn from: 0 standing still, 1 on the move.</summary>
    public const int Variants = 2;
    public const int Seeds = 16;

    /// <summary>The code a swing is announced with: its variant (low two bits) and its seed (the next four).</summary>
    public static int Code(int variant, int seed) => (variant & 3) | ((seed & 15) << 2);
    public static int VariantOf(int code) => code & 3;
    public static int SeedOf(int code) => (code >> 2) & 15;

    /// <summary>The set for the body's state: on the move (a stick held, or running) draws the advancing set.</summary>
    public static int VariantFor(bool moving) => moving ? 1 : 0;

    // dodge variants, in the code's variant bits
    public const int DodgeBack = 0, DodgeRoll = 1, DodgeLeft = 2, DodgeRight = 3;

    /// <summary>The dodge variant for a dash along (<paramref name="fwd"/>, <paramref name="right"/>) in the body's own frame.</summary>
    public static int DodgeVariant(float fwd, float right) =>
        fwd > 0.55f ? DodgeRoll : fwd < -0.55f ? DodgeBack : right < 0f ? DodgeLeft : DodgeRight;

    private const float H = 0.085f;   // the figure's ankle height

    /// <summary>The ready stance: sword low and forward, the off hand back, left foot leading, a little crouched.</summary>
    public static readonly SwordPose Ready = new()
    {
        Psi = -0.25f, Theta = 0.12f, Py = -0.06f, Neck = -0.04f,
        Hx = 0.12f, Hy = -0.12f, Hz = 0.28f, Ox = 0.18f, Oy = -0.10f, Oz = 0.12f,
        LeadX = -0.13f, LeadY = H, LeadZ = 0.17f, RearX = 0.14f, RearY = H, RearZ = -0.15f,
    };

    private readonly record struct Move(SwordPose Coil, SwordPose Strike, SwordPose Over);

    private static SwordPose P(float psi, float theta, float phi, float py,
        float hx, float hy, float hz, float ox, float oy, float oz,
        float leadZ, float rearZ, float pz = 0f, float spin = 0f, float neck = -0.04f, float yaw = 0f,
        float leadY = H, float rearY = H, float leadX = -0.13f, float rearX = 0.14f, float px = 0f) => new()
    {
        Psi = psi, Theta = theta, Phi = phi, Py = py, Px = px, Pz = pz, Neck = neck, HeadYaw = yaw,
        Hx = hx, Hy = hy, Hz = hz, Ox = ox, Oy = oy, Oz = oz,
        LeadX = leadX, LeadY = leadY, LeadZ = leadZ, RearX = rearX, RearY = rearY, RearZ = rearZ, Spin = spin,
    };

    private const float Turn = 6.2831855f;

    // ---- the tables: [move][variant] --------------------------------------------------------------------------------
    //          psi     theta  phi    py     hand (out,up,fwd)         off hand                 lead   rear   ...
    private static readonly Move[][] Slashes =
    {
        // 1: a diagonal slash from the right shoulder across to the left hip
        new[]
        {
            new Move(P(-0.65f, 0.05f, 0.12f, -0.10f, 0.50f, 0.40f, -0.10f, 0.45f, 0.10f, 0.05f, 0.16f, -0.20f, yaw: -0.25f),
                     P(0.75f, 0.30f, -0.10f, -0.16f, -0.30f, -0.40f, 0.40f, 0.12f, -0.25f, -0.05f, 0.42f, -0.22f, pz: 0.05f, yaw: 0.25f),
                     P(0.95f, 0.32f, -0.12f, -0.17f, -0.40f, -0.48f, 0.30f, 0.20f, -0.20f, -0.10f, 0.44f, -0.22f, pz: 0.06f, yaw: 0.3f)),
            new Move(P(-0.50f, 0.12f, 0.10f, -0.12f, 0.52f, 0.36f, -0.12f, 0.42f, 0.10f, 0.05f, 0.20f, -0.22f, yaw: -0.2f),
                     P(0.60f, 0.42f, -0.12f, -0.20f, -0.32f, -0.42f, 0.42f, 0.10f, -0.28f, -0.05f, 0.52f, -0.26f, pz: 0.08f, yaw: 0.2f),
                     P(0.80f, 0.44f, -0.14f, -0.21f, -0.42f, -0.50f, 0.32f, 0.18f, -0.22f, -0.10f, 0.54f, -0.26f, pz: 0.09f, yaw: 0.25f)),
        },
        // 2: a backhand rising slash, from the left hip up across to the right
        new[]
        {
            new Move(P(0.55f, 0.20f, -0.10f, -0.14f, -0.40f, -0.45f, 0.25f, 0.25f, 0.00f, 0.10f, 0.18f, -0.20f, yaw: 0.2f),
                     P(-0.70f, -0.05f, 0.14f, -0.08f, 0.45f, 0.50f, 0.15f, 0.50f, 0.30f, -0.05f, 0.34f, -0.20f, yaw: -0.25f),
                     P(-0.90f, -0.12f, 0.16f, -0.07f, 0.50f, 0.58f, 0.05f, 0.55f, 0.36f, -0.10f, 0.35f, -0.20f, yaw: -0.3f)),
            new Move(P(0.45f, 0.26f, -0.10f, -0.16f, -0.42f, -0.46f, 0.28f, 0.22f, 0.00f, 0.12f, 0.24f, -0.22f, yaw: 0.2f),
                     P(-0.55f, 0.05f, 0.14f, -0.12f, 0.46f, 0.52f, 0.20f, 0.48f, 0.28f, -0.05f, 0.46f, -0.24f, pz: 0.05f, yaw: -0.2f),
                     P(-0.75f, -0.02f, 0.16f, -0.11f, 0.52f, 0.60f, 0.10f, 0.54f, 0.34f, -0.10f, 0.48f, -0.24f, pz: 0.05f, yaw: -0.25f)),
        },
        // 3: a horizontal slash that turns the whole body once round
        new[]
        {
            new Move(P(-1.00f, 0.15f, 0.05f, -0.22f, 0.55f, 0.05f, -0.30f, 0.45f, 0.10f, 0.00f, 0.14f, -0.22f, yaw: -0.3f),
                     P(0.30f, 0.10f, -0.05f, -0.20f, 0.55f, 0.00f, 0.30f, 0.30f, 0.05f, 0.10f, 0.20f, -0.24f, spin: Turn, yaw: 0.2f),
                     P(0.60f, 0.10f, -0.05f, -0.18f, -0.10f, -0.10f, 0.45f, 0.20f, -0.10f, 0.10f, 0.22f, -0.22f, spin: Turn, yaw: 0.3f)),
            new Move(P(-0.90f, 0.20f, 0.05f, -0.26f, 0.55f, 0.08f, -0.28f, 0.45f, 0.10f, 0.00f, 0.18f, -0.24f, yaw: -0.3f),
                     P(0.25f, 0.14f, -0.05f, -0.24f, 0.55f, 0.02f, 0.30f, 0.30f, 0.05f, 0.10f, 0.26f, -0.26f, spin: Turn, yaw: 0.2f),
                     P(0.55f, 0.14f, -0.05f, -0.22f, -0.08f, -0.08f, 0.45f, 0.20f, -0.10f, 0.10f, 0.28f, -0.24f, spin: Turn, yaw: 0.3f)),
        },
        // 4: a thrust, the sword drawn to the hip and driven out behind a lunge step
        new[]
        {
            new Move(P(-0.50f, 0.10f, 0.00f, -0.18f, 0.25f, -0.05f, -0.15f, 0.40f, 0.15f, 0.10f, 0.10f, -0.25f),
                     P(0.15f, 0.40f, 0.00f, -0.30f, 0.05f, 0.00f, 0.55f, 0.50f, 0.20f, -0.10f, 0.62f, -0.40f, pz: 0.10f, neck: 0.05f),
                     P(0.12f, 0.36f, 0.00f, -0.30f, 0.05f, 0.02f, 0.52f, 0.52f, 0.22f, -0.12f, 0.62f, -0.40f, pz: 0.10f, neck: 0.05f)),
            new Move(P(-0.45f, 0.14f, 0.00f, -0.22f, 0.25f, -0.05f, -0.15f, 0.40f, 0.15f, 0.10f, 0.14f, -0.27f),
                     P(0.10f, 0.46f, 0.00f, -0.36f, 0.05f, 0.00f, 0.55f, 0.52f, 0.20f, -0.10f, 0.72f, -0.46f, pz: 0.12f, neck: 0.06f),
                     P(0.08f, 0.42f, 0.00f, -0.36f, 0.05f, 0.02f, 0.52f, 0.54f, 0.22f, -0.12f, 0.72f, -0.46f, pz: 0.12f, neck: 0.06f)),
        },
        // 5: a jump and an overhead slam
        new[]
        {
            new Move(P(-0.30f, -0.10f, 0.00f, 0.06f, 0.15f, 0.62f, -0.05f, 0.30f, 0.55f, 0.00f, 0.10f, -0.12f, leadY: 0.34f, rearY: 0.30f, neck: -0.1f),
                     P(0.10f, 0.55f, 0.00f, -0.34f, 0.05f, -0.45f, 0.50f, 0.20f, -0.30f, 0.30f, 0.45f, -0.30f, pz: 0.08f, neck: 0.1f),
                     P(0.10f, 0.50f, 0.00f, -0.36f, 0.05f, -0.50f, 0.46f, 0.18f, -0.32f, 0.28f, 0.46f, -0.30f, pz: 0.08f, neck: 0.1f)),
            new Move(P(-0.30f, -0.12f, 0.00f, 0.08f, 0.15f, 0.64f, -0.05f, 0.30f, 0.55f, 0.00f, 0.14f, -0.14f, leadY: 0.40f, rearY: 0.36f, neck: -0.1f),
                     P(0.10f, 0.60f, 0.00f, -0.38f, 0.05f, -0.46f, 0.52f, 0.20f, -0.30f, 0.30f, 0.60f, -0.34f, pz: 0.12f, neck: 0.12f),
                     P(0.10f, 0.55f, 0.00f, -0.40f, 0.05f, -0.52f, 0.48f, 0.18f, -0.32f, 0.28f, 0.62f, -0.34f, pz: 0.12f, neck: 0.12f)),
        },
    };

    /// <summary>The charged blow: crouched and wound up, released into a full turn.</summary>
    private static readonly Move ChargedMove =
        new(P(-1.00f, 0.35f, 0.05f, -0.35f, 0.55f, 0.05f, -0.35f, 0.40f, 0.20f, 0.20f, 0.10f, -0.26f, yaw: -0.3f),
            P(0.80f, 0.10f, -0.08f, -0.22f, 0.60f, 0.00f, 0.30f, 0.30f, 0.05f, 0.10f, 0.35f, -0.28f, spin: Turn, yaw: 0.25f),
            P(1.00f, 0.12f, -0.08f, -0.20f, -0.30f, -0.30f, 0.40f, 0.20f, -0.10f, 0.10f, 0.36f, -0.26f, spin: Turn, yaw: 0.3f));

    /// <summary>The plunge: the coil is the tucked fall, the strike a kneel on the landing.</summary>
    private static readonly Move PlungeMove =
        new(P(0.0f, 0.15f, 0.00f, 0.00f, 0.10f, 0.60f, 0.05f, 0.35f, 0.55f, 0.00f, 0.10f, -0.10f, leadY: 0.34f, rearY: 0.30f, neck: 0.1f),
            P(0.05f, 0.40f, 0.00f, -0.50f, 0.05f, -0.50f, 0.35f, 0.25f, -0.25f, 0.20f, 0.35f, -0.38f, pz: 0.05f, neck: 0.15f, rearY: 0.14f),
            P(0.05f, 0.36f, 0.00f, -0.52f, 0.05f, -0.52f, 0.32f, 0.25f, -0.25f, 0.20f, 0.35f, -0.38f, pz: 0.05f, neck: 0.15f, rearY: 0.14f));

    /// <summary>Dodges by direction: a backstep, a roll forward, a sidestep to each side.</summary>
    private static readonly Move[] Dodges =
    {
        new(P(-0.20f, 0.30f, 0.00f, -0.18f, 0.20f, -0.20f, 0.20f, 0.30f, 0.10f, 0.10f, 0.12f, -0.18f),
            P(-0.10f, -0.28f, 0.00f, -0.12f, 0.20f, -0.20f, 0.20f, 0.30f, 0.10f, 0.00f, -0.30f, -0.34f, pz: -0.10f, leadY: 0.20f, rearY: 0.16f),
            P(-0.15f, -0.10f, 0.00f, -0.10f, 0.15f, -0.18f, 0.22f, 0.25f, 0.05f, 0.05f, -0.12f, -0.26f, pz: -0.05f)),
        new(P(0.00f, 0.60f, 0.00f, -0.35f, 0.15f, -0.30f, 0.20f, 0.20f, -0.30f, 0.20f, 0.14f, -0.16f),
            P(0.00f, 1.25f, 0.00f, -0.55f, 0.10f, -0.40f, 0.20f, 0.10f, -0.40f, 0.20f, 0.20f, -0.10f, leadY: 0.28f, rearY: 0.28f, neck: 0.3f),
            P(0.00f, 0.50f, 0.00f, -0.30f, 0.12f, -0.20f, 0.25f, 0.15f, -0.20f, 0.15f, 0.20f, -0.18f)),
        new(P(0.15f, 0.20f, -0.25f, -0.16f, 0.15f, -0.20f, 0.25f, 0.25f, 0.05f, 0.10f, 0.10f, -0.16f),
            P(0.20f, 0.12f, -0.35f, -0.14f, 0.15f, -0.20f, 0.25f, 0.45f, 0.20f, 0.00f, 0.10f, -0.16f, leadX: -0.45f, leadY: 0.12f, px: -0.06f),
            P(0.15f, 0.16f, -0.18f, -0.12f, 0.15f, -0.20f, 0.25f, 0.30f, 0.10f, 0.05f, 0.14f, -0.16f, leadX: -0.22f)),
        new(P(-0.15f, 0.20f, 0.25f, -0.16f, 0.15f, -0.20f, 0.25f, 0.25f, 0.05f, 0.10f, 0.10f, -0.16f),
            P(-0.20f, 0.12f, 0.35f, -0.14f, 0.15f, -0.20f, 0.25f, 0.30f, 0.05f, 0.00f, 0.16f, -0.16f, rearX: 0.45f, rearY: 0.12f, px: 0.06f),
            P(-0.15f, 0.16f, 0.18f, -0.12f, 0.15f, -0.20f, 0.25f, 0.30f, 0.10f, 0.05f, 0.14f, -0.16f, rearX: 0.22f)),
    };

    /// <summary>Metres a swing steps forward (root motion, done by the owner and stopped at the target) by move and variant.</summary>
    public static float Lunge(SwordMove move, int variant)
    {
        bool run = variant == 1;
        return move switch
        {
            SwordMove.Slash1 => run ? 0.8f : 0.45f,
            SwordMove.Slash2 => run ? 0.6f : 0.35f,
            SwordMove.Slash3 => run ? 0.9f : 0.5f,
            SwordMove.Slash4 => run ? 1.2f : 0.9f,
            SwordMove.Slash5 => run ? 1.1f : 0.8f,
            SwordMove.Charged => 1.0f,
            _ => 0f,
        };
    }

    /// <summary>
    /// The fraction (0..1) of the lunge done by <paramref name="progress"/> of the move: eased in and out (a smoothstep) over the
    /// anticipation's second half, the strike and the start of the follow-through, so the body travels as it winds up and arrives with the blow.
    /// </summary>
    public static float LungeDone(SwordMove move, float progress)
    {
        var (_, tA, tS, _) = Times(move, 0);
        float from = tA * 0.4f, to = tS + (1f - tS) * 0.3f;
        float x = Math.Clamp((progress - from) / Math.Max(0.001f, to - from), 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>The span of the move (progress) a trail covers: from just before the blade goes live to just after the strike ends.</summary>
    public static (float From, float To) TrailWindow(SwordMove move, int seed)
    {
        var (_, tA, tS, _) = Times(move, seed);
        return (Math.Max(0f, tA * 0.92f), Math.Min(0.98f, tS + 0.03f));
    }

    /// <summary>The moves' phase marks as fractions of the move: (ready 0, end of the anticipation, end of the strike, the follow-through's peak).</summary>
    public static (float Zero, float Anticipation, float Strike, float Follow) Times(SwordMove move, int seed)
    {
        float tA, tS;
        if (move == SwordMove.Dodge) { tA = 0.12f; tS = 0.50f; }
        else
        {
            var d = SwordRules.Def(move);
            float total = Math.Max(0.0001f, d.Total);
            tA = d.Startup / total;
            tS = (d.Startup + d.Active) / total;
        }
        // a few per cent of timing, by the seed: the wind-up a little longer or shorter, never past the strike
        float j = Jitter(seed, 2);
        tA = Math.Clamp(tA * (1f + 0.04f * j), 0f, Math.Max(0f, tS - 0.02f));
        float tF = tS + (1f - tS) * 0.45f;
        return (0f, tA, tS, tF);
    }

    /// <summary>A deterministic number in -1..1 from the seed and a channel: the same on every peer.</summary>
    public static float Jitter(int seed, int channel)
    {
        uint h = (uint)(seed * 40503 + channel * 9973 + 17) * 2654435761u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        return (h & 0xFFFF) / 32767.5f - 1f;
    }

    private static Move Table(SwordMove move, int variant) => move switch
    {
        >= SwordMove.Slash1 and <= SwordMove.Slash5 => Slashes[(int)move - 1][Math.Clamp(variant, 0, Variants - 1)],
        SwordMove.Charged => ChargedMove,
        SwordMove.Plunge => PlungeMove,
        SwordMove.Dodge => Dodges[Math.Clamp(variant, 0, 3)],
        _ => new Move(Ready, Ready, Ready),
    };

    private const int KeyCount = 6;

    // How much later than the hips each part of the body follows (seconds): overlapping action. The hips lead, the chest a moment
    // after, then the shoulder and arm, the head and the off arm last (they also settle with the spline's overshoot).
    public const float ChestLag = 0.04f, ArmLag = 0.06f, HeadLag = 0.10f;
    /// <summary>The blade trails the hand by this much more: the whip of the wrist.</summary>
    public const float BladeLag = 0.05f;

    /// <summary>
    /// The pose <paramref name="progress"/> (0..1) of the way through <paramref name="move"/>, in <paramref name="variant"/>, jittered by
    /// <paramref name="seed"/>. The keys (the stance, the coil as the blade goes live, the end of the blow, the follow-through's peak, a
    /// small undershoot past the stance, the stance) are joined by a cubic Hermite spline with Catmull-Rom tangents: the velocity is
    /// continuous through every key, and only the true ends (rest at both) stop. Each part of the body reads the spline a little later than
    /// the hips (<see cref="ChestLag"/>, <see cref="ArmLag"/>, <see cref="HeadLag"/>), and the weight shifts over the front foot on the strike
    /// and comes back in the recovery. A plunge starts in its tucked fall.
    /// </summary>
    public static SwordPose Sample(SwordMove move, int variant, int seed, float progress)
    {
        if (move is SwordMove.None or SwordMove.Charge) return Ready;
        var t = Table(move, variant);
        var (_, tA, tS, tF) = Times(move, seed);
        // the seed's few degrees: the strike reaches a little further or less far, a little higher or lower
        float a = Jitter(seed, 0), b = Jitter(seed, 1);
        var coil = t.Coil;
        var strike = t.Strike;
        var over = t.Over;
        strike.Psi += 0.07f * a; over.Psi += 0.07f * a;
        strike.Hx += 0.05f * b; strike.Hy += 0.05f * a; over.Hx += 0.05f * b; over.Hy += 0.05f * a;
        strike.Theta += 0.04f * b; over.Theta += 0.04f * b;
        coil.Psi -= 0.05f * b;
        var start = move == SwordMove.Plunge ? coil : Ready;
        // a small undershoot past the stance, on the far side of the follow-through, then home
        var under = SwordPose.Lerp(over, start, 1.08f);
        float tU = tF + (1f - tF) * 0.55f;

        Span<SwordPose> k = stackalloc SwordPose[KeyCount];
        Span<float> kt = stackalloc float[KeyCount];
        // a spin slash's turn stays turned through the settle (the facing at the end is the same one): never unwound backwards
        under.Spin = over.Spin;
        var end = start;
        end.Spin = over.Spin;
        k[0] = start; k[1] = coil; k[2] = strike; k[3] = over; k[4] = under; k[5] = end;
        kt[0] = 0f; kt[1] = tA; kt[2] = tS; kt[3] = tF; kt[4] = tU; kt[5] = 1f;
        // keep the times strictly increasing (a plunge's coil is at 0)
        for (int i = 1; i < KeyCount; i++) if (kt[i] <= kt[i - 1]) kt[i] = kt[i - 1] + 0.001f;

        float p = Math.Clamp(progress, 0f, 1f);
        float total = Math.Max(0.0001f, move == SwordMove.Dodge ? 0.45f : SwordRules.Def(move).Total);
        // the lags fade out over the last stretch so every part is home together at the end
        float fade = Math.Clamp((1f - p) / 0.15f, 0f, 1f);
        float Lag(float seconds) => Math.Max(0f, p - seconds / total * fade);

        var hips = Spline(k, kt, p);
        var chest = Spline(k, kt, Lag(ChestLag));
        var arm = Spline(k, kt, Lag(ArmLag));
        var head = Spline(k, kt, Lag(HeadLag));
        var r = hips;
        r.Spin = Math.Clamp(r.Spin, Math.Min(0f, over.Spin), Math.Max(0f, over.Spin));
        r.Psi = chest.Psi; r.Theta = chest.Theta; r.Phi = chest.Phi;
        r.Hx = arm.Hx; r.Hy = arm.Hy; r.Hz = arm.Hz;
        r.Neck = head.Neck; r.HeadYaw = head.HeadYaw; r.Ox = head.Ox; r.Oy = head.Oy; r.Oz = head.Oz;

        if (move is >= SwordMove.Slash1 and <= SwordMove.Slash5 or SwordMove.Charged)
        {
            // the weight over the front foot on the strike, back in the recovery: the pelvis drops and moves to the front (left) foot, the back heel
            // lifts, and the front foot, which steps, is off the floor while it travels and planted when it lands
            float w = Bump(p, tA * 0.6f, tU);
            r.Px -= 0.06f * w; r.Py -= 0.05f * w; r.RearY += 0.05f * w;
            float step = Math.Abs(strike.LeadZ - coil.LeadZ);
            r.LeadY += Math.Clamp(step * 0.4f, 0f, 0.14f) * Bump(p, tA * 0.8f, tS + (tF - tS) * 0.5f);
        }
        return r;
    }

    private static float Bump(float p, float from, float to)
    {
        float x = Math.Clamp((p - from) / Math.Max(0.001f, to - from), 0f, 1f);
        return MathF.Sin(MathF.PI * x);
    }

    /// <summary>Cubic Hermite through the keys with Catmull-Rom tangents (non-uniform), rest (zero velocity) at the two ends.</summary>
    private static SwordPose Spline(ReadOnlySpan<SwordPose> k, ReadOnlySpan<float> t, float p)
    {
        int n = k.Length, i = 0;
        while (i < n - 2 && p >= t[i + 1]) i++;
        float h = t[i + 1] - t[i];
        float s = Math.Clamp((p - t[i]) / h, 0f, 1f);
        float s2 = s * s, s3 = s2 * s;
        float h00 = 2f * s3 - 3f * s2 + 1f, h10 = s3 - 2f * s2 + s, h01 = -2f * s3 + 3f * s2, h11 = s3 - s2;
        SwordPose r = default;
        var fr = MemoryMarshal.Cast<SwordPose, float>(MemoryMarshal.CreateSpan(ref r, 1));
        var f0 = MemoryMarshal.Cast<SwordPose, float>(k.Slice(i, 1));
        var f1 = MemoryMarshal.Cast<SwordPose, float>(k.Slice(i + 1, 1));
        var fp = i > 0 ? MemoryMarshal.Cast<SwordPose, float>(k.Slice(i - 1, 1)) : f0;
        var fn = i + 2 < n ? MemoryMarshal.Cast<SwordPose, float>(k.Slice(i + 2, 1)) : f1;
        float dp = i > 0 ? t[i + 1] - t[i - 1] : 0f, dn = i + 2 < n ? t[i + 2] - t[i] : 0f;
        for (int c = 0; c < fr.Length; c++)
        {
            float m0 = i > 0 ? (f1[c] - fp[c]) / dp : 0f;
            float m1 = i + 2 < n ? (fn[c] - f0[c]) / dn : 0f;
            fr[c] = h00 * f0[c] + h10 * h * m0 + h01 * f1[c] + h11 * h * m1;
        }
        return r;
    }

    /// <summary>The swing a new one chains from: its move, variant, seed and how far through it was when the new one began.</summary>
    public readonly record struct Chain(SwordMove Move, int Variant, int Seed, float Progress)
    {
        public static readonly Chain None = new(SwordMove.None, 0, 0, 0f);
    }

    /// <summary>Packs a <see cref="Chain"/> into 16 bits (move 4, variant 2, seed 4, progress 6).</summary>
    public static int PackChain(in Chain c) =>
        c.Move == SwordMove.None ? 0 : (int)c.Move | ((c.Variant & 3) << 4) | ((c.Seed & 15) << 6) | ((int)Math.Round(Math.Clamp(c.Progress, 0f, 1f) * 63f) << 10);

    public static Chain UnpackChain(int bits) =>
        (bits & 15) == 0 ? Chain.None : new Chain((SwordMove)(bits & 15), (bits >> 4) & 3, (bits >> 6) & 15, ((bits >> 10) & 63) / 63f);

    /// <summary>
    /// The pose of <paramref name="move"/> at <paramref name="progress"/> when it began while <paramref name="prev"/> was still settling: the
    /// previous swing goes on along its own spline while the new one's takes over, a smoothstep over the wind-up. The body never returns
    /// to the stance between the swings of a combo; only the last swing, or a pause, settles.
    /// </summary>
    public static SwordPose SampleChain(SwordMove move, int variant, int seed, float progress, in Chain prev)
    {
        var cur = move == SwordMove.Charge ? Charging(progress * (float)SwordRules.ChargeMax, 0f) : Sample(move, variant, seed, progress);
        if (prev.Move == SwordMove.None) return cur;
        float curTotal = move == SwordMove.Charge ? (float)SwordRules.ChargeMax : move == SwordMove.Dodge ? 0.45f : SwordRules.Def(move).Total;
        float seconds = progress * curTotal;
        float blend = Math.Max(0.14f, move == SwordMove.Charge ? 0.3f : move == SwordMove.Dodge ? 0.12f : SwordRules.Def(move).Startup * 0.95f);
        float x = seconds / blend;
        if (x >= 1f) return cur;
        SwordPose old;
        if (prev.Move == SwordMove.Charge) old = Charging(prev.Progress * (float)SwordRules.ChargeMax, 0f);
        else
        {
            float prevTotal = prev.Move == SwordMove.Dodge ? 0.45f : SwordRules.Def(prev.Move).Total;
            old = Sample(prev.Move, prev.Variant, prev.Seed, Math.Min(1f, prev.Progress + seconds / Math.Max(0.01f, prevTotal)));
        }
        float w = x * x * (3f - 2f * x);
        var r = SwordPose.Lerp(old, cur, w);
        r.Spin = cur.Spin;
        return r;
    }

    /// <summary>The held charge: the stance drawn in to the crouched wind-up over its first third of a second, then held with a tremble.</summary>
    public static SwordPose Charging(float seconds, float shake)
    {
        var pose = SwordPose.Lerp(Ready, ChargedMove.Coil, Math.Clamp(seconds / 0.3f, 0f, 1f) is var u ? u * u * (3f - 2f * u) : 0f);
        pose.Psi += shake;
        pose.Py += shake * 0.1f;
        return pose;
    }
}
