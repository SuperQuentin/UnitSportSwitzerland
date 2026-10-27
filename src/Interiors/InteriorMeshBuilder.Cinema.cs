using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A cinema's pieces (#1238): the seating bank and the screen of an auditorium, its exit signs and
/// porthole; the lobby's box office, concession stand, posters, standee and stanchions; the booth's
/// projector, rack, platter and control panel. Authored back to −Z like every piece.
/// </summary>
public static partial class InteriorMeshBuilder
{
    private static bool IsCinemaPiece(FurnitureType t) => t is FurnitureType.SeatingBank or FurnitureType.MovieScreen
        or FurnitureType.ExitSign or FurnitureType.Porthole or FurnitureType.BoxOffice or FurnitureType.ConcessionStand
        or FurnitureType.PosterFrame or FurnitureType.Standee or FurnitureType.Stanchions or FurnitureType.Projector
        or FurnitureType.ServerRack or FurnitureType.FilmPlatter or FurnitureType.BoothPanel
        or FurnitureType.Loudspeaker or FurnitureType.WallSconce;

    /// <summary>Velvet for the seats: the classic red, a modern dark blue, a burgundy.</summary>
    private static readonly Color[] Velvet = { C(0.62f, 0.06f, 0.10f), C(0.10f, 0.14f, 0.34f), C(0.42f, 0.06f, 0.14f) };

    /// <summary>
    /// A screen's picture in its piece's frame (front +Z): centre and half extents. Scope, 2.39:1 [S],
    /// its bottom 0.9 m up, as wide as the curtains leave and as tall as the room lets it be. Shared with
    /// the booth's picture on it (<see cref="CinemaBooths"/>), so the cat lands on the screen exactly.
    /// </summary>
    public static (Vector3 Center, Vector2 Half) ScreenPicture(FurniturePlan p)
    {
        float w = p.W / 2 - 0.65f, bottom = 0.9f;
        float h = Math.Min(w * 2 / 2.39f, p.H - 0.5f - bottom);
        w = Math.Min(w, h * 2.39f / 2);
        return (new Vector3(0, bottom + h / 2, -p.D / 2 + 0.12f), new Vector2(w, h / 2));
    }

