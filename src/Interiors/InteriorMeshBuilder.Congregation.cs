using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The pastor rat's congregation (#241): chibi people filling the front pew, facing the altar,
/// each in a cat-ear headband with a kawaii face. One style for every face: features are
/// pixels on a 17 x 17 grid over the head's front, eyes are 5 x 5 with white highlights and a
/// lash flick, every cheek is blushed. Who sits where is a hash of the pew's position, so every
/// peer builds the same people. Each is a <see cref="Figure"/> (#370): seated as before, with
/// standing legs for when the chess type beat gets them up.
/// </summary>
public static partial class InteriorMeshBuilder
{
    private const float Seat = 0.62f, Px = 0.017f;
    private const int Grid = 17;

    // pixel maps: K black, W white, P pink, R deep pink, B tear blue, '.' skin
    private static readonly string[][] Eyes =
    {
        new[] { ".KKK.", "KWWKK", "KWKKK", "KKKWK", ".KKK." },                  // sparkle
        new[] { ".KKK.", "KWKWK", "KWWWK", "KKWKK", ".KKK." },                  // heart pupil
        new[] { ".KKK.", "KWKKK", "KKKWK", "KPPPK", ".KPK." },                  // pink shine
        new[] { ".....", ".KKK.", "K...K", ".....", "....." },                  // happy ^ ^
        new[] { ".KKK.", "KWKKK", "KKKKK", "KBBBK", ".KKK.", "...B.", "...B." }, // teary
        new[] { ".....", "KKKKK", "KKWKK", ".KKK.", "....." },                  // sleepy
    };
    private static readonly bool[] EyeLash = { true, true, true, false, true, false };

    private static readonly string[][] Mouths =
    {
        new[] { "K.K.K", ".K.K." },            // :3
        new[] { ".KKK.", "KRRRK", ".KKK." },   // o
        new[] { "KKKKK", "KRRRK", ".KPK." },   // big open smile
        new[] { "K...K", ".KKK." },            // small smile
        new[] { "KKKKK", "..PP." },            // tongue out
    };

    private static readonly Color[] Skins =
        { C(0.98f, 0.84f, 0.74f), C(0.93f, 0.74f, 0.60f), C(0.78f, 0.56f, 0.40f), C(0.55f, 0.37f, 0.25f), C(1.0f, 0.90f, 0.84f) };
    private static readonly Color[] Hairs =
    {
        C(0.10f, 0.08f, 0.10f), C(0.36f, 0.22f, 0.12f), C(0.92f, 0.80f, 0.45f), C(0.95f, 0.55f, 0.72f),
        C(0.45f, 0.62f, 0.95f), C(0.92f, 0.92f, 0.95f), C(0.85f, 0.42f, 0.18f), C(0.60f, 0.45f, 0.85f),
    };
    private static readonly Color[] Clothes =
    {
        C(0.95f, 0.70f, 0.80f), C(0.55f, 0.75f, 0.95f), C(0.98f, 0.92f, 0.60f), C(0.65f, 0.88f, 0.70f),
        C(0.20f, 0.20f, 0.26f), C(0.92f, 0.92f, 0.94f), C(0.72f, 0.58f, 0.90f),
    };
    private static readonly Color[] Ears =
        { C(0.12f, 0.11f, 0.13f), C(0.96f, 0.96f, 0.96f), C(0.95f, 0.60f, 0.75f), C(0.90f, 0.55f, 0.22f) };

    private static void Congregation(FurniturePlan p, float w, float d, float y0, List<Figure> figures)
    {
        int n = Math.Max(1, (int)((p.W - 0.1f) / Seat));
        int seed = InteriorGenerator.StableHash($"{p.X:F2},{p.Z:F2}") & 0x7fffffff;
        var pew = FrameOf(p, y0);
        for (int i = 0; i < n; i++)
        {
            float cx = -w + p.W * (i + 0.5f) / n;
            int h = InteriorGenerator.StableHash($"{seed}:{i}") & 0x7fffffff;
            figures.Add(new Figure(FigureKind.Person, pew * Transform3D.Identity.Translated(new Vector3(cx, 0, 0)), h,
                Person(d, h, i + seed % 7)));
        }
    }

