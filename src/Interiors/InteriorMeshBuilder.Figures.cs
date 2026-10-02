using Godot;

namespace UnitSport.Interiors;

/// <summary>What a <see cref="InteriorMeshBuilder.Figure"/> is: the pastor rat or one of its congregation.</summary>
public enum FigureKind { Rat, Person }

/// <summary>
/// The figures of a church (#370): the pastor rat and its congregation, no longer baked into the
/// interior's one mesh but built as parts, each its own mesh about a pivot, so
/// <see cref="ChurchDancers"/> can turn them to the chess type beat. With every part at its pivot
/// and unturned they are exactly the boxes the static mesh used to hold.
/// </summary>
public static partial class InteriorMeshBuilder
{
    /// <summary>
    /// One part: <paramref name="Pivot"/> in the figure's frame, its triangles relative to it, under
    /// the part <paramref name="Parent"/> (-1 the figure itself). <paramref name="Hidden"/> parts
    /// are shown only while dancing (a congregant's standing legs).
    /// </summary>
    public sealed record FigurePart(int Parent, Vector3 Pivot, Vector3[] Vertices, Color[] Colors, bool Hidden);

    /// <summary>
    /// A figure: <paramref name="Frame"/> places its frame (authored facing +Z, feet at y 0) in the
    /// interior; <paramref name="Hash"/> picks its dance variant, the same on every peer.
    /// </summary>
    public sealed record Figure(FigureKind Kind, Transform3D Frame, int Hash, FigurePart[] Parts);

    /// <summary>Part indices of a <see cref="FigureKind.Rat"/>.</summary>
    public static class RatParts
    {
        public const int Feet = 0, Body = 1, Tail = 2, ArmL = 3, ArmR = 4, Head = 5;
    }

    /// <summary>Part indices of a <see cref="FigureKind.Person"/>.</summary>
    public static class PersonParts
    {
        public const int LegsSeated = 0, LegsStanding = 1, Torso = 2, ArmL = 3, ArmR = 4, Head = 5;
    }

    /// <summary>How far in front of its seat a congregant stands up to dance, m.</summary>
    public const float StandForward = 0.45f;

    /// <summary>Collects a figure's boxes part by part, in the figure's frame.</summary>
    private sealed class FigureBuilder
    {
        private readonly List<(int Parent, Vector3 Pivot, bool Hidden, Scratch S)> _parts = new();

        /// <summary>Adds a part; parts are numbered in the order they are added.</summary>
        public int Part(int parent, Vector3 pivot, bool hidden = false)
        {
            _parts.Add((parent, pivot, hidden, new Scratch()));
            return _parts.Count - 1;
        }

        public void Box(int part, Vector3 min, Vector3 max, Color col)
        {
            var (_, pivot, _, s) = _parts[part];
            s.Box(min - pivot, max - pivot, col, false);
        }

        public FigurePart[] Done()
        {
            var parts = new FigurePart[_parts.Count];
            for (int i = 0; i < parts.Length; i++)
            {
                var (parent, pivot, hidden, s) = _parts[i];
                parts[i] = new FigurePart(parent, pivot, s.V.ToArray(), s.C.ToArray(), hidden);
            }
            return parts;
        }
    }

