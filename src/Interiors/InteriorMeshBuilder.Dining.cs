using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A dining venue's pieces (#1242): tables with their chairs, banquettes, high tables, the tiled stove,
/// the pizza oven, the pastry case, the order and kebab counters, table football, the jukebox, a stage,
/// the DJ booth, the dance floor and the kitchen's cook line. Authored back to −Z like every piece; the
/// woods, cloths and pads follow the room they stand in (<see cref="_room"/>).
/// </summary>
public static partial class InteriorMeshBuilder
{
    private static bool IsDiningPiece(FurnitureType t) => t is FurnitureType.TableSet or FurnitureType.BanquetteSet
        or FurnitureType.HighTable or FurnitureType.Kachelofen or FurnitureType.PizzaOven or FurnitureType.PastryCase
        or FurnitureType.OrderCounter or FurnitureType.KebabCounter or FurnitureType.TableFootball or FurnitureType.Jukebox
        or FurnitureType.Stage or FurnitureType.DjBooth or FurnitureType.DanceFloor or FurnitureType.CookLine;

    /// <summary>The room the piece being built stands in, for the pieces whose look follows it. Per thread.</summary>
    [ThreadStatic] private static RoomType _room;

    private static RoomType RoomOf(InteriorLayout l, FurniturePlan p)
    {
        if (p.Floor < 0 || p.Floor >= l.Floors.Count) return RoomType.Hall;
        foreach (var r in l.Floors[p.Floor].Rooms)
            if (p.X >= r.X0 && p.X <= r.X1 && p.Z >= r.Z0 && p.Z <= r.Z1) return r.Type;
        return RoomType.Hall;
    }

    /// <summary>A venue's wood, its tabletop, its chairs and its pads, by its kind.</summary>
    private static (Color Wood, Color Top, Color Chair, Color Pad) DiningLook(FurniturePlan p) => _room switch
    {
        RoomType.Gaststube => (C(0.36f, 0.22f, 0.12f), C(0.93f, 0.92f, 0.86f), C(0.42f, 0.26f, 0.14f),
            Hash(p, 7) < 0.5f ? C(0.18f, 0.32f, 0.20f) : C(0.50f, 0.10f, 0.10f)),
        RoomType.Pizzeria => (C(0.55f, 0.36f, 0.20f), C(0.78f, 0.12f, 0.12f), C(0.52f, 0.34f, 0.18f), C(0.16f, 0.36f, 0.18f)),
        RoomType.CafeRoom => (C(0.22f, 0.13f, 0.08f), C(0.88f, 0.88f, 0.86f), C(0.22f, 0.13f, 0.08f), C(0.55f, 0.08f, 0.12f)),
        RoomType.FastFoodRoom => (C(0.70f, 0.70f, 0.72f), C(0.95f, 0.78f, 0.18f), C(0.86f, 0.18f, 0.14f), C(0.86f, 0.18f, 0.14f)),
        RoomType.KebabRoom => (C(0.70f, 0.70f, 0.72f), C(0.85f, 0.85f, 0.82f), C(0.20f, 0.20f, 0.22f), C(0.36f, 0.20f, 0.12f)),
        RoomType.PubRoom => (C(0.25f, 0.15f, 0.08f), C(0.32f, 0.19f, 0.10f), C(0.30f, 0.18f, 0.09f), C(0.40f, 0.10f, 0.08f)),
        RoomType.BarRoom or RoomType.ClubRoom => (C(0.10f, 0.10f, 0.11f), C(0.16f, 0.14f, 0.18f), C(0.12f, 0.12f, 0.14f), C(0.30f, 0.10f, 0.40f)),
        _ => (C(0.45f, 0.30f, 0.18f), C(0.55f, 0.38f, 0.22f), C(0.45f, 0.30f, 0.18f), C(0.30f, 0.30f, 0.34f)),
    };