    private static FigurePart[] Person(float d, int h, int k)
    {
        // seated, leaning on the backrest, facing +Z (the altar)
        float bz0 = -d + 0.09f, bz1 = bz0 + 0.2f;
        float hz0 = bz0 + 0.02f;
        var fb = new FigureBuilder();
        int legs = fb.Part(-1, Vector3.Zero);
        int standing = fb.Part(-1, new Vector3(0, 0, StandForward), hidden: true);
        int torso = fb.Part(-1, new Vector3(0, 0.47f, bz0 + 0.10f));
        int armL = fb.Part(torso, new Vector3(-0.195f, 0.83f, bz0 + 0.10f));
        int armR = fb.Part(torso, new Vector3(0.195f, 0.83f, bz0 + 0.10f));
        int head = fb.Part(torso, new Vector3(0, 0.86f, hz0 + 0.12f));
        int part = torso;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col) =>
            fb.Box(part, new Vector3(xa, ya, za), new Vector3(xb, yb, zb), col);
        var skin = Skins[h % Skins.Length];
        int hairIx = (h >> 4) % Hairs.Length;
        var hair = Hairs[hairIx];
        var cloth = Clothes[(h >> 8) % Clothes.Length];
        // black ears vanish into black or brown hair: those get white, pink or orange ears
        int earIx = (h >> 12) % Ears.Length;
        if (hairIx <= 1 && earIx == 0) earIx = 1 + (h >> 20) % (Ears.Length - 1);
        var ear = Ears[earIx];
        var shoe = C(0.15f, 0.12f, 0.12f);
        var pink = C(0.98f, 0.62f, 0.72f);
        // eyes and mouths step through their lists along the row, so neighbours never match
        var eye = Eyes[k % Eyes.Length];
        bool lash = EyeLash[k % Eyes.Length];
        var mouth = Mouths[(k * 3 + (h >> 16)) % Mouths.Length];

        B(-0.16f, 0.47f, bz0, 0.16f, 0.86f, bz1, cloth);                       // body
        part = legs;
        B(-0.15f, 0.47f, bz1, -0.03f, 0.59f, bz1 + 0.26f, cloth * 0.85f);      // thighs
        B(0.03f, 0.47f, bz1, 0.15f, 0.59f, bz1 + 0.26f, cloth * 0.85f);
        B(-0.14f, 0.06f, bz1 + 0.15f, -0.04f, 0.47f, bz1 + 0.25f, skin);       // shins
        B(0.04f, 0.06f, bz1 + 0.15f, 0.14f, 0.47f, bz1 + 0.25f, skin);
        B(-0.15f, 0, bz1 + 0.13f, -0.03f, 0.06f, bz1 + 0.31f, shoe);
        B(0.03f, 0, bz1 + 0.13f, 0.15f, 0.06f, bz1 + 0.31f, shoe);
        // standing (#370): straight legs under the body, a step in front of the seat
        part = standing;
        float sz = bz0 + StandForward;
        B(-0.15f, 0.06f, sz + 0.04f, -0.03f, 0.47f, sz + 0.16f, cloth * 0.85f);
        B(0.03f, 0.06f, sz + 0.04f, 0.15f, 0.47f, sz + 0.16f, cloth * 0.85f);
        B(-0.15f, 0, sz + 0.02f, -0.03f, 0.06f, sz + 0.24f, shoe);
        B(0.03f, 0, sz + 0.02f, 0.15f, 0.06f, sz + 0.24f, shoe);
        part = armL;                                                           // arms, hands on the lap
        B(-0.23f, 0.62f, bz0 + 0.05f, -0.16f, 0.84f, bz0 + 0.15f, cloth);
        B(-0.23f, 0.60f, bz0 + 0.15f, -0.12f, 0.67f, bz1 + 0.12f, cloth);
        B(-0.12f, 0.60f, bz1 + 0.08f, 0, 0.66f, bz1 + 0.16f, skin);
        part = armR;
        B(0.16f, 0.62f, bz0 + 0.05f, 0.23f, 0.84f, bz0 + 0.15f, cloth);
        B(0.12f, 0.60f, bz0 + 0.15f, 0.23f, 0.67f, bz1 + 0.12f, cloth);
        B(0, 0.60f, bz1 + 0.08f, 0.12f, 0.66f, bz1 + 0.16f, skin);