    // Every part below keeps clear of a flicker (`zfight`: interiors draw both sides of every face): a part
    // stacked on another sinks 1.2 cm into it, faces of two colours side by side stand 1.2 cm and more
    // apart, a decal straddles its surface and stands 1.2 cm proud of it.
    private static void CinemaPiece(Scratch s, FurniturePlan p, Vector3 at, Basis basis)
    {
        float w = p.W / 2, d = p.D / 2, H = p.H;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col, bool collide = false) =>
            s.Box(at + new Vector3(xa, FloorStart(ya), za), at + new Vector3(xb, yb, zb), col, collide, basis, at);
        var dark = C(0.08f, 0.08f, 0.09f);
        var chrome = C(0.78f, 0.80f, 0.84f);
        var darkWood = C(0.30f, 0.20f, 0.12f);
        const float sink = 0.012f;
        switch (p.Type)
        {
            case FurnitureType.SeatingBank:
            {
                // rows rising from the front (+Z, toward the screen) to the back, an aisle each side with
                // half steps and step lights, a ramp of collision up each aisle so the steps are walked
                const float aisle = 1.1f, seat = 0.58f;
                int rows = Math.Max(1, (int)(p.D / InteriorGenerator.RowPitch));
                float pitch = p.D / rows, rise = H / Math.Max(1, rows - 1 + 1e-3f);
                var velvet = Velvet[(int)(Hash(p, 1) * Velvet.Length) % Velvet.Length];
                var back = (velvet * 0.85f) with { A = 1 };
                var tier = C(0.12f, 0.10f, 0.14f);
                var step = (tier * 0.9f) with { A = 1 };
                float sx0 = -w + aisle, sx1 = w - aisle;
                int seats = Math.Max(1, (int)((sx1 - sx0) / seat));
                float sw = (sx1 - sx0) / seats;
                for (int k = 0; k < rows; k++)
                {
                    float z1 = d - k * pitch, z0 = z1 - pitch, y = k * rise;
                    // the tier reaches 1.5 cm into each aisle, so its side and the aisle's never share a plane
                    if (y > 0.01f) B(sx0 - 0.015f, 0, z0, sx1 + 0.015f, y, z1, tier, true);
                    // the seats of the row: a cushion, a back behind it, armrests between them
                    float cz = z0 + pitch * 0.45f;
                    B(sx0, y + 0.35f, cz - 0.05f, sx1, y + 0.47f, cz + 0.42f, velvet);
                    B(sx0 + 0.015f, y + 0.33f, cz - 0.17f, sx1 - 0.015f, y + 0.95f, cz - 0.035f, back);
                    for (int i = 0; i <= seats; i++)
                    {
                        float x = sx0 + i * sw;
                        B(x - 0.03f, y - sink, cz - 0.15f, x + 0.03f, y + 0.62f, cz + 0.4f, dark);
                    }
                    // the aisles: two half steps a row, a step light at each
                    foreach (float ax in new[] { -w, w - aisle })
                    {
                        if (y > 0.01f)
                        {
                            B(ax, 0, z0, ax + aisle, y - rise / 2, z1, step);
                            B(ax, 0, z0, ax + aisle, y, z0 + pitch / 2, step);
                        }
                        float lx = ax < 0 ? ax + aisle - 0.08f : ax + 0.04f;
                        B(lx, y - sink, z1 - 0.07f, lx + 0.04f, y + 0.05f, z1 - 0.03f, Lit(1.0f, 0.62f, 0.20f));
                    }
                }
                // walked up the aisles on a ramp, the front of each row a step
                foreach (float ax in new[] { -w, w - aisle })
                {
                    var p0 = at + basis * new Vector3(ax, 0, d);
                    var p1 = at + basis * new Vector3(ax + aisle, 0, d);
                    var p2 = at + basis * new Vector3(ax + aisle, (rows - 1) * rise, -d);
                    var p3 = at + basis * new Vector3(ax, (rows - 1) * rise, -d);
                    s.Col.Add(p0); s.Col.Add(p1); s.Col.Add(p2); s.Col.Add(p0); s.Col.Add(p2); s.Col.Add(p3);
                }
                break;
            }
            case FurnitureType.MovieScreen:
            {
                // a low stage, the black masking on it, the picture, the red curtains drawn back each side
                var (c, half) = ScreenPicture(p);
                B(-w, 0, -d, w, 0.35f, d, darkWood, true);
                B(-w + 0.015f, 0.35f - sink, -d + 0.015f, w - 0.015f, H, -d + 0.08f, dark);
                B(c.X - half.X, c.Y - half.Y, -d + 0.065f, c.X + half.X, c.Y + half.Y, -d + 0.095f, C(0.86f, 0.87f, 0.90f));
                var red = C(0.56f, 0.04f, 0.08f);
                var fold = (red * 0.82f) with { A = 1 };
                foreach (float side in new[] { -1f, 1f })
                    for (int i = 0; i < 4; i++)
                    {
                        // the folds alternate front and back, their depths apart, so two never share a plane
                        float x0 = side < 0 ? -w + 0.015f + i * 0.16f : w - 0.015f - (i + 1) * 0.16f;
                        float z = -d + 0.12f + (i % 2) * 0.115f;
                        B(x0, 0.35f - sink, z, x0 + 0.16f, H - 0.25f, z + 0.1f, i % 2 == 0 ? red : fold);
                    }
                B(-w, H - 0.35f, -d + 0.1f, w, H - 0.15f, -d + 0.35f, red);
                break;
            }
            case FurnitureType.ExitSign:
            {
                B(-w, 0, -d, w, H, d, Lit(0.06f, 0.62f, 0.24f));
                // the running man and his door, white on green
                var white = Lit(0.96f, 0.98f, 0.96f);
                B(-w + 0.06f, 0.04f, d - sink, -w + 0.16f, H - 0.04f, d + sink, white);
                B(-0.03f, 0.05f, d - sink, 0.02f, 0.13f, d + sink, white);
                B(-0.02f, 0.15f, d - sink, 0.02f, 0.19f, d + sink, white);
                break;
            }
            case FurnitureType.Loudspeaker:
            {
                // a black cabinet, its grille a hand proud, a woofer under a horn [S: surround speakers on the
                // side walls, 2.5-3.5 m up, every 3-4 m; the screen channels in tall stacks either side]
                B(-w, 0, -d, w, H, d, dark);
                var grille = C(0.16f, 0.16f, 0.17f);
                B(-w + 0.03f, 0.03f, d - sink, w - 0.03f, H - 0.03f, d + sink, grille);
                var cone = C(0.30f, 0.30f, 0.32f);
                float c = Math.Min(w, H / 4) * 0.7f;
                B(-c, H * 0.3f - c, d, c, H * 0.3f + c, d + 2 * sink, cone);
                B(-c * 0.8f, H * 0.72f - c * 0.45f, d, c * 0.8f, H * 0.72f + c * 0.45f, d + 2 * sink, cone);
                break;
            }
            case FurnitureType.WallSconce:
                // a dark plate on the wall, an uplight shell glowing warm over it
                B(-w * 0.4f, 0, -d, w * 0.4f, H, -d + 0.03f, darkWood);
                B(-w, H * 0.35f, -d + 0.03f - sink, w, H * 0.75f, d, Lit(1.0f, 0.74f, 0.42f));
                break;
            case FurnitureType.Porthole:
                // the projection port and the viewing port beside it, lit from the booth behind
                B(-w, 0, -d, w, H, d, dark);
                B(-w + 0.08f, 0.08f, d - sink, -0.05f, H - 0.08f, d + sink, Lit(0.55f, 0.62f, 0.75f));
                B(0.1f, 0.12f, d - sink, w - 0.1f, H - 0.15f, d + sink, Lit(0.30f, 0.30f, 0.38f));
                break;
            case FurnitureType.BoxOffice:
            {
                // the back wall cabinet, a gap for the cashier, the counter with its glass and its screen
                B(-w, 0, -d, w, 1.1f, -d + 0.45f, darkWood);
                B(-w, 0, d - 0.8f, w, 1.05f, d, C(0.46f, 0.06f, 0.10f), true);
                B(-w - 0.03f, 1.05f - sink, d - 0.84f, w + 0.03f, 1.1f, d + 0.04f, C(0.88f, 0.76f, 0.40f));
                B(-w + 0.015f, 1.1f - sink, d - 0.42f, w - 0.015f, 1.95f, d - 0.4f, Lit(0.60f, 0.70f, 0.78f) with { A = 0.25f });
                B(-0.3f, 1.1f - sink, d - 0.7f, 0.3f, 1.42f, d - 0.6f, dark);
                B(-0.27f, 1.13f, d - 0.612f, 0.27f, 1.39f, d - 0.588f, Lit(0.30f, 0.50f, 0.90f));
                // the TICKETS board over it
                B(-w * 0.6f, H - 0.33f, d - 0.54f, w * 0.6f, H, d - 0.44f, Lit(0.96f, 0.80f, 0.20f));
                break;
            }
            case FurnitureType.ConcessionStand:
            {
                // the back counter: soda fountain, the lit menu boards over it
                B(-w, 0, -d, w, 0.95f, -d + 0.6f, darkWood);
                B(-w * 0.3f, 0.95f - sink, -d + 0.1f, w * 0.3f, 1.4f, -d + 0.5f, chrome);
                for (int i = 0; i < 4; i++)
                    B(-w * 0.25f + i * w * 0.15f, 1.15f, -d + 0.49f, -w * 0.25f + i * w * 0.15f + 0.04f, 1.25f, -d + 0.56f, dark);
                int boards = Math.Max(2, (int)(p.W / 1.4f));
                for (int i = 0; i < boards; i++)
                {
                    float x0 = -w + 0.1f + i * (p.W - 0.2f) / boards, x1 = x0 + (p.W - 0.2f) / boards - 0.08f;
                    B(x0, 1.7f, -d, x1, H - 0.05f, -d + 0.06f, Lit(0.12f, 0.12f, 0.16f));
                    for (int line = 0; line < 4; line++)
                        B(x0 + 0.08f, 1.85f + line * 0.16f, -d + 0.05f, x1 - 0.08f, 1.88f + line * 0.16f, -d + 0.075f, Lit(0.98f, 0.86f, 0.40f));
                }
                // the front counter: a lit candy case in its face, the popcorn machine glowing at one end
                B(-w, 0, d - 0.85f, w, 1.0f, d, C(0.56f, 0.06f, 0.10f), true);
                B(-w - 0.03f, 1.0f - sink, d - 0.89f, w + 0.03f, 1.05f, d + 0.04f, C(0.90f, 0.84f, 0.72f));
                B(-w + 0.3f, 0.2f, d - sink, -0.2f, 0.85f, d + 0.015f, Lit(0.98f, 0.70f, 0.40f));
                float px = w - 0.55f;
                B(px - 0.42f, 1.05f - sink, d - 0.82f, px + 0.42f, 1.12f, d - 0.08f, (chrome * 0.6f) with { A = 1 });
                B(px - 0.38f, 1.12f - sink, d - 0.78f, px + 0.38f, 1.88f, d - 0.12f, Lit(0.99f, 0.86f, 0.40f));
                B(px - 0.42f, 1.88f - sink, d - 0.82f, px + 0.42f, 1.98f, d - 0.08f, C(0.80f, 0.10f, 0.10f));
                break;
            }
            case FurnitureType.PosterFrame:
            {
                // a lightbox: a frame, the poster lit from behind, a title band and a figure
                B(-w, 0.5f, -d, w, H, -d + 0.08f, (chrome * 0.5f) with { A = 1 });
                var bg = Lit(0.10f + 0.6f * Hash(p, 1), 0.10f + 0.4f * Hash(p, 2), 0.25f + 0.6f * Hash(p, 3));
                B(-w + 0.05f, 0.55f, -d + 0.068f, w - 0.05f, H - 0.05f, -d + 0.092f, bg);
                B(-w + 0.1f, H - 0.4f, -d + 0.08f, w - 0.1f, H - 0.2f, -d + 0.104f, Lit(0.98f, 0.92f, 0.70f));
                B(-0.15f, 0.8f, -d + 0.08f, 0.15f, 1.5f, -d + 0.104f, Lit(0.10f, 0.08f, 0.10f));
                break;
            }
            case FurnitureType.Standee:
            {
                // a cardboard cut-out on its foot: a hero, cape and all, the title over it
                var cape = C(0.20f + 0.7f * Hash(p, 1), 0.10f, 0.60f * Hash(p, 2));
                B(-w, 0, -d, w, 0.04f, d, darkWood);
                B(-0.18f, 0.04f - sink, -0.02f, 0.18f, 0.9f, 0.02f, C(0.20f, 0.24f, 0.50f));
                B(-0.2f, 0.9f - sink, -0.035f, 0.2f, 1.5f, 0.035f, cape);
                B(-0.12f, 1.5f - sink, -0.02f, 0.12f, 1.78f, 0.02f, C(0.86f, 0.70f, 0.56f));
                B(-w, 0.3f, -0.03f, -0.215f, 1.6f, -0.01f, (cape * 0.8f) with { A = 1 });
                B(-w + 0.05f, 1.8f, -0.02f, w - 0.05f, H, 0.02f, C(0.96f, 0.80f, 0.20f));
                break;
            }
            case FurnitureType.Stanchions:
            {
                int posts = Math.Max(2, (int)(p.W / 1.0f) + 1);
                for (int i = 0; i < posts; i++)
                {
                    float x = -w + i * p.W / (posts - 1);
                    B(x - 0.15f, 0, -0.15f, x + 0.15f, 0.03f, 0.15f, chrome);
                    B(x - 0.03f, 0.03f - sink, -0.03f, x + 0.03f, 0.98f, 0.03f, chrome);
                }
                B(-w, 0.86f, -0.02f, w, 0.9f, 0.02f, C(0.62f, 0.06f, 0.10f));
                break;
            }
            case FurnitureType.Projector:
            {
                // the pedestal, the digital projector on it, its lens to the porthole (+Z)
                B(-0.3f, 0, -0.4f, 0.3f, 0.9f, 0.4f, dark);
                B(-w, 0.9f - sink, -d, w, 1.45f, d - 0.15f, C(0.20f, 0.20f, 0.22f));
                B(-0.12f, 1.05f, d - 0.165f, 0.12f, 1.29f, d, dark);
                B(-0.09f, 1.08f, d - sink, 0.09f, 1.26f, d + sink, Lit(0.70f, 0.80f, 1.00f));
                B(w - 0.2f, 1.45f - sink, -d + 0.1f, w - 0.05f, 1.5f, -d + 0.3f, Lit(0.20f, 0.90f, 0.30f));
                break;
            }
            case FurnitureType.ServerRack:
            {
                B(-w, 0, -d, w, H, d, dark);
                for (int i = 0; i < 8; i++)
                {
                    float y = 0.2f + i * 0.22f;
                    B(-w + 0.05f, y, d - sink, w - 0.05f, y + 0.16f, d + sink, C(0.16f, 0.16f, 0.18f));
                    B(w - 0.12f, y + 0.06f, d, w - 0.08f, y + 0.1f, d + 0.024f, Lit(0.20f, 0.95f * Hash(p, i), 0.30f));
                }
                break;
            }
            case FurnitureType.FilmPlatter:
            {
                // the 35 mm platter, kept for show: three discs of film round a column
                B(-0.06f, 0, -0.06f, 0.06f, 1.5f, 0.06f, chrome);
                foreach (float y in new[] { 0.45f, 0.85f, 1.25f })
                {
                    B(-w, y, -d, w, y + 0.03f, d, (chrome * 0.7f) with { A = 1 });
                    B(-w * 0.8f, y + 0.03f - sink, -d * 0.8f, w * 0.8f, y + 0.07f, d * 0.8f, C(0.30f, 0.20f, 0.12f));
                }
                break;
            }
            case FurnitureType.BoothPanel:
            {
                // the house lights (three keys: full, dim, off) and the screen's switch, labelled
                B(-w, 0.8f, -d, w, H, -d + 0.12f, C(0.22f, 0.22f, 0.24f));
                for (int i = 0; i < 3; i++)
                    B(-w + 0.12f + i * 0.2f, 1.25f, -d + 0.108f, -w + 0.27f + i * 0.2f, 1.4f, -d + 0.16f,
                        Lit(0.98f, 0.98f - 0.3f * i, 0.70f - 0.3f * i));
                B(-w + 0.1f, 1.48f, -d + 0.108f, -w + 0.7f, 1.54f, -d + 0.135f, Lit(0.96f, 0.86f, 0.40f));
                B(w - 0.45f, 1.2f, -d + 0.108f, w - 0.15f, 1.45f, -d + 0.18f, Lit(0.90f, 0.20f, 0.20f));
                B(w - 0.5f, 1.48f, -d + 0.108f, w - 0.1f, 1.54f, -d + 0.135f, Lit(0.40f, 0.80f, 0.98f));
                break;
            }
        }
    }
}