    // As every venue's: a part stacked on another sinks 1.2 cm into it and keeps 1.5 cm in from the faces it
    // would share, a decal straddles its surface and stands 1.2 cm proud of it, what meets the floor starts
    // under it (`zfight`).
    private static void DiningPiece(Scratch s, FurniturePlan p, Vector3 at, Basis basis)
    {
        float w = p.W / 2, d = p.D / 2, H = p.H;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col, bool collide = false) =>
            s.Box(at + new Vector3(xa, FloorStart(ya), za), at + new Vector3(xb, yb, zb), col, collide, basis, at);
        var (wood, top, chairCol, pad) = DiningLook(p);
        var dark = C(0.08f, 0.08f, 0.09f);
        var steel = C(0.70f, 0.72f, 0.74f);
        const float sink = 0.012f;

        // a chair at (cx, cz), facing the table along -dir: dir is the unit step away from the table
        void Chair(float cx, float cz, int dx, int dz)
        {
            const float half = 0.2f, seat = 0.45f;
            // the seat's box, then its back on the side away from the table
            float x0 = cx - half, x1 = cx + half, z0 = cz - half, z1 = cz + half;
            B(x0, seat - 0.05f, z0, x1, seat, z1, chairCol);
            foreach (float lx in new[] { x0 + 0.02f, x1 - 0.06f })
                foreach (float lz in new[] { z0 + 0.02f, z1 - 0.06f })
                    B(lx, 0, lz, lx + 0.04f, seat - 0.05f + sink, lz + 0.04f, dark);
            if (dx > 0) B(x1 - 0.05f, seat - sink, z0 + 0.015f, x1 - 0.01f, 0.92f, z1 - 0.015f, chairCol);
            else if (dx < 0) B(x0 + 0.01f, seat - sink, z0 + 0.015f, x0 + 0.05f, 0.92f, z1 - 0.015f, chairCol);
            else if (dz > 0) B(x0 + 0.015f, seat - sink, z1 - 0.05f, x1 - 0.015f, 0.92f, z1 - 0.01f, chairCol);
            else B(x0 + 0.015f, seat - sink, z0 + 0.01f, x1 - 0.015f, 0.92f, z0 + 0.05f, chairCol);
        }
        // a tabletop on its pedestal, its cloth checked in a pizzeria
        void Table(float cx, float cz, float hw, float hd, float y)
        {
            B(cx - 0.25f, 0, cz - 0.25f, cx + 0.25f, 0.03f, cz + 0.25f, dark);
            B(cx - 0.04f, 0.03f - sink, cz - 0.04f, cx + 0.04f, y - 0.04f + sink, cz + 0.04f, dark);
            B(cx - hw, y - 0.04f, cz - hd, cx + hw, y, cz + hd, top);
            if (_room == RoomType.Pizzeria)
            {
                // white squares on the red, a check board with gaps: no two squares share an edge
                const float cell = 0.16f;
                var white = C(0.95f, 0.94f, 0.90f);
                int nx = (int)(2 * hw / cell), nz = (int)(2 * hd / cell);
                for (int i = 0; i < nx; i++)
                    for (int k = 0; k < nz; k++)
                    {
                        if ((i + k) % 2 == 1) continue;
                        float sx = cx - hw + i * cell + 0.01f, sz = cz - hd + k * cell + 0.01f;
                        B(sx, y - sink, sz, sx + cell - 0.02f, y + sink, sz + cell - 0.02f, white);
                    }
            }
        }

