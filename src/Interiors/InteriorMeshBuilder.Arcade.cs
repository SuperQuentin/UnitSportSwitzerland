using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// An arcade's pieces (#1240): the prize counter and the change machine by the door, the claw machines,
/// the twin racer, the simulator pod, the dance machine, the drum game, the basketball game and the air
/// hockey table. Uprights and pinballs are the bowling centre's (<see cref="VenuePiece"/>). Authored back
/// to −Z, the player's side +Z, like every piece.
/// </summary>
public static partial class InteriorMeshBuilder
{
    private static bool IsArcadePiece(FurnitureType t) => t is FurnitureType.PrizeCounter or FurnitureType.ChangeMachine
        or FurnitureType.ClawMachine or FurnitureType.Racer or FurnitureType.SimPod or FurnitureType.DanceMachine
        or FurnitureType.DrumGame or FurnitureType.BasketballGame or FurnitureType.AirHockey;

    /// <summary>Plush prizes: pink, yellow, sky blue, lime, white, orange.</summary>
    private static readonly Color[] Plush =
    {
        C(0.96f, 0.52f, 0.72f), C(0.98f, 0.86f, 0.24f), C(0.44f, 0.74f, 0.96f), C(0.56f, 0.88f, 0.30f),
        C(0.94f, 0.94f, 0.92f), C(0.96f, 0.56f, 0.18f),
    };