    private static Transform3D FrameOf(FurniturePlan p, float y0) =>
        new(new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2), new Vector3(p.X, y0, p.Z));

    /// <summary>
    /// The church radio (#370): the boombox of the world radio (<c>Items.RadioBody</c>'s look) on a
    /// small wooden stand, speakers to +Z (toward the pews once turned).
    /// </summary>
    private static void ChurchRadio(float w, float d, float h, Action<Vector3, Vector3, Color> box)
    {
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col) =>
            box(new Vector3(xa, ya, za), new Vector3(xb, yb, zb), col);
        var wood = C(0.34f, 0.22f, 0.14f);
        var shell = C(0.16f, 0.17f, 0.19f);
        var grille = C(0.08f, 0.08f, 0.09f);
        var chrome = C(0.72f, 0.74f, 0.76f);
        var red = C(0.80f, 0.12f, 0.10f);
        float top = h - 0.27f;
        // the stand: a top, four legs, a shelf
        B(-w, top - 0.04f, -d, w, top, d, wood);
        foreach (float x in new[] { -w + 0.03f, w - 0.07f })
            foreach (float z in new[] { -d + 0.03f, d - 0.07f })
                B(x, 0, z, x + 0.04f, top - 0.04f, z + 0.04f, wood);
        B(-w + 0.03f, 0.18f, -d + 0.03f, w - 0.03f, 0.21f, d - 0.03f, wood);
        // the boombox on it
        float bw = 0.23f, bh = 0.22f, bd = 0.08f, y = top;
        B(-bw, y, -bd, bw, y + bh, bd, shell);
        foreach (float x in new[] { -0.14f, 0.14f })
        {
            B(x - 0.065f, y + 0.035f, bd, x + 0.065f, y + 0.165f, bd + 0.008f, grille);
            B(x - 0.03f, y + 0.07f, bd + 0.008f, x + 0.03f, y + 0.13f, bd + 0.012f, chrome);
        }
        B(-0.055f, y + 0.05f, bd, 0.055f, y + 0.11f, bd + 0.008f, grille);
        for (int i = 0; i < 4; i++)
            B(-0.055f + i * 0.03f, y + 0.16f, bd, -0.035f + i * 0.03f, y + 0.175f, bd + 0.012f, i == 3 ? red : chrome);
        // the handle and the aerial
        B(-0.16f, y + bh, -0.008f, -0.144f, y + bh + 0.05f, 0.008f, chrome);
        B(0.144f, y + bh, -0.008f, 0.16f, y + bh + 0.05f, 0.008f, chrome);
        B(-0.16f, y + bh + 0.05f, -0.008f, 0.16f, y + bh + 0.066f, 0.008f, chrome);
        B(0.19f, y + bh, -0.05f, 0.198f, y + bh + 0.30f, -0.042f, chrome);
    }

    /// <summary>The Dorime rat (#241): robe, mitre, arms spread in blessing, facing +Z; a figure since #370.</summary>
    private static Figure Rat(FurniturePlan p, float y0, Color dark)
    {
        float k = p.H / 1.35f;
        var fb = new FigureBuilder();
        Vector3 V(float x, float y, float z) => new(x * k, y * k, z * k);
        int feet = fb.Part(-1, Vector3.Zero);
        int body = fb.Part(-1, V(0, 0.04f, 0));
        int tail = fb.Part(-1, V(0, 0.045f, -0.12f));
        int armL = fb.Part(body, V(-0.20f, 0.75f, 0));
        int armR = fb.Part(body, V(0.20f, 0.75f, 0));
        int head = fb.Part(body, V(0, 0.86f, 0));
        void R(int part, float xa, float ya, float za, float xb, float yb, float zb, Color col) =>
            fb.Box(part, V(xa, ya, za), V(xb, yb, zb), col);
        var robe = C(0.93f, 0.93f, 0.90f);
        var shade = C(0.80f, 0.80f, 0.78f);
        var gilt = C(0.80f, 0.64f, 0.28f);
        var fur = C(0.80f, 0.55f, 0.32f);
        var pink = C(0.88f, 0.64f, 0.62f);
        // feet and tail
        R(feet, -0.12f, 0, 0.08f, -0.04f, 0.05f, 0.20f, pink);
        R(feet, 0.04f, 0, 0.08f, 0.12f, 0.05f, 0.20f, pink);
        R(tail, -0.02f, 0.03f, -0.22f, 0.02f, 0.06f, -0.12f, pink);
        // the robe, flaring to the hem, the gold belt and the cross on the chest
        R(body, -0.24f, 0.04f, -0.16f, 0.24f, 0.36f, 0.16f, robe);
        R(body, -0.20f, 0.36f, -0.13f, 0.20f, 0.88f, 0.13f, robe);
        R(body, -0.21f, 0.60f, -0.14f, 0.21f, 0.65f, 0.14f, gilt);
        R(body, -0.015f, 0.68f, 0.13f, 0.015f, 0.84f, 0.15f, gilt);
        R(body, -0.06f, 0.77f, 0.13f, 0.06f, 0.80f, 0.15f, gilt);
        // wide sleeves held out, the cloth hanging under them, little pink hands
        foreach (float sx in new[] { -1f, 1f })
        {
            int arm = sx < 0 ? armL : armR;
            float a = sx * 0.20f, b = sx * 0.42f;
            R(arm, Math.Min(a, b), 0.66f, -0.10f, Math.Max(a, b), 0.84f, 0.10f, robe);
            float c = sx * 0.27f;
            R(arm, Math.Min(c, b), 0.30f, -0.04f, Math.Max(c, b), 0.66f, 0.04f, shade);
            float e = sx * 0.45f;
            R(arm, Math.Min(b, e), 0.72f, -0.03f, Math.Max(b, e), 0.78f, 0.03f, pink);
        }
        // the head: snout, pink nose, black eyes, round ears
        R(head, -0.15f, 0.86f, -0.11f, 0.15f, 1.10f, 0.08f, fur);
        R(head, -0.09f, 0.88f, 0.08f, 0.09f, 1.02f, 0.20f, fur);
        R(head, -0.03f, 0.95f, 0.20f, 0.03f, 0.99f, 0.23f, pink);
        R(head, -0.13f, 1.00f, 0.08f, -0.095f, 1.045f, 0.095f, dark);
        R(head, 0.095f, 1.00f, 0.08f, 0.13f, 1.045f, 0.095f, dark);
        R(head, -0.25f, 1.04f, -0.05f, -0.11f, 1.18f, -0.01f, pink);
        R(head, 0.11f, 1.04f, -0.05f, 0.25f, 1.18f, -0.01f, pink);
        // the mitre, its gold band and stripe
        R(head, -0.09f, 1.10f, -0.06f, 0.09f, 1.29f, 0.06f, robe);
        R(head, -0.05f, 1.29f, -0.04f, 0.05f, 1.35f, 0.04f, robe);
        R(head, -0.095f, 1.10f, -0.065f, 0.095f, 1.13f, 0.065f, gilt);
        R(head, -0.015f, 1.13f, 0.06f, 0.015f, 1.33f, 0.07f, gilt);
        int hash = InteriorGenerator.StableHash($"rat:{p.X:F2},{p.Z:F2}") & 0x7fffffff;
        return new Figure(FigureKind.Rat, FrameOf(p, y0), hash, fb.Done());
    }
}