        switch (p.Type)
        {
            case FurnitureType.TableSet:
            {
                // chairs on the long sides, and on the ends when the set is wide enough; two a side round a Stammtisch
                const float chair = 0.42f;
                bool ends = p.W >= 1.6f;
                float hw = ends ? w - chair : w - 0.17f, hd = d - chair;
                Table(0, 0, hw, hd, 0.76f);
                int per = p.W >= 1.85f ? 2 : 1;
                for (int i = 0; i < per; i++)
                {
                    float off = per == 1 ? 0 : (i - 0.5f) * hw;
                    Chair(off, d - 0.2f, 0, 1);
                    Chair(off, -d + 0.2f, 0, -1);
                    if (!ends) continue;
                    Chair(w - 0.2f, off, 1, 0);
                    Chair(-w + 0.2f, off, -1, 0);
                }
                break;
            }
            case FurnitureType.BanquetteSet:
            {
                // a bench along the wall with its back, a table before it, chairs across
                float back = _room == RoomType.FastFoodRoom || _room == RoomType.KebabRoom ? 1.2f : 1.0f;
                B(-w, 0, -d, w, 0.4f, -d + 0.55f, wood);
                B(-w + 0.015f, 0.4f - sink, -d + 0.12f, w - 0.015f, 0.47f, -d + 0.54f, pad);
                B(-w + 0.015f, 0.4f - sink, -d + 0.015f, w - 0.015f, back, -d + 0.12f, pad);
                float tz = -d + 0.55f + 0.38f;
                Table(0, tz, w - 0.15f, 0.34f, 0.76f);
                int n = p.W >= 1.4f ? 2 : 1;
                for (int i = 0; i < n; i++) Chair(n == 1 ? 0 : (i - 0.5f) * (p.W - 0.6f), d - 0.2f, 0, 1);
                break;
            }
            case FurnitureType.HighTable:
            {
                // a tall pedestal table and four stools round it
                B(-0.25f, 0, -0.25f, 0.25f, 0.03f, 0.25f, dark);
                B(-0.04f, 0.03f - sink, -0.04f, 0.04f, H - 0.04f + sink, 0.04f, dark);
                B(-0.35f, H - 0.04f, -0.35f, 0.35f, H, 0.35f, top);
                foreach (var (sx, sz) in new[] { (0f, d - 0.18f), (0f, -d + 0.18f), (w - 0.18f, 0f), (-w + 0.18f, 0f) })
                {
                    B(sx - 0.03f, 0, sz - 0.03f, sx + 0.03f, 0.72f + sink, sz + 0.03f, dark);
                    B(sx - 0.17f, 0.72f, sz - 0.17f, sx + 0.17f, 0.77f, sz + 0.17f, pad);
                }
                break;
            }
            case FurnitureType.Kachelofen:
            {
                // the tiled stove on its plinth, a cornice and a crown, the Ofenbank before it
                var tile = C(0.30f, 0.48f, 0.38f);
                var joint = (tile * 0.7f) with { A = 1 };
                float body = -d + 0.6f;
                B(-w, 0, -d, w, 0.15f, body, C(0.40f, 0.38f, 0.36f));
                B(-w + 0.05f, 0.15f - sink, -d + 0.015f, w - 0.05f, 1.5f, body - 0.05f, tile);
                for (int k = 1; k < 5; k++)
                    B(-w + 0.065f, 0.15f + k * 0.27f, body - 0.05f - sink, w - 0.065f, 0.17f + k * 0.27f, body - 0.05f + sink, joint);
                B(-w + 0.02f, 1.5f - sink, -d, w - 0.02f, 1.58f, body - 0.02f, tile);
                B(-w + 0.12f, 1.58f - sink, -d + 0.1f, w - 0.12f, H, body - 0.12f, tile);
                B(-w, 0.4f, body, w, 0.46f, d, wood);
                foreach (float lx in new[] { -w + 0.05f, w - 0.1f })
                    B(lx, 0, d - 0.1f, lx + 0.05f, 0.4f + sink, d - 0.05f, wood);
                break;
            }
            case FurnitureType.PizzaOven:
            {
                // the brick base with the wood under it, the dome in tiers, its glowing mouth, the flue
                var brick = C(0.62f, 0.30f, 0.20f);
                var dome = C(0.72f, 0.42f, 0.28f);
                B(-w, 0, -d, w, 0.95f, d, brick);
                B(-w + 0.3f, 0.12f, d - sink, w - 0.3f, 0.62f, d + sink, dark);
                for (int i = 0; i < 4; i++)
                    B(-w + 0.4f + i * 0.25f, 0.13f, d - 0.3f, -w + 0.6f + i * 0.25f, 0.25f, d + 0.04f, C(0.50f, 0.32f, 0.18f));
                B(-w + 0.1f, 0.95f - sink, -d + 0.1f, w - 0.1f, 1.4f, d - 0.1f, dome);
                B(-w + 0.35f, 1.4f - sink, -d + 0.3f, w - 0.35f, 1.65f, d - 0.3f, dome);
                B(-0.3f, 1.0f, d - 0.1f - sink, 0.3f, 1.28f, d - 0.1f + sink, Lit(1.00f, 0.55f, 0.15f));
                B(-0.15f, 1.65f - sink, -d + 0.45f, 0.15f, H, -d + 0.75f, (brick * 0.7f) with { A = 1 });
                break;
            }
            case FurnitureType.PastryCase:
            {
                // the back counter with the coffee machine, the server's aisle, the lit case of cakes in front
                var cream = C(0.94f, 0.90f, 0.82f);
                B(-w, 0, -d, w, 0.9f, -d + 0.5f, wood);
                B(-w + 0.2f, 0.9f - sink, -d + 0.05f, -w + 0.9f, 1.35f, -d + 0.42f, Chrome);
                B(-w + 0.3f, 1.05f, -d + 0.42f - sink, -w + 0.8f, 1.25f, -d + 0.42f + sink, Lit(0.30f, 0.30f, 0.32f));
                B(-w, 0, d - 0.8f, w, 0.9f, d, cream, true);
                B(-w + 0.03f, 0.9f - sink, d - 0.77f, w - 0.03f, 0.93f, d - 0.03f, Lit(0.98f, 0.96f, 0.90f));
                Color[] cakes = { C(0.95f, 0.60f, 0.70f), C(0.45f, 0.26f, 0.14f), C(0.96f, 0.92f, 0.80f), C(0.85f, 0.15f, 0.20f), C(0.98f, 0.82f, 0.40f) };
                int n = Math.Max(3, (int)((p.W - 0.2f) / 0.3f));
                for (int i = 0; i < n; i++)
                {
                    float cx = -w + 0.15f + i * (p.W - 0.3f) / n;
                    B(cx, 0.93f - sink, d - 0.6f, cx + 0.2f, 1.03f, d - 0.4f, cakes[(int)(Hash(p, 20 + i) * cakes.Length) % cakes.Length]);
                }
                foreach (float px in new[] { -w + 0.045f, w - 0.085f })
                    foreach (float pz in new[] { d - 0.755f, d - 0.085f })
                        B(px, 0.93f - sink, pz, px + 0.04f, 1.25f + sink, pz + 0.04f, Chrome);
                B(-w + 0.015f, 1.25f, d - 0.79f, w - 0.015f, 1.28f, d - 0.01f, Chrome);
                break;
            }
            case FurnitureType.OrderCounter:
            case FurnitureType.KebabCounter:
            {
                bool kebab = p.Type == FurnitureType.KebabCounter;
                // the back line, the menu boards lit over it, the counter in front with its tills
                B(-w, 0, -d, w, 0.9f, -d + 0.6f, steel);
                B(-w + 0.015f, 0.9f - sink, -d + 0.015f, w - 0.015f, 0.95f, -d + 0.585f, C(0.30f, 0.30f, 0.32f));
                int boards = Math.Max(2, (int)(p.W / 1.2f));
                for (int i = 0; i < boards; i++)
                {
                    float x0 = -w + 0.1f + i * (p.W - 0.2f) / boards, x1 = x0 + (p.W - 0.2f) / boards - 0.08f;
                    B(x0, 1.55f, -d, x1, 2.35f, -d + 0.06f, Lit(0.12f, 0.12f, 0.14f));
                    // the photos of the dishes on it, two rows
                    for (int k = 0; k < 2; k++)
                        for (int j = 0; j < 3; j++)
                        {
                            float px = x0 + 0.06f + j * (x1 - x0 - 0.12f) / 3, pw = (x1 - x0 - 0.12f) / 3 - 0.04f;
                            B(px, 1.65f + k * 0.32f, -d + 0.06f - sink, px + pw, 1.87f + k * 0.32f, -d + 0.06f + sink,
                                Lit(0.70f + 0.3f * Hash(p, 40 + i * 6 + k * 3 + j), 0.40f + 0.4f * Hash(p, 60 + j), 0.15f));
                        }
                }
                if (kebab)
                {
                    // the spit: the meat turning on its rod before the glowing grill
                    float sx = w - 0.45f, sz = -d + 0.35f;
                    B(sx - 0.3f, 0.95f - sink, -d + 0.03f, sx + 0.3f, 1.75f, -d + 0.12f, Lit(1.00f, 0.42f, 0.10f));
                    B(sx - 0.02f, 0.95f - sink, sz - 0.02f, sx + 0.02f, 1.8f, sz + 0.02f, Chrome);
                    B(sx - 0.14f, 1.05f, sz - 0.14f, sx + 0.14f, 1.6f, sz + 0.14f, C(0.58f, 0.32f, 0.14f));
                }
                var front = kebab ? C(0.94f, 0.94f, 0.92f) : C(0.85f, 0.15f, 0.12f);
                B(-w, 0, d - 0.8f, w, 1.0f, d, front, true);
                B(-w - 0.03f, 1.0f - sink, d - 0.84f, w + 0.03f, 1.05f, d + 0.04f, C(0.82f, 0.82f, 0.80f));
                if (kebab)
                {
                    // the salads under the counter's lit glass: tubs of red, green, white
                    Color[] tubs = { C(0.80f, 0.14f, 0.12f), C(0.30f, 0.66f, 0.22f), C(0.94f, 0.94f, 0.90f), C(0.92f, 0.70f, 0.20f) };
                    for (int i = 0; i < (int)((p.W - 1.3f) / 0.4f); i++)
                        B(-w + 0.15f + i * 0.4f, 1.05f - sink, d - 0.7f, -w + 0.45f + i * 0.4f, 1.12f, d - 0.45f, tubs[i % tubs.Length]);
                }
                int tills = kebab ? 1 : p.W >= 3f ? 2 : 1;
                for (int i = 0; i < tills; i++)
                {
                    float tx = kebab ? w - 0.6f : -w + 0.6f + i * (p.W - 1.2f);
                    B(tx - 0.18f, 1.05f - sink, d - 0.6f, tx + 0.18f, 1.12f, d - 0.3f, dark);
                    B(tx - 0.15f, 1.12f - sink, d - 0.55f, tx + 0.15f, 1.35f, d - 0.5f, dark);
                    B(tx - 0.13f, 1.14f, d - 0.5f - sink, tx + 0.13f, 1.33f, d - 0.5f + sink, Lit(0.30f, 0.60f, 0.95f));
                }
                break;
            }
            case FurnitureType.TableFootball:
            {
                // the box on its legs, the green pitch inside, eight rods across with their players and handles
                var box = C(0.36f, 0.24f, 0.14f);
                const float bx = 0.6f, bz = 0.36f, y0 = 0.7f, y1 = 0.92f;
                foreach (float lx in new[] { -bx + 0.07f, bx - 0.13f })
                    foreach (float lz in new[] { -bz + 0.07f, bz - 0.13f })
                        B(lx, 0, lz, lx + 0.06f, y0 + sink, lz + 0.06f, dark);
                B(-bx + 0.045f, y0, -bz + 0.045f, bx - 0.045f, 0.78f, bz - 0.045f, C(0.20f, 0.56f, 0.24f));
                B(-bx, y0, -bz, bx, y1, -bz + 0.03f, box);
                B(-bx, y0, bz - 0.03f, bx, y1, bz, box);
                B(-bx, y0, -bz + 0.015f, -bx + 0.03f, y1 - 0.015f, bz - 0.015f, box);
                B(bx - 0.03f, y0, -bz + 0.015f, bx, y1 - 0.015f, bz - 0.015f, box);
                for (int i = 0; i < 8; i++)
                {
                    float x = -0.49f + i * 0.14f;
                    B(x - 0.008f, 0.86f, -d - 0.1f, x + 0.008f, 0.876f, d + 0.1f, Chrome);
                    float side = i % 2 == 0 ? -1 : 1;
                    B(x - 0.02f, 0.85f, side * (d + 0.03f), x + 0.02f, 0.89f, side * (d + 0.11f), dark);
                    var team = (i == 0 || i == 1 || i == 3 || i == 5) ? C(0.82f, 0.12f, 0.12f) : C(0.12f, 0.24f, 0.78f);
                    int players = i is 0 or 7 ? 1 : i is 1 or 6 ? 2 : i is 3 or 4 ? 5 : 3;
                    for (int k = 0; k < players; k++)
                    {
                        float z = players == 1 ? 0 : -0.25f + k * 0.5f / (players - 1);
                        B(x - 0.02f, 0.8f, z - 0.03f, x + 0.02f, 0.9f, z + 0.03f, team);
                    }
                }
                break;
            }
            case FurnitureType.Jukebox:
            {
                B(-w, 0, -d, w, 1.2f, d, C(0.40f, 0.16f, 0.10f));
                B(-w + 0.05f, 1.2f - sink, -d + 0.05f, w - 0.05f, H, d - 0.05f, Lit(0.98f, 0.62f, 0.20f));
                Color[] bars = { Lit(0.98f, 0.30f, 0.40f), Lit(0.30f, 0.80f, 0.98f), Lit(0.98f, 0.86f, 0.30f) };
                for (int i = 0; i < 3; i++)
                {
                    B(-w + 0.05f + i * 0.04f, 0.2f, d - sink, -w + 0.08f + i * 0.04f, 1.15f, d + sink, bars[i]);
                    B(w - 0.08f - i * 0.04f, 0.2f, d - sink, w - 0.05f - i * 0.04f, 1.15f, d + sink, bars[i]);
                }
                B(-0.2f, 0.7f, d - sink, 0.2f, 1.0f, d + sink, Lit(0.96f, 0.92f, 0.80f));
                break;
            }
            case FurnitureType.Stage:
            {
                // the black platform with its edge lit, a backdrop, two speakers and a microphone
                B(-w, 0, -d, w, H, d, C(0.08f, 0.08f, 0.09f), true);
                B(-w + 0.05f, H - 0.08f, d - sink, w - 0.05f, H - 0.05f, d + sink, Lit(0.98f, 0.70f, 0.20f));
                B(-w + 0.015f, H - sink, -d + 0.015f, w - 0.015f, H + 2.2f, -d + 0.075f, C(0.36f, 0.06f, 0.10f));
                foreach (float sx in new[] { -w + 0.35f, w - 0.35f })
                    B(sx - 0.22f, H - sink, d - 0.6f, sx + 0.22f, H + 0.75f, d - 0.25f, dark);
                B(-0.01f, H - sink, 0.2f, 0.01f, H + 1.5f, 0.22f, Chrome);
                B(-0.03f, H + 1.48f, 0.19f, 0.03f, H + 1.56f, 0.25f, dark);
                break;
            }
            case FurnitureType.DjBooth:
            {
                // the desk with its LED face, two decks and the mixer on it, the lit wall behind
                B(-w, 0, d - 0.7f, w, 1.1f, d, dark, true);
                for (int i = 0; i < 5; i++)
                    B(-w + 0.1f, 0.2f + i * 0.17f, d - sink, w - 0.1f, 0.26f + i * 0.17f, d + sink,
                        Lit(0.30f + 0.6f * Hash(p, 30 + i), 0.20f, 0.90f - 0.5f * Hash(p, 31 + i)));
                foreach (float dx in new[] { -0.75f, 0.75f })
                {
                    B(dx - 0.25f, 1.1f - sink, d - 0.6f, dx + 0.25f, 1.15f, d - 0.15f, C(0.30f, 0.30f, 0.32f));
                    B(dx - 0.17f, 1.15f - sink, d - 0.55f, dx + 0.17f, 1.15f + sink, d - 0.21f, Lit(0.20f, 0.85f, 0.95f));
                }
                B(-0.25f, 1.1f - sink, d - 0.6f, 0.25f, 1.17f, d - 0.15f, C(0.18f, 0.18f, 0.20f));
                B(-0.2f, 1.17f - sink, d - 0.5f, 0.2f, 1.17f + sink, d - 0.25f, Lit(0.98f, 0.30f, 0.70f));
                B(-w + 0.1f, 1.4f, -d, w - 0.1f, 2.4f, -d + 0.05f, Lit(0.30f, 0.12f, 0.62f));
                break;
            }
            case FurnitureType.DanceFloor:
            {
                // lit tiles with dark joints between them, laid from the piece's own place so every peer lays the same
                Color[] tiles = { Lit(0.98f, 0.20f, 0.70f), Lit(0.20f, 0.90f, 0.98f), Lit(0.98f, 0.86f, 0.12f), Lit(0.50f, 0.20f, 0.96f) };
                const float cell = 0.6f, y0 = 0.012f, y1 = 0.026f;
                int nx = Math.Max(1, (int)(p.W / cell)), nz = Math.Max(1, (int)(p.D / cell));
                float ox = -nx * cell / 2, oz = -nz * cell / 2;
                for (int i = 0; i < nx; i++)
                    for (int k = 0; k < nz; k++)
                    {
                        float x = ox + i * cell, z = oz + k * cell;
                        B(x + 0.03f, y0, z + 0.03f, x + cell - 0.03f, y1, z + cell - 0.03f, tiles[(int)(Hash(p, 100 + i * 31 + k) * tiles.Length) % tiles.Length]);
                    }
                break;
            }
            case FurnitureType.CookLine:
            {
                // ranges and a fryer under the hood, pots on the burners, the hood's lamps
                B(-w, 0, -d, w, 0.9f, d, steel);
                B(-w + 0.015f, 0.9f - sink, -d + 0.015f, w - 0.015f, 0.93f, d - 0.015f, C(0.24f, 0.24f, 0.26f));
                int n = Math.Max(1, (int)(p.W / 0.8f));
                for (int i = 0; i < n; i++)
                {
                    float cx = -w + (i + 0.5f) * p.W / n;
                    B(cx - 0.3f, 0.3f, d - sink, cx + 0.3f, 0.75f, d + sink, C(0.50f, 0.52f, 0.54f));
                    if (i == n - 1 && n > 1)
                    {
                        // the fryer: its basket over hot oil
                        B(cx - 0.25f, 0.93f - sink, -0.25f, cx + 0.25f, 0.93f + sink, 0.25f, Lit(0.90f, 0.70f, 0.20f));
                        continue;
                    }
                    foreach (float bz in new[] { -0.2f, 0.2f })
                    {
                        B(cx - 0.12f, 0.93f - sink, bz - 0.12f, cx + 0.12f, 0.93f + sink, bz + 0.12f, C(0.12f, 0.12f, 0.13f));
                        if (Hash(p, 10 + i * 2 + (bz > 0 ? 1 : 0)) < 0.5f)
                            B(cx - 0.1f, 0.925f, bz - 0.1f, cx + 0.1f, 1.08f, bz + 0.1f, Chrome);
                    }
                }
                B(-w, 1.9f, -d, w, H, d - 0.05f, steel);
                for (int i = 0; i < n; i++)
                {
                    float cx = -w + (i + 0.5f) * p.W / n;
                    B(cx - 0.1f, 1.9f - sink, -0.1f, cx + 0.1f, 1.9f + sink, 0.1f, Lit(1.00f, 0.92f, 0.70f));
                }
                break;
            }
        }
    }
}