    // As the cinema's: a part stacked on another sinks 1.2 cm into it and is inset 1.5 cm from the faces
    // it would share, a decal straddles its surface and stands 1.2 cm proud of it (`zfight`).
    private static void ArcadePiece(Scratch s, FurniturePlan p, Vector3 at, Basis basis)
    {
        float w = p.W / 2, d = p.D / 2, H = p.H;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col, bool collide = false) =>
            s.Box(at + new Vector3(xa, FloorStart(ya), za), at + new Vector3(xb, yb, zb), col, collide, basis, at);
        var dark = C(0.08f, 0.08f, 0.09f);
        var body = Vinyl[(int)(Hash(p, 4) * Vinyl.Length) % Vinyl.Length];
        var trim = (body * 0.75f) with { A = 1 };
        var screen = Lit(0.10f + 0.4f * Hash(p, 8), 0.30f + 0.5f * Hash(p, 9), 0.85f);
        var marquee = Lit(0.98f, 0.30f + 0.6f * Hash(p, 10), 0.30f + 0.5f * Hash(p, 11));
        const float sink = 0.012f;
        switch (p.Type)
        {
            case FurnitureType.PrizeCounter:
            {
                // the shelves of prizes behind, the clerk's aisle, the lit glass case in front
                float back = -d + 0.45f;
                B(-w, 0, -d, w, H, back, C(0.20f, 0.12f, 0.30f));
                for (int k = 0; k < 3; k++)
                {
                    float y = 0.55f + k * 0.48f;
                    B(-w + 0.015f, y - 0.03f, back - sink, w - 0.015f, y, back + 0.17f, C(0.86f, 0.84f, 0.80f));
                    int n = Math.Max(3, (int)(p.W / 0.3f));
                    float step = (p.W - 0.2f) / n;
                    for (int i = 0; i < n; i++)
                    {
                        float x = -w + 0.1f + i * step, hgt = 0.18f + 0.18f * Hash(p, 20 + k * 16 + i);
                        B(x + 0.03f, y - sink, back - sink, x + step - 0.03f, y + hgt, back + 0.13f,
                            Plush[(int)(Hash(p, 40 + k * 16 + i) * Plush.Length) % Plush.Length]);
                    }
                }
                B(-w + 0.1f, H - 0.38f, back - sink, w - 0.1f, H - 0.06f, back + 0.05f, Lit(0.98f, 0.30f, 0.70f));
                B(-w, 0, d - 0.8f, w, 0.95f, d, body, true);
                B(-w - 0.03f, 0.95f - sink, d - 0.84f, w + 0.03f, 1.0f, d + 0.04f, Chrome);
                B(-w + 0.1f, 0.15f, d - sink, w - 0.1f, 0.85f, d + sink, Lit(0.90f, 0.86f, 0.70f));
                for (int i = 0; i < 4; i++)
                {
                    float x = -w + 0.25f + i * (p.W - 0.5f) / 4;
                    B(x, 0.35f, d + 0.012f, x + 0.25f, 0.6f, d + 0.03f, Plush[(int)(Hash(p, 60 + i) * Plush.Length) % Plush.Length]);
                }
                break;
            }
            case FurnitureType.ChangeMachine:
            {
                B(-w, 0, -d, w, H, d, C(0.72f, 0.70f, 0.66f));
                B(-w + 0.06f, H - 0.42f, d - sink, w - 0.06f, H - 0.1f, d + sink, Lit(0.98f, 0.82f, 0.20f));
                B(-0.12f, 1.15f, d - sink, 0.12f, 1.2f, d + sink, dark);
                B(-0.1f, 1.24f, d - sink, 0.1f, 1.34f, d + sink, Lit(0.20f, 0.90f, 0.30f));
                B(-0.15f, 0.5f, d - sink, 0.15f, 0.62f, d + 0.08f, Chrome);
                break;
            }
            case FurnitureType.ClawMachine:
            {
                // the cabinet, its control ledge, the box of plush with the claw over it, the lit top: the box
                // is left open (a pane would be window glass to the interior shader, which draws the sky in it)
                const float top = 0.9f, roof = 1.85f;
                B(-w, 0, -d, w, top, d, body);
                B(-w + 0.015f, 0.72f, d - sink, w - 0.015f, top - 0.02f, d + 0.16f, trim);
                B(-0.25f, top - 0.02f - sink, d + 0.04f, -0.21f, 1.0f, d + 0.08f, dark);
                B(-0.27f, 1.0f - sink, d + 0.03f, -0.19f, 1.05f, d + 0.09f, C(0.85f, 0.10f, 0.10f));
                B(0.1f, top - 0.02f - sink, d + 0.04f, 0.2f, top, d + 0.12f, Lit(0.98f, 0.20f, 0.20f));
                B(-w + 0.08f, 0.25f, d - sink, -w + 0.4f, 0.5f, d + sink, dark);
                foreach (var (cx, cz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                {
                    float x0 = cx < 0 ? -w + 0.015f : w - 0.07f, z0 = cz < 0 ? -d + 0.015f : d - 0.07f;
                    B(x0, top - 0.03f, z0, x0 + 0.055f, roof, z0 + 0.055f, dark);
                }
                B(-w + 0.03f, top - sink, -d + 0.04f, w - 0.03f, roof, -d + 0.05f, trim);
                for (int i = 0; i < 6; i++)
                {
                    float x = -w + 0.1f + (i % 3) * (p.W - 0.2f) / 3, z = -d + 0.12f + (i / 3) * (p.D - 0.3f) / 2;
                    float s0 = (p.W - 0.2f) / 3 - 0.05f;
                    B(x, top - sink, z, x + s0, top + 0.1f + 0.12f * Hash(p, 30 + i), z + (p.D - 0.3f) / 2 - 0.05f,
                        Plush[(int)(Hash(p, 50 + i) * Plush.Length) % Plush.Length]);
                }
                B(-0.01f, 1.45f, -0.01f, 0.01f, roof + sink, 0.01f, Chrome);
                B(-0.07f, 1.33f, -0.07f, 0.07f, 1.45f + sink, 0.07f, Chrome);
                B(-w, roof - sink, -d, w, H, d, trim);
                B(-w + 0.05f, roof + 0.04f, d - sink, w - 0.05f, H - 0.04f, d + sink, marquee);
                break;
            }
            case FurnitureType.Racer:
            {
                // the twin screen cabinet against the wall, two dashes with wheels, two bucket seats
                float face = -d + 0.75f;
                B(-w, 0, -d, w, H, face, C(0.10f, 0.10f, 0.12f));
                B(-w + 0.06f, 0.95f, face - sink, -0.03f, 1.55f, face + sink, screen);
                B(0.03f, 0.95f, face - sink, w - 0.06f, 1.55f, face + sink, screen);
                B(-w + 0.03f, 1.65f, face - sink, w - 0.03f, H - 0.05f, face + sink, marquee);
                var seat = (body * 0.9f) with { A = 1 };
                foreach (float sx in new[] { -w / 2, w / 2 })
                {
                    B(sx - 0.3f, 0.6f, face - sink, sx + 0.3f, 0.9f, face + 0.3f, body);
                    B(sx - 0.17f, 0.82f, face + 0.22f, sx + 0.17f, 1.12f, face + 0.26f, dark);
                    B(sx - 0.25f, 0, d - 0.75f, sx + 0.25f, 0.4f, d - 0.05f, dark);
                    B(sx - 0.23f, 0.4f - sink, d - 0.7f, sx + 0.23f, 0.5f, d - 0.15f, seat);
                    B(sx - 0.22f, 0.45f, d - 0.2f, sx + 0.22f, 1.1f, d - 0.08f, seat);
                    B(sx - 0.15f, 0.03f - sink, face + 0.45f, sx + 0.15f, 0.12f, face + 0.6f, Chrome);
                }
                break;
            }
            case FurnitureType.SimPod:
            {
                // a capsule on its plinth, open at the front: the screen inside, the seat and the yoke, a
                // lit band round its mouth
                const float floor = 0.3f;
                B(-w + 0.03f, 0, -d + 0.03f, w - 0.03f, floor, d - 0.03f, dark);
                B(-w, floor - sink, -d, w, H, -d + 0.12f, body);
                foreach (float sx in new[] { -1f, 1f })
                {
                    float x0 = sx < 0 ? -w + 0.015f : w - 0.1f;
                    B(x0, floor - sink, -d + 0.108f, x0 + 0.085f, H - 0.108f, d - 0.27f, body);
                }
                B(-w, H - 0.12f, -d + 0.108f, w, H - 0.02f, d - 0.25f, body);
                B(-w + 0.03f, H - 0.1f, d - 0.25f - sink, w - 0.03f, H - 0.04f, d - 0.25f + sink, Lit(0.20f, 0.90f, 0.98f));
                B(-w + 0.15f, 0.9f, -d + 0.12f - sink, w - 0.15f, 1.75f, -d + 0.12f + sink, screen);
                var seat = (trim * 0.6f) with { A = 1 };
                B(-0.25f, floor - sink, 0f, 0.25f, 0.75f, 0.55f, seat);
                B(-0.23f, 0.75f - sink, 0.4f, 0.23f, 1.3f, 0.53f, seat);
                B(-0.03f, floor - sink, -0.34f, 0.03f, 0.96f, -0.31f, dark);
                B(-0.2f, 0.95f, -0.35f, 0.2f, 1.05f, -0.3f, dark);
                B(-w + 0.1f, 0.12f, d - 0.03f - sink, w - 0.1f, 0.2f, d - 0.03f + sink, Lit(0.98f, 0.70f, 0.10f));
                break;
            }
            case FurnitureType.DanceMachine:
            {
                // the screen cabinet with a speaker each side, the platform in front with two players'
                // lit arrows, the bar behind them
                float face = -d + 0.7f;
                B(-0.45f, 0, -d, 0.45f, H, face, body);
                B(-0.38f, 1.1f, face - sink, 0.38f, 1.7f, face + sink, screen);
                B(-0.43f, 1.85f, face - sink, 0.43f, H - 0.05f, face + sink, marquee);
                foreach (float sx in new[] { -1f, 1f })
                {
                    float x0 = sx < 0 ? -w : 0.47f, x1 = sx < 0 ? -0.47f : w;
                    B(x0, 0, -d, x1, 1.9f, -d + 0.6f, dark);
                    B(x0 + 0.08f, 1.2f, -d + 0.6f - sink, x1 - 0.08f, 1.6f, -d + 0.6f + sink, Lit(0.98f, 0.30f, 0.80f));
                }
                const float deck = 0.15f;
                float pz0 = face + 0.1f;
                B(-w, 0, pz0, w, deck, d, C(0.70f, 0.72f, 0.76f));
                var blue = Lit(0.20f, 0.50f, 0.98f);
                var pink = Lit(0.98f, 0.30f, 0.70f);
                float cz = (pz0 + d - 0.2f) / 2;
                foreach (float px in new[] { -w / 2, w / 2 })
                    foreach (var (ix, iz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                    {
                        float x = px + ix * 0.3f, z = cz + iz * 0.3f;
                        B(x - 0.13f, deck - sink, z - 0.13f, x + 0.13f, deck + sink, z + 0.13f, iz == 0 ? blue : pink);
                    }
                foreach (float sx in new[] { -1f, 1f })
                {
                    float x = sx * (w - 0.05f);
                    B(x - 0.03f, deck - sink, d - 0.11f, x + 0.03f, 1.07f, d - 0.05f, Chrome);
                }
                B(-w + 0.04f, 1.0f, d - 0.1f, w - 0.04f, 1.06f, d - 0.06f, Chrome);
                break;
            }
            case FurnitureType.DrumGame:
            {
                float face = -d + 0.65f;
                B(-w, 0, -d, w, H, face, body);
                B(-w + 0.08f, 1.0f, face - sink, w - 0.08f, 1.5f, face + sink, screen);
                B(-w + 0.04f, 1.6f, face - sink, w - 0.04f, H - 0.05f, face + sink, marquee);
                B(-w + 0.02f, 0, face - sink, w - 0.02f, 0.5f, d - 0.02f, dark);
                foreach (float x in new[] { -0.2f, 0.2f })
                {
                    B(x - 0.18f, 0.5f - sink, d - 0.48f, x + 0.18f, 0.78f, d - 0.12f, C(0.66f, 0.30f, 0.12f));
                    B(x - 0.15f, 0.78f - sink, d - 0.45f, x + 0.15f, 0.78f + sink, d - 0.15f, C(0.95f, 0.90f, 0.80f));
                }
                break;
            }
            case FurnitureType.BasketballGame:
            {
                // the lane up to the backboard tower, its rails and the frame over it, the hoop, a few balls
                const float lane = 0.7f, rail = 0.95f;
                float tower = -d + 0.3f;
                B(-w, 0, -d, w, H, tower, (body * 0.6f) with { A = 1 });
                B(-w + 0.05f, H - 0.35f, tower - sink, w - 0.05f, H - 0.12f, tower + sink, Lit(0.98f, 0.20f, 0.16f));
                B(-w + 0.015f, 0, tower - sink, w - 0.015f, lane, d, body, true);
                B(-w + 0.1f, lane - sink, tower + 0.05f, w - 0.1f, lane + sink, d - 0.1f, C(0.80f, 0.55f, 0.25f));
                var white = C(0.92f, 0.94f, 0.96f);
                B(-0.45f, 1.6f, tower - sink, 0.45f, 2.3f, tower + 0.04f, white);
                B(-0.15f, 1.75f, tower + 0.04f - sink, 0.15f, 1.95f, tower + 0.04f + sink, Lit(0.98f, 0.50f, 0.10f));
                var hoop = C(0.94f, 0.40f, 0.08f);
                B(-0.23f, 1.72f, tower + 0.03f, 0.23f, 1.75f, tower + 0.07f, hoop);
                B(-0.23f, 1.72f, tower + 0.45f, 0.23f, 1.75f, tower + 0.49f, hoop);
                B(-0.23f, 1.72f, tower + 0.06f, -0.2f, 1.75f, tower + 0.46f, hoop);
                B(0.2f, 1.72f, tower + 0.06f, 0.23f, 1.75f, tower + 0.46f, hoop);
                B(-0.17f, 1.45f, tower + 0.1f, 0.17f, 1.71f, tower + 0.42f, white);
                foreach (float sx in new[] { -1f, 1f })
                {
                    float x0 = sx < 0 ? -w + 0.03f : w - 0.09f;
                    B(x0, lane - sink, tower - sink, x0 + 0.06f, rail, d - 0.015f, trim);
                    float px = sx < 0 ? -w + 0.04f : w - 0.08f;
                    B(px, rail - sink, d - 0.08f, px + 0.04f, H - 0.07f, d - 0.03f, Chrome);
                    B(px, H - 0.08f, tower - sink, px + 0.04f, H - 0.04f, d - 0.03f, Chrome);
                }
                for (int i = 0; i < 3; i++)
                {
                    float x = -0.3f + i * 0.3f;
                    B(x - 0.12f, lane - 0.03f, d - 0.42f, x + 0.12f, lane + 0.24f, d - 0.18f, hoop);
                }
                break;
            }
            case FurnitureType.AirHockey:
            {
                // the table, its lit field and centre line, the rails round it, two mallets and the puck
                const float field = 0.78f;
                B(-w, 0, -d, w, field, d, body, true);
                B(-w + 0.08f, field - sink, -d + 0.08f, w - 0.08f, field + sink, d - 0.08f, Lit(0.84f, 0.92f, 0.98f));
                B(-w + 0.095f, field, -0.02f, w - 0.095f, field + 2 * sink, 0.02f, Lit(0.98f, 0.20f, 0.30f));
                var railCol = C(0.14f, 0.14f, 0.16f);
                foreach (float sz in new[] { -1f, 1f })
                {
                    float z0 = sz < 0 ? -d + 0.015f : d - 0.075f;
                    B(-w + 0.015f, field - sink, z0, w - 0.015f, H, z0 + 0.06f, railCol);
                }
                foreach (float sx in new[] { -1f, 1f })
                {
                    float x0 = sx < 0 ? -w + 0.03f : w - 0.075f;
                    B(x0, field - sink, -d + 0.06f, x0 + 0.045f, H, d - 0.06f, railCol);
                }
                B(-0.06f, field, d - 0.35f, 0.06f, 0.86f, d - 0.23f, C(0.85f, 0.10f, 0.12f));
                B(-0.06f, field, -d + 0.23f, 0.06f, 0.86f, -d + 0.35f, C(0.10f, 0.30f, 0.85f));
                B(-0.04f, field, -0.25f, 0.04f, 0.81f, -0.17f, dark);
                break;
            }
        }
    }
}
