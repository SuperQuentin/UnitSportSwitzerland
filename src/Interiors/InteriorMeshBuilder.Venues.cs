using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The pieces of entertainment venues (#1232): a bowling centre's lanes, settees, counters, bar, pool
/// tables and games (#1235). Authored like every piece, back to −Z and centred on the origin, then turned
/// and moved. Parts that light themselves (score screens, lane numbers, a pinball's playfield, a cabinet's
/// screen and marquee, the bar's neon) carry <see cref="Lit"/>'s vertex alpha, which the interior shader
/// draws unlit in every style.
/// </summary>
public static partial class InteriorMeshBuilder
{
    /// <summary>A colour that lights itself: vertex alpha in the band the interior shader draws unlit (0.2-0.5).</summary>
    private static Color Lit(float r, float g, float b) => C(r, g, b) with { A = 0.35f };

    private static readonly Color LaneMaple = C(0.86f, 0.70f, 0.48f), LaneTan = C(0.80f, 0.62f, 0.40f),
        Gutter = C(0.10f, 0.10f, 0.12f), Capping = C(0.30f, 0.22f, 0.40f), PinWhite = C(0.96f, 0.96f, 0.94f),
        PinRed = C(0.82f, 0.10f, 0.12f), Pit = C(0.04f, 0.04f, 0.05f), MachineGrey = C(0.62f, 0.64f, 0.62f),
        Chrome = C(0.78f, 0.80f, 0.84f);

    /// <summary>House balls' colours: black, blue, red, green, purple, orange, pink, yellow [S].</summary>
    private static readonly Color[] BallColors =
    {
        C(0.08f, 0.08f, 0.10f), C(0.12f, 0.30f, 0.80f), C(0.80f, 0.10f, 0.12f), C(0.10f, 0.60f, 0.24f),
        C(0.46f, 0.16f, 0.66f), C(0.94f, 0.46f, 0.08f), C(0.94f, 0.36f, 0.62f), C(0.96f, 0.80f, 0.12f),
    };

    /// <summary>Settee vinyl: teal, red, orange, black, purple.</summary>
    private static readonly Color[] Vinyl =
    {
        C(0.06f, 0.52f, 0.54f), C(0.70f, 0.10f, 0.14f), C(0.92f, 0.44f, 0.08f), C(0.10f, 0.10f, 0.12f), C(0.42f, 0.16f, 0.58f),
    };

    /// <summary>A masking unit's theme: a ground and two accents, the bright ones fluorescent under the blacklight.</summary>
    private static readonly (Color Ground, Color A, Color B)[] Masking =
    {
        (C(0.06f, 0.06f, 0.20f), Lit(0.96f, 0.28f, 0.70f), Lit(0.20f, 0.90f, 0.98f)),   // night sky, pink and cyan
        (C(0.10f, 0.10f, 0.10f), Lit(0.98f, 0.46f, 0.06f), Lit(0.98f, 0.86f, 0.10f)),   // flames
        (C(0.04f, 0.20f, 0.30f), Lit(0.40f, 0.96f, 0.30f), Lit(0.98f, 0.96f, 0.30f)),   // lagoon
        (C(0.24f, 0.04f, 0.30f), Lit(0.98f, 0.70f, 0.10f), Lit(0.40f, 0.70f, 1.00f)),   // planets
    };

    /// <summary>Whether a piece is drawn here rather than in the main switch.</summary>
    private static bool IsVenuePiece(FurnitureType t) => t is FurnitureType.LanePair or FurnitureType.Settee
        or FurnitureType.ShoeCounter or FurnitureType.BallRack or FurnitureType.BarCounter or FurnitureType.BackBar
        or FurnitureType.BarStool or FurnitureType.PoolTable or FurnitureType.Pinball or FurnitureType.ArcadeCabinet
        or FurnitureType.DartBoard or FurnitureType.ShoeRack;

    private static void VenuePiece(Scratch s, FurniturePlan p, Vector3 at, Basis basis)
    {
        float w = p.W / 2, d = p.D / 2, H = p.H;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col, bool collide = false) =>
            s.Box(at + new Vector3(xa, ya, za), at + new Vector3(xb, yb, zb), col, collide, basis, at);
        var wood = C(0.52f, 0.36f, 0.22f);
        var darkWood = C(0.30f, 0.20f, 0.12f);
        var dark = C(0.10f, 0.10f, 0.11f);
        switch (p.Type)
        {
            case FurnitureType.LanePair: LanePair(s, p, at, basis, B); break;
            case FurnitureType.Settee:
            {
                // curved benches round a table, read as a U of seats: the back row and its two wings
                var vinyl = Vinyl[(int)(Hash(p, 3) * Vinyl.Length) % Vinyl.Length];
                var trim = vinyl * 0.7f;
                trim.A = 1;
                void Bench(float x0, float z0, float x1, float z1, bool backAtMinusZ, bool backAtX0)
                {
                    B(x0, 0, z0, x1, 0.42f, z1, trim);
                    B(x0 + 0.02f, 0.42f, z0 + 0.02f, x1 - 0.02f, 0.48f, z1 - 0.02f, vinyl);
                    if (backAtMinusZ) B(x0, 0.42f, z0, x1, 0.9f, z0 + 0.14f, vinyl);
                    else if (backAtX0) B(x0, 0.42f, z0, x0 + 0.14f, 0.9f, z1, vinyl);
                    else B(x1 - 0.14f, 0.42f, z0, x1, 0.9f, z1, vinyl);
                }
                Bench(-w, -d, w, -d + 0.6f, true, false);
                float wing = Math.Min(1.2f, p.D - 0.9f);
                Bench(-w, -d + 0.6f, -w + 0.58f, -d + 0.6f + wing, false, true);
                Bench(w - 0.58f, -d + 0.6f, w, -d + 0.6f + wing, false, false);
                // the scoring table and its console
                float tz = -d + 0.6f + wing * 0.55f;
                B(-0.06f, 0, tz - 0.06f, 0.06f, 0.7f, tz + 0.06f, dark);
                B(-0.45f, 0.7f, tz - 0.35f, 0.45f, 0.75f, tz + 0.35f, C(0.20f, 0.20f, 0.24f));
                B(-0.18f, 0.75f, tz - 0.1f, 0.18f, 0.85f, tz + 0.04f, dark);
                B(-0.16f, 0.77f, tz - 0.08f, 0.16f, 0.851f, tz + 0.02f, Lit(0.20f, 0.42f, 0.90f));
                break;
            }
            case FurnitureType.ShoeCounter:
            {
                // the shoe wall behind, a gap for whoever serves, and the counter at the front
                B(-w, 0, -d, w, H, -d + 0.45f, darkWood);
                int cols = Math.Max(2, (int)(p.W / 0.32f)), rows = 6;
                for (int c = 0; c < cols; c++)
                    for (int r = 0; r < rows; r++)
                    {
                        float x = -w + (c + 0.5f) * p.W / cols, y = 0.25f + r * (H - 0.35f) / rows;
                        // two-tone hire shoes, sizes running along the wall
                        var shoe = (c + r) % 3 == 0 ? C(0.70f, 0.12f, 0.12f) : (c + r) % 3 == 1 ? C(0.16f, 0.24f, 0.60f) : C(0.42f, 0.28f, 0.16f);
                        B(x - 0.12f, y, -d + 0.45f, x + 0.12f, y + 0.12f, -d + 0.47f, shoe);
                    }
                B(-w, 0, d - 0.8f, w, 1.0f, d, C(0.16f, 0.16f, 0.30f), true);
                B(-w - 0.03f, 1.0f, d - 0.84f, w + 0.03f, 1.06f, d + 0.04f, C(0.86f, 0.84f, 0.80f));
                B(-w, 0.86f, d, w, 0.92f, d + 0.01f, Lit(0.98f, 0.20f, 0.30f));
                break;
            }
            case FurnitureType.ShoeRack:
                B(-w, 0, -d, w, H, d, darkWood);
                break;
            case FurnitureType.BallRack:
            {
                // two tiers of house balls, lightest to heaviest along the rack
                foreach (float y in new[] { 0.4f, 0.85f })
                {
                    B(-w, y - 0.05f, -d, w, y, -d + 0.08f, Chrome);
                    B(-w, y - 0.05f, d - 0.08f, w, y, d, Chrome);
                }
                foreach (float x in new[] { -w, w - 0.05f })
                    B(x, 0, -d, x + 0.05f, 0.9f, d, Chrome);
                int n = Math.Max(3, (int)(p.W / 0.26f));
                for (int tier = 0; tier < 2; tier++)
                    for (int i = 0; i < n; i++)
                    {
                        float x = -w + 0.15f + i * (p.W - 0.3f) / Math.Max(1, n - 1), y = tier == 0 ? 0.4f : 0.85f;
                        var col = BallColors[(int)(Hash(p, i * 2 + tier) * BallColors.Length) % BallColors.Length];
                        B(x - 0.1f, y, -0.1f, x + 0.1f, y + 0.2f, 0.1f, col);
                    }
                break;
            }
            case FurnitureType.BarCounter:
            {
                // the back bar: a cabinet, shelves of bottles against a lit mirror
                B(-w, 0, -d, w, 0.9f, -d + 0.5f, darkWood);
                B(-w, 0.9f, -d, w, H, -d + 0.04f, Lit(0.30f, 0.20f, 0.14f));
                foreach (float y in new[] { 1.25f, 1.65f })
                {
                    B(-w, y - 0.03f, -d, w, y, -d + 0.25f, C(0.80f, 0.80f, 0.78f));
                    int bottles = Math.Max(2, (int)(p.W / 0.16f));
                    for (int i = 0; i < bottles; i++)
                    {
                        float x = -w + 0.1f + i * (p.W - 0.2f) / bottles;
                        float hb = 0.18f + 0.12f * Hash(p, i + (int)(y * 10));
                        var glass = Hash(p, i * 7 + 1) < 0.5f ? C(0.20f, 0.42f, 0.18f) : C(0.56f, 0.30f, 0.10f);
                        B(x - 0.03f, y, -d + 0.08f, x + 0.03f, y + hb, -d + 0.15f, glass);
                    }
                }
                // the counter, its top, a footrail, the taps, a strip of neon under the lip
                B(-w, 0, d - 0.75f, w, 1.06f, d, wood, true);
                B(-w - 0.04f, 1.06f, d - 0.8f, w + 0.04f, 1.12f, d + 0.06f, darkWood);
                B(-w, 0.18f, d + 0.12f, w, 0.22f, d + 0.16f, Chrome);
                for (int i = 0; i < 3; i++)
                {
                    float x = -0.4f + i * 0.4f;
                    B(x - 0.03f, 1.12f, d - 0.5f, x + 0.03f, 1.45f, d - 0.44f, Chrome);
                }
                B(-w, 1.0f, d + 0.06f, w, 1.04f, d + 0.07f, Lit(0.20f, 0.70f, 1.00f));
                break;
            }
            case FurnitureType.BackBar:
                B(-w, 0, -d, w, H, d, darkWood);
                break;
            case FurnitureType.BarStool:
                B(-0.03f, 0, -0.03f, 0.03f, 0.72f, 0.03f, Chrome);
                B(-0.16f, 0, -0.16f, 0.16f, 0.03f, 0.16f, Chrome);
                B(-0.18f, 0.72f, -0.18f, 0.18f, 0.78f, 0.18f, Vinyl[(int)(Hash(p, 2) * Vinyl.Length) % Vinyl.Length]);
                break;
            case FurnitureType.PoolTable:
            {
                var cloth = Hash(p, 5) < 0.6f ? C(0.10f, 0.46f, 0.20f) : C(0.12f, 0.30f, 0.62f);
                foreach (var (lx, lz) in new[] { (-w + 0.12f, -d + 0.12f), (w - 0.12f, -d + 0.12f), (-w + 0.12f, d - 0.12f), (w - 0.12f, d - 0.12f) })
                    B(lx - 0.08f, 0, lz - 0.08f, lx + 0.08f, 0.6f, lz + 0.08f, darkWood);
                B(-w, 0.6f, -d, w, 0.76f, d, darkWood);
                B(-w + 0.12f, 0.76f, -d + 0.12f, w - 0.12f, 0.78f, d - 0.12f, cloth);
                B(-w, 0.76f, -d, w, 0.82f, -d + 0.12f, darkWood);
                B(-w, 0.76f, d - 0.12f, w, 0.82f, d, darkWood);
                B(-w, 0.76f, -d, -w + 0.12f, 0.82f, d, darkWood);
                B(w - 0.12f, 0.76f, -d, w, 0.82f, d, darkWood);
                foreach (float px in new[] { -w + 0.1f, w - 0.1f })
                    foreach (float pz in new[] { -d + 0.1f, 0f, d - 0.1f })
                        B(px - 0.06f, 0.821f, pz - 0.06f, px + 0.06f, 0.83f, pz + 0.06f, dark);
                for (int i = 0; i < 6; i++)
                {
                    float bx = (Hash(p, 20 + i) - 0.5f) * (p.W - 0.5f), bz = (Hash(p, 40 + i) - 0.5f) * (p.D - 0.6f);
                    B(bx - 0.03f, 0.78f, bz - 0.03f, bx + 0.03f, 0.84f, bz + 0.03f, BallColors[i % BallColors.Length]);
                }
                // the lamp over it, lit underneath, on its chains
                B(-0.32f, 1.72f, -0.85f, 0.32f, 1.84f, 0.85f, C(0.08f, 0.26f, 0.14f));
                B(-0.27f, 1.715f, -0.8f, 0.27f, 1.72f, 0.8f, Lit(1.00f, 0.92f, 0.70f));
                foreach (float cz in new[] { -0.6f, 0.6f })
                    B(-0.01f, 1.84f, cz - 0.01f, 0.01f, 2.4f, cz + 0.01f, dark);
                break;
            }
            case FurnitureType.Pinball:
            {
                // legs, the body sloping up to the backbox, a lit playfield under its glass, the backglass
                var body = Vinyl[(int)(Hash(p, 4) * Vinyl.Length) % Vinyl.Length];
                foreach (var (lx, lz) in new[] { (-w + 0.05f, d - 0.08f), (w - 0.05f, d - 0.08f), (-w + 0.05f, -d + 0.35f), (w - 0.05f, -d + 0.35f) })
                    B(lx - 0.03f, 0, lz - 0.03f, lx + 0.03f, 0.76f, lz + 0.03f, Chrome);
                B(-w, 0.76f, -d + 0.25f, w, 0.96f, d, body);
                B(-w + 0.04f, 0.96f, -d + 0.3f, w - 0.04f, 0.97f, d - 0.06f, Lit(0.10f + 0.5f * Hash(p, 6), 0.20f, 0.70f));
                for (int i = 0; i < 5; i++)
                {
                    float bx = (Hash(p, 60 + i) - 0.5f) * (p.W - 0.25f), bz = -d + 0.5f + Hash(p, 70 + i) * (p.D - 0.8f);
                    B(bx - 0.05f, 0.97f, bz - 0.05f, bx + 0.05f, 1.0f, bz + 0.05f, Lit(0.98f, 0.30f + 0.6f * Hash(p, 80 + i), 0.20f));
                }
                B(-w, 0.96f, -d, w, H, -d + 0.25f, (body * 0.8f) with { A = 1 });
                B(-w + 0.05f, 1.22f, -d + 0.25f, w - 0.05f, H - 0.08f, -d + 0.26f, Lit(0.98f, 0.60f, 0.16f));
                B(-w - 0.01f, 0.86f, d - 0.25f, -w, 0.9f, d - 0.2f, Lit(0.98f, 0.20f, 0.20f));
                B(w, 0.86f, d - 0.25f, w + 0.01f, 0.9f, d - 0.2f, Lit(0.98f, 0.20f, 0.20f));
                break;
            }
            case FurnitureType.ArcadeCabinet:
            {
                var body = Vinyl[(int)(Hash(p, 4) * Vinyl.Length) % Vinyl.Length];
                B(-w, 0, -d, w, H, d - 0.3f, body);
                B(-w, 0.85f, d - 0.3f, w, 0.98f, d, (body * 0.8f) with { A = 1 });
                B(-w + 0.08f, 1.12f, d - 0.33f, w - 0.08f, 1.58f, d - 0.3f, Lit(0.10f + 0.4f * Hash(p, 8), 0.30f + 0.5f * Hash(p, 9), 0.80f));
                B(-w, 1.64f, d - 0.33f, w, H, d - 0.3f, Lit(0.98f, 0.80f * Hash(p, 10), 0.30f));
                B(-0.2f, 0.98f, d - 0.18f, -0.17f, 1.08f, d - 0.15f, dark);
                B(-0.23f, 1.08f, d - 0.21f, -0.14f, 1.12f, d - 0.12f, C(0.85f, 0.10f, 0.10f));
                for (int i = 0; i < 3; i++)
                    B(0.0f + i * 0.08f, 0.98f, d - 0.17f, 0.05f + i * 0.08f, 1.0f, d - 0.12f, Lit(0.2f + 0.3f * i, 0.8f, 0.3f));
                break;
            }
            case FurnitureType.DartBoard:
            {
                // a wall cabinet, open, the board in it at 1.73 m [S]
                B(-w, 1.3f, -d, w, 2.15f, -d + 0.05f, darkWood);
                B(-0.23f, 1.5f, -d + 0.05f, 0.23f, 1.96f, -d + 0.09f, dark);
                B(-0.19f, 1.54f, -d + 0.09f, 0.19f, 1.92f, -d + 0.095f, C(0.85f, 0.80f, 0.62f));
                B(-0.13f, 1.6f, -d + 0.095f, 0.13f, 1.86f, -d + 0.1f, C(0.12f, 0.50f, 0.20f));
                B(-0.07f, 1.66f, -d + 0.1f, 0.07f, 1.8f, -d + 0.105f, C(0.80f, 0.10f, 0.12f));
                B(-0.02f, 1.71f, -d + 0.105f, 0.02f, 1.75f, -d + 0.11f, C(0.80f, 0.10f, 0.12f));
                break;
            }
        }
    }

    /// <summary>
    /// A pair of lanes, approach at −Z and pins at +Z: two lanes and their gutters with capping between,
    /// the foul line, arrows and dots, ten pins on each deck, the pits, the masking unit with its lane
    /// numbers and theme, the pinsetters and their aisle behind it, the ball return's hood with balls on
    /// it, and the two score screens hung over the approach facing the settees. Mini lanes when it is
    /// short: the same parts, scaled down. Walked on; only the hood and the machine end collide.
    /// </summary>
    private static void LanePair(Scratch s, FurniturePlan p, Vector3 at, Basis basis,
        Action<float, float, float, float, float, float, Color, bool> B)
    {
        float w = p.W / 2, d = p.D / 2, H = p.H;
        bool mini = p.D < 20f;
        var spec = mini ? InteriorGenerator.MiniLanes : InteriorGenerator.FullLanes;
        float scale = mini ? spec.Lane / InteriorGenerator.FullLanes.Lane : 1f;
        float z0 = -d, foul = z0 + spec.Approach, laneEnd = foul + spec.Lane;
        float[] lanes = { -w / 2 + 0.0375f, w / 2 - 0.0375f };   // ±0.8875 at the regulation pitch
        const float half = 0.53f, gutter = 0.2325f;

        // the approach, the foul line, the dots
        B(-w, 0, z0, w, 0.02f, foul, LaneMaple, false);
        B(-w, 0.02f, foul - 0.03f, w, 0.022f, foul + 0.01f, Gutter, false);
        foreach (float c in lanes)
        {
            foreach (float dot in new[] { -0.3f, 0f, 0.3f })
                B(c + dot - 0.02f, 0.02f, foul - 1.0f, c + dot + 0.02f, 0.022f, foul - 0.96f, Gutter, false);
            // the lane, the gutters, the arrows, the pin deck and the pit
            B(c - half, 0, foul, c + half, 0.025f, laneEnd - 0.9f * scale, LaneTan, false);
            B(c - half, 0, laneEnd - 0.9f * scale, c + half, 0.025f, laneEnd, LaneMaple, false);
            B(c - half - gutter, 0, foul, c - half, 0.008f, laneEnd, Gutter, false);
            B(c + half, 0, foul, c + half + gutter, 0.008f, laneEnd, Gutter, false);
            float arrows = foul + 4.3f * scale;
            for (int a = 0; a < 7; a++)
            {
                float ax = c - half + (a + 1) * 2 * half / 8;
                float az = arrows + Math.Abs(a - 3) * 0.3f * scale;
                B(ax - 0.015f, 0.025f, az, ax + 0.015f, 0.027f, az + 0.15f * scale, Lit(0.30f, 0.60f, 1.00f), false);
            }
            Pins(c, laneEnd - 0.868f * scale, scale, B);
            B(c - half - gutter, 0, laneEnd, c + half + gutter, 0.004f, laneEnd + 0.6f * scale, Pit, false);
        }
        // the capping boards: between the two lanes (the ball return runs under it), and half of each outer one
        B(-0.125f, 0, foul, 0.125f, 0.12f, laneEnd, Capping, false);
        B(-w, 0, foul, -w + 0.1f, 0.1f, laneEnd, Capping, false);
        B(w - 0.1f, 0, foul, w, 0.1f, laneEnd, Capping, false);

        // the ball return's hood at the head of the approach, three balls waiting on it
        float hz = z0 + 0.15f;
        var hood = Masking[(int)(Hash(p, 11) * Masking.Length) % Masking.Length].A;
        B(-0.38f, 0, hz, 0.38f, 0.62f, hz + 0.72f, C(0.14f, 0.14f, 0.18f), true);
        B(-0.32f, 0.62f, hz + 0.05f, 0.32f, 0.76f, hz + 0.67f, hood, false);
        for (int i = 0; i < 3; i++)
        {
            float bx = -0.22f + i * 0.22f;
            B(bx - 0.1f, 0.62f, hz + 0.75f, bx + 0.1f, 0.82f, hz + 0.95f, BallColors[(int)(Hash(p, 30 + i) * BallColors.Length) % BallColors.Length], false);
        }

        // the score screens over the approach, facing back at the settees, on poles to the ceiling
        float sy = Math.Min(2.5f, H - 0.7f), sz = z0 + spec.Approach * 0.4f;
        foreach (float c in lanes)
        {
            B(c - 0.45f, sy, sz, c + 0.45f, sy + 0.52f, sz + 0.06f, C(0.06f, 0.06f, 0.08f), false);
            B(c - 0.42f, sy + 0.03f, sz - 0.002f, c + 0.42f, sy + 0.49f, sz, Lit(0.12f, 0.22f, 0.70f), false);
            for (int row = 0; row < 3; row++)
                B(c - 0.38f, sy + 0.1f + row * 0.13f, sz - 0.004f, c + 0.38f, sy + 0.12f + row * 0.13f, sz - 0.002f, Lit(0.94f, 0.94f, 0.80f), false);
            B(c - 0.02f, sy + 0.52f, sz + 0.02f, c + 0.02f, H, sz + 0.04f, C(0.2f, 0.2f, 0.22f), false);
        }

        // the masking unit over the decks: a themed panel, the lane numbers, the bowl / don't-bowl light
        var theme = Masking[(int)(Hash(p, 12) * Masking.Length) % Masking.Length];
        float mz = laneEnd - 1.15f * scale, top = Math.Min(H, mini ? 2.4f : 3.3f), bottom = mini ? 0.6f : 0.85f;
        B(-w, bottom, mz, w, top, mz + 0.3f, theme.Ground, false);
        for (int i = 0; i < 5; i++)
        {
            float x0 = -w + 0.2f + i * (p.W - 0.4f) / 5, band = bottom + 0.25f + (i % 2) * 0.35f;
            B(x0, band, mz - 0.01f, x0 + 0.45f, band + 0.12f, mz, i % 2 == 0 ? theme.A : theme.B, false);
        }
        foreach (float c in lanes)
        {
            B(c - 0.22f, top - 0.6f, mz - 0.02f, c + 0.22f, top - 0.2f, mz, Lit(0.96f, 0.96f, 0.90f), false);
            B(c - 0.1f, top - 0.52f, mz - 0.03f, c + 0.1f, top - 0.28f, mz - 0.02f, theme.Ground, false);
            B(c - 0.05f, bottom - 0.12f, mz - 0.02f, c + 0.05f, bottom - 0.04f, mz, Lit(0.20f, 0.90f, 0.30f), false);
        }
        // a dark bulkhead above it to the ceiling, the curtain behind the pits
        if (H > top + 0.05f) B(-w, top, mz, w, H, mz + 0.3f, C(0.06f, 0.06f, 0.08f), false);
        B(-w, 0, laneEnd + 0.6f * scale, w, bottom, laneEnd + 0.65f * scale, Pit, false);

        // the pinsetters and their service aisle, behind the masking unit
        float m0 = laneEnd + 0.65f * scale;
        if (d - m0 > 0.3f)
        {
            B(-w, 0, m0, w, 0.01f, d, C(0.40f, 0.40f, 0.40f), false);
            foreach (float c in lanes)
                B(c - 0.7f, 0, m0 + 0.1f, c + 0.7f, Math.Min(H - 0.1f, mini ? 1.4f : 2.2f), Math.Min(d - 0.2f, m0 + (mini ? 0.8f : 2.4f)), MachineGrey, false);
        }
        // nobody walks into the machines: the masking unit down to the floor and everything behind it collides
        CollisionBox(s, at, basis, new Vector3(-w, 0, mz), new Vector3(w, H, d));
    }

    /// <summary>Ten pins in their triangle on a deck, the head pin at <paramref name="head"/>, 0.305 m apart [S].</summary>
    private static void Pins(float c, float head, float scale, Action<float, float, float, float, float, float, Color, bool> B)
    {
        float spacing = 0.305f * scale, row = 0.264f * scale, r = 0.06f * scale, ph = 0.381f * scale;
        for (int k = 0; k < 4; k++)
            for (int i = 0; i <= k; i++)
            {
                float x = c + (i - k / 2f) * spacing, z = head + k * row;
                B(x - r, 0.025f, z - r, x + r, 0.025f + ph * 0.6f, z + r, PinWhite, false);
                B(x - r * 0.55f, 0.025f + ph * 0.6f, z - r * 0.55f, x + r * 0.55f, 0.025f + ph * 0.72f, z + r * 0.55f, PinRed, false);
                B(x - r * 0.75f, 0.025f + ph * 0.72f, z - r * 0.75f, x + r * 0.75f, 0.025f + ph, z + r * 0.75f, PinWhite, false);
            }
    }
}