        // the big chibi head, hair cap, fringe and side locks
        part = head;
        float hs = Grid * Px, hx0 = -hs / 2, hy0 = 0.88f, hy1 = hy0 + hs;
        float hz1 = hz0 + 0.25f;
        B(-0.05f, 0.85f, hz0 + 0.07f, 0.05f, hy0, hz0 + 0.17f, skin);
        B(hx0, hy0, hz0, -hx0, hy1, hz1, skin);
        B(hx0 - 0.015f, hy0 + 0.04f, hz0 - 0.02f, -hx0 + 0.015f, hy1 + 0.02f, hz1 - 0.06f, hair);
        B(hx0 - 0.015f, hy1 - 4 * Px, hz1 - 0.06f, -hx0 + 0.015f, hy1 + 0.02f, hz1 + 0.004f, hair);
        B(hx0 - 0.015f, hy0 + 0.02f, hz1 - 0.06f, hx0 + 2 * Px, hy1, hz1 + 0.004f, hair);
        B(-hx0 - 2 * Px, hy0 + 0.02f, hz1 - 0.06f, -hx0 + 0.015f, hy1, hz1 + 0.004f, hair);

        // cat-ear headband: the band, then each ear stepped to a point, pink inside
        B(hx0 - 0.02f, hy1 + 0.02f, hz0 + 0.09f, -hx0 + 0.02f, hy1 + 0.04f, hz0 + 0.13f, ear);
        foreach (float sx in new[] { -1f, 1f })
        {
            float ex = sx * 0.09f;
            for (int s = 0; s < 4; s++)
            {
                float half = 0.06f - s * 0.014f, y = hy1 + 0.04f + s * 0.025f;
                B(ex - half, y, hz0 + 0.08f, ex + half, y + 0.025f, hz0 + 0.14f, ear);
                if (s < 3)
                    B(ex - half + 0.018f, y, hz0 + 0.14f, ex + half - 0.018f, y + 0.025f, hz0 + 0.145f, pink);
            }
        }

        // the face, pixel runs on the head's front
        void Pix(string[] map, int col, int row)
        {
            for (int r = 0; r < map.Length; r++)
                for (int c = 0; c < map[r].Length;)
                {
                    char ch = map[r][c];
                    int run = 1;
                    while (c + run < map[r].Length && map[r][c + run] == ch) run++;
                    if (ch != '.')
                    {
                        var colr = ch switch
                        {
                            'K' => C(0.06f, 0.05f, 0.08f),
                            'W' => C(1f, 1f, 1f),
                            'P' => pink,
                            'R' => C(0.85f, 0.30f, 0.42f),
                            _ => C(0.55f, 0.80f, 1f),
                        };
                        float x0 = hx0 + (col + c) * Px, top = hy1 - (row + r) * Px;
                        B(x0, top - Px, hz1, x0 + run * Px, top, hz1 + 0.005f, colr);
                    }
                    c += run;
                }
        }
        Pix(eye, 2, 6);
        Pix(eye, 10, 6);
        if (lash)
        {
            Pix(new[] { "K" }, 1, 5);
            Pix(new[] { "K" }, 15, 5);
        }
        Pix(new[] { "PP" }, 1, 11);   // blush
        Pix(new[] { "PP" }, 14, 11);
        Pix(mouth, 6, 12);
        return fb.Done();
    }
}
