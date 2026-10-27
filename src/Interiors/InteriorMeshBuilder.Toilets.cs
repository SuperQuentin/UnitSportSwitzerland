using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// Public toilets' pieces (#1285): the counter of basins, the hand dryer, the screen between two urinals,
/// an accessible WC's grab rails and the pictogram signs over the doorways. Authored back to −Z like every
/// piece; the bowl, the urinal, the basin, the paper and the mirror are the house's and the site cabin's.
/// </summary>
public static partial class InteriorMeshBuilder
{
    private static bool IsToiletPiece(FurnitureType t) => t is FurnitureType.SinkRow or FurnitureType.HandDryer
        or FurnitureType.UrinalScreen or FurnitureType.GrabRail or FurnitureType.WcSignWomen or FurnitureType.WcSignMen
        or FurnitureType.WcSignAll or FurnitureType.WcSignAccessible;

    private static void ToiletPiece(Scratch s, FurniturePlan p, Vector3 at, Basis basis)
    {
        float w = p.W / 2, d = p.D / 2, H = p.H;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col) =>
            s.Box(at + new Vector3(xa, FloorStart(ya), za), at + new Vector3(xb, yb, zb), col, false, basis, at);
        var porcelain = C(0.95f, 0.95f, 0.94f);
        var steel = C(0.78f, 0.80f, 0.82f);
        const float sink = 0.012f;
        switch (p.Type)
        {
            case FurnitureType.SinkRow:
            {
                // a wall-hung stone counter, its upstand reaching back to the wall over the 4 cm gap the mirrors take,
                // a bowl sunk in it every 0.65 m and a tap behind each
                var stone = C(0.40f, 0.42f, 0.44f);
                B(-w, H - 0.1f, -d, w, H, d, stone);
                B(-w, H - 0.012f, -d - 0.04f, w, H + 0.1f, -d + 0.012f, stone * 0.9f);
                B(-w + 0.04f, H - 0.32f, -d + 0.05f, w - 0.04f, H - 0.1f + sink, -d + 0.1f, stone * 0.8f);
                int n = Math.Max(1, (int)MathF.Round(p.W / ToiletSizing.BasinPitch));
                float pitch = p.W / n;
                for (int i = 0; i < n; i++)
                {
                    float cx = -w + pitch * (i + 0.5f);
                    B(cx - 0.2f, H - 0.16f, -d + 0.1f, cx + 0.2f, H + 0.012f, d - 0.06f, porcelain);
                    B(cx - 0.16f, H - 0.012f, -d + 0.14f, cx + 0.16f, H + 0.024f, d - 0.1f, C(0.80f, 0.84f, 0.86f));
                    B(cx - 0.02f, H, -d + 0.02f, cx + 0.02f, H + 0.2f, -d + 0.06f, steel);
                    B(cx - 0.02f, H + 0.16f, -d + 0.06f, cx + 0.02f, H + 0.2f, -d + 0.16f, steel);
                }
                break;
            }
            case FurnitureType.HandDryer:
            {
                // a white box on the wall, its dark nozzle underneath at the front
                B(-w, 0.06f, -d, w, H, d, porcelain);
                B(-w * 0.5f, 0f, d * 0.1f, w * 0.5f, 0.06f + sink, d * 0.8f, C(0.18f, 0.18f, 0.20f));
                B(-w * 0.25f, H * 0.55f, d - sink, w * 0.25f, H * 0.75f, d + sink, C(0.30f, 0.50f, 0.80f));
                break;
            }
            case FurnitureType.UrinalScreen:
            {
                // a frosted panel 0.3-1.5 m up, out from the wall between two urinals, on a steel bracket
                B(-w, 0.3f, -d, w, H, d, C(0.80f, 0.84f, 0.86f));
                B(-w - sink, 0.3f, -d, w + sink, 0.36f, -d + 0.06f, steel);
                break;
            }
            case FurnitureType.GrabRail:
            {
                // a steel bar at 0.75-0.8 m along the piece's longer side, on a plate at its wall end
                bool outward = p.D >= p.W;
                if (outward)
                {
                    B(-0.02f, H - 0.05f, -d, 0.02f, H, d, steel);
                    B(-0.05f, H - 0.12f, -d, 0.05f, H + 0.06f, -d + 0.02f, steel * 0.9f);
                }
                else
                {
                    B(-w, H - 0.05f, -d + 0.02f, w, H, d, steel);
                    foreach (float x in new[] { -w, w - 0.06f })
                        B(x, H - 0.08f, -d, x + 0.06f, H + 0.03f, -d + 0.02f, steel * 0.9f);
                }
                break;
            }
            default:
                Sign(B, p.Type, w, d, H);
                break;
        }
    }

    /// <summary>A toilet sign: a blue plate, the white figures standing on it (a skirt for the women, a wheelchair for the accessible WC).</summary>
    private static void Sign(Action<float, float, float, float, float, float, Color> B, FurnitureType type, float w, float d, float H)
    {
        const float sink = 0.012f;
        B(-w, 0, -d, w, H, d, C(0.14f, 0.30f, 0.56f));
        var fig = C(0.97f, 0.97f, 0.96f);
        float s = H / 0.3f;
        void Part(float x0, float y0, float x1, float y1) => B(x0 * s, y0 * s, d - sink, x1 * s, y1 * s, d + sink, fig);
        void Man(float ox)
        {
            Part(ox - 0.022f, 0.21f, ox + 0.022f, 0.255f);
            Part(ox - 0.04f, 0.12f, ox + 0.04f, 0.2f);
            Part(ox - 0.035f, 0.04f, ox - 0.006f, 0.12f);
            Part(ox + 0.006f, 0.04f, ox + 0.035f, 0.12f);
        }
        void Woman(float ox)
        {
            Part(ox - 0.022f, 0.21f, ox + 0.022f, 0.255f);
            Part(ox - 0.03f, 0.16f, ox + 0.03f, 0.2f);
            Part(ox - 0.055f, 0.1f, ox + 0.055f, 0.16f);
            Part(ox - 0.03f, 0.04f, ox - 0.008f, 0.1f);
            Part(ox + 0.008f, 0.04f, ox + 0.03f, 0.1f);
        }
        switch (type)
        {
            case FurnitureType.WcSignWomen: Woman(0); break;
            case FurnitureType.WcSignMen: Man(0); break;
            case FurnitureType.WcSignAll: Woman(-0.065f); Man(0.065f); break;
            default:
            {
                // the wheelchair user: head, back leaning, the seat, the legs forward, the big wheel as a square ring
                Part(-0.03f, 0.215f, 0.01f, 0.255f);
                Part(-0.035f, 0.12f, -0.005f, 0.205f);
                Part(-0.035f, 0.11f, 0.05f, 0.135f);
                Part(0.03f, 0.05f, 0.055f, 0.135f);
                Part(-0.09f, 0.03f, 0.02f, 0.045f);
                Part(-0.09f, 0.115f, -0.04f, 0.13f);
                Part(-0.09f, 0.03f, -0.075f, 0.13f);
                Part(0.005f, 0.03f, 0.02f, 0.09f);
                break;
            }
        }
    }
}
