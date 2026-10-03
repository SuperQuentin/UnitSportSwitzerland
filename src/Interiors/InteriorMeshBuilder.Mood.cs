using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// What a house's mood looks like (#434, <see cref="InteriorMood"/>): the lamp every room hangs
/// (where <see cref="RoomLights"/> puts its light), a fancy house's cornices, wall colours,
/// paintings, fireplace and chandeliers, and a messy or abandoned house's clutter, cobwebs and dust.
/// Everything is a pure function of the plan, so every peer draws the same house.
/// </summary>
public static partial class InteriorMeshBuilder
{
    private static readonly Color Brass = C(0.80f, 0.64f, 0.30f);
    private static readonly Color Crystal = C(0.90f, 0.94f, 1.00f);
    private static readonly Color Bulb = C(1.00f, 0.96f, 0.78f);
    private static readonly Color Cord = C(0.10f, 0.10f, 0.10f);
    private static readonly Color Web = C(0.82f, 0.82f, 0.80f);

    /// <summary>A colour scaled, alpha kept at 1 (a <see cref="Color"/> times a float scales alpha too).</summary>
    private static Color K(Color c, float k) => new(c.R * k, c.G * k, c.B * k, 1f);

    /// <summary>A stable 0..1 from a position: the colour of a pile, the picture in a frame.</summary>
    private static float Hash01(float x, float z, int salt = 0) =>
        (float)(Core.Fnv.Unit(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:F2}|{z:F2}|{salt}")));

    /// <summary>Rooms a fancy house dresses up: cornices, deep wall colours, chandeliers.</summary>
    private static bool Grand(InteriorLayout l, RoomType t) => l.Mood == InteriorMood.Fancy
        && t is RoomType.Living or RoomType.Dining or RoomType.Hall or RoomType.Study or RoomType.Bedroom
            or RoomType.GuestRoom or RoomType.Landing or RoomType.Lobby;

    /// <summary>A fancy house's grand rooms in one of four wall colours (the house's), the ceiling white.</summary>
    private static (Color Floor, Color Wall, Color Ceiling) MoodPalette(InteriorLayout l, RoomType t, (Color Floor, Color Wall, Color Ceiling) p)
    {
        if (!Grand(l, t) || t is RoomType.Hall or RoomType.Landing or RoomType.Lobby) return p;
        var walls = new[] { C(0.93f, 0.88f, 0.74f), C(0.62f, 0.72f, 0.60f), C(0.56f, 0.24f, 0.26f), C(0.30f, 0.38f, 0.52f) };
        return (p.Floor, walls[(int)(Core.Fnv.Unit(l.Key + "|walls") * walls.Length) % walls.Length], C(0.97f, 0.96f, 0.93f));
    }

    /// <summary>A moulding round the top of a grand room's walls.</summary>
    private static void Cornice(Scratch s, RectPlan inner, float top, Color ceiling)
    {
        var col = K(ceiling, 0.97f);
        const float t = 0.07f, h = 0.13f;
        s.Box(new Vector3(inner.X0, top - h, inner.Z0), new Vector3(inner.X1, top, inner.Z0 + t), col, false);
        s.Box(new Vector3(inner.X0, top - h, inner.Z1 - t), new Vector3(inner.X1, top, inner.Z1), col, false);
        s.Box(new Vector3(inner.X0, top - h, inner.Z0), new Vector3(inner.X0 + t, top, inner.Z1), col, false);
        s.Box(new Vector3(inner.X1 - t, top - h, inner.Z0), new Vector3(inner.X1, top, inner.Z1), col, false);
    }

    /// <summary>
    /// An abandoned house is grey with dust and a messy one a little dull: every colour drawn so
    /// far, greyed and darkened (glass, alpha under 0.5, stays glass).
    /// </summary>
    private static void Weather(Scratch s, InteriorMood mood)
    {
        if (mood is not (InteriorMood.Abandoned or InteriorMood.Messy)) return;
        float grey = mood == InteriorMood.Abandoned ? 0.5f : 0.12f, dark = mood == InteriorMood.Abandoned ? 0.7f : 0.93f;
        var dust = C(0.55f, 0.50f, 0.42f);
        for (int i = 0; i < s.C.Count; i++)
        {
            var c = s.C[i];
            if (c.A < 0.5f) continue;
            float lum = c.R * 0.3f + c.G * 0.59f + c.B * 0.11f;
            var g = new Color(lum, lum, lum).Lerp(dust * lum * 2f, 0.3f);
            var w = c.Lerp(g, grey) * dark;
            w.A = c.A;
            s.C[i] = w;
        }
    }

    /// <summary>
    /// The room's ceiling lamp, drawn where its light is (<see cref="RoomLights.LampAt"/>): a shade
    /// on a cord, a chandelier in a fancy house's grand rooms, a bare bulb in a messy one, only
    /// the cord in an abandoned one, a panel in an office or a shop, an enamel shade in a workshop.
    /// </summary>
    private static void Fixture(Scratch s, InteriorLayout l, RoomPlan r, float y0)
    {
        var kind = RoomLights.LampOf(l, r);
        if (kind == RoomLights.Lamp.Hidden) return;
        float top = y0 + l.ClearOf(r);
        var c = RoomLights.LampAt(l, r, y0);
        void B(float x0, float ya, float z0, float x1, float yb, float z1, Color col) =>
            s.Box(new Vector3(c.X + x0, ya, c.Z + z0), new Vector3(c.X + x1, yb, c.Z + z1), col, false);
        void Rod(float ya, float yb, Color col, float t = 0.008f) => B(-t, ya, -t, t, yb, t, col);
        switch (kind)
        {
            case RoomLights.Lamp.Panel:
            {
                float pw = Mathf.Min(0.6f, r.Width * 0.3f), pd = Mathf.Min(1.2f, r.Depth * 0.4f);
                B(-pw / 2, top - 0.04f, -pd / 2, pw / 2, top, pd / 2, C(0.85f, 0.86f, 0.88f));
                B(-pw / 2 + 0.03f, top - 0.045f, -pd / 2 + 0.03f, pw / 2 - 0.03f, top - 0.04f, pd / 2 - 0.03f, Bulb);
                break;
            }
            case RoomLights.Lamp.Industrial:
                Rod(c.Y + 0.12f, top, Cord);
                B(-0.24f, c.Y, -0.24f, 0.24f, c.Y + 0.05f, 0.24f, C(0.22f, 0.34f, 0.26f));
                B(-0.12f, c.Y + 0.05f, -0.12f, 0.12f, c.Y + 0.14f, 0.12f, C(0.22f, 0.34f, 0.26f));
                B(-0.04f, c.Y - 0.04f, -0.04f, 0.04f, c.Y + 0.01f, 0.04f, Bulb);
                break;
            case RoomLights.Lamp.Bulb:
                Rod(c.Y + 0.06f, top, Cord);
                B(-0.02f, c.Y + 0.04f, -0.02f, 0.02f, c.Y + 0.08f, 0.02f, Cord);
                B(-0.035f, c.Y - 0.04f, -0.035f, 0.035f, c.Y + 0.04f, 0.035f, Bulb);
                break;
            case RoomLights.Lamp.Wire:
                // the bulb long gone, the cord frayed and kinked
                Rod(c.Y + 0.1f, top, Cord);
                B(-0.02f, c.Y + 0.04f, -0.02f, 0.02f, c.Y + 0.1f, 0.02f, C(0.24f, 0.22f, 0.2f));
                B(0.0f, c.Y - 0.05f, -0.004f, 0.006f, c.Y + 0.04f, 0.004f, C(0.55f, 0.35f, 0.2f));
                break;
            case RoomLights.Lamp.Chandelier:
                Chandelier(s, c, top, r.Span > 1);
                break;
            default:
            {
                // a cloth shade in a colour of the room's own
                Rod(c.Y + 0.18f, top, Cord);
                float hue = Hash01(r.X0, r.Z0, 7);
                var shade = Color.FromHsv(hue, 0.35f, 0.85f).SrgbToLinear();
                B(-0.2f, c.Y, -0.2f, 0.2f, c.Y + 0.05f, 0.2f, shade);
                B(-0.16f, c.Y + 0.05f, -0.16f, 0.16f, c.Y + 0.14f, 0.16f, shade);
                B(-0.1f, c.Y + 0.14f, -0.1f, 0.1f, c.Y + 0.19f, 0.1f, shade);
                B(-0.035f, c.Y - 0.03f, -0.035f, 0.035f, c.Y + 0.03f, 0.035f, Bulb);
                break;
            }
        }
    }

    /// <summary>A brass chandelier, its centre at <paramref name="c"/>: arms with candle bulbs and crystal drops, two tiers in a double-height room.</summary>
    private static void Chandelier(Scratch s, Vector3 c, float top, bool tall)
    {
        void Box(Vector3 min, Vector3 max, Color col, float angle = 0f) =>
            s.Box(c + min, c + max, col, false, new Basis(Vector3.Up, angle), c);
        // the chain and the stem
        Box(new Vector3(-0.012f, 0.15f, -0.012f), new Vector3(0.012f, top - c.Y, 0.012f), Brass);
        Box(new Vector3(-0.05f, -0.2f, -0.05f), new Vector3(0.05f, 0.18f, 0.05f), Brass);
        Box(new Vector3(-0.09f, -0.26f, -0.09f), new Vector3(0.09f, -0.18f, 0.09f), Crystal);
        int tiers = tall ? 2 : 1;
        for (int tier = 0; tier < tiers; tier++)
        {
            float radius = tier == 0 ? (tall ? 0.55f : 0.36f) : 0.32f, y = tier == 0 ? 0f : 0.28f;
            int arms = tier == 0 ? (tall ? 8 : 6) : 5;
            for (int a = 0; a < arms; a++)
            {
                float angle = a * Mathf.Tau / arms + tier * 0.3f;
                Box(new Vector3(0, y - 0.015f, -0.012f), new Vector3(radius, y + 0.015f, 0.012f), Brass, angle);
                // the candle cup, a candle and its flame-bulb; a drop of crystal under the cup
                Box(new Vector3(radius - 0.04f, y, -0.04f), new Vector3(radius + 0.04f, y + 0.03f, 0.04f), Brass, angle);
                Box(new Vector3(radius - 0.015f, y + 0.03f, -0.015f), new Vector3(radius + 0.015f, y + 0.12f, 0.015f), C(0.95f, 0.93f, 0.86f), angle);
                Box(new Vector3(radius - 0.02f, y + 0.12f, -0.02f), new Vector3(radius + 0.02f, y + 0.17f, 0.02f), Bulb, angle);
                Box(new Vector3(radius - 0.012f, y - 0.12f, -0.012f), new Vector3(radius + 0.012f, y - 0.02f, 0.012f), Crystal, angle);
            }
        }
    }

    /// <summary>The pieces a mood adds (#434). False for anything else, which the main switch draws.</summary>
    private static bool MoodPiece(Scratch s, FurniturePlan p, Vector3 at, Basis basis)
    {
        if (p.Type is not (FurnitureType.Painting or FurnitureType.Fireplace or FurnitureType.Mirror) && p.Type < FurnitureType.Cobweb)
            return false;
        float w = p.W / 2, d = p.D / 2, H = p.H;
        void B(float xa, float ya, float za, float xb, float yb, float zb, Color col) =>
            s.Box(at + new Vector3(xa, ya, za), at + new Vector3(xb, yb, zb), col, false, basis, at);
        Vector3 P(float x, float y, float z) => at + basis * new Vector3(x, y, z);
        // a thin sheet seen from either side (a web, a sheet of paper)
        void Sheet(Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            s.Tri(a, b, c, col, false);
            s.Tri(a, c, b, col, false);
        }
        float h0 = Hash01(p.X, p.Z), h1 = Hash01(p.X, p.Z, 1);
        switch (p.Type)
        {
            case FurnitureType.Painting:
            {
                // drawn high on the wall, in a gilt or dark frame; the canvas a landscape in a few bands
                float y0 = H - 0.95f, y1 = H - 0.2f;
                var frame = h0 < 0.5f ? Brass : C(0.22f, 0.14f, 0.08f);
                B(-w, y0, -d, w, y1, d, frame);
                float inner = w - 0.06f, bottom = y0 + 0.06f, height = y1 - y0 - 0.12f;
                var sky = Color.FromHsv(0.55f + h1 * 0.1f, 0.35f, 0.85f).SrgbToLinear();
                var hills = Color.FromHsv(0.22f + h0 * 0.15f, 0.5f, 0.55f).SrgbToLinear();
                var ground = Color.FromHsv(0.08f + h1 * 0.06f, 0.55f, 0.45f).SrgbToLinear();
                B(-inner, bottom + height * 0.55f, d, inner, bottom + height, d + 0.005f, sky);
                B(-inner, bottom + height * 0.25f, d, inner, bottom + height * 0.55f, d + 0.005f, hills);
                B(-inner, bottom, d, inner, bottom + height * 0.25f, d + 0.005f, ground);
                break;
            }
            case FurnitureType.Mirror:
            {
                float y0 = H - 1.05f, y1 = H - 0.15f;
                B(-w, y0, -d, w, y1, d, Brass);
                // a mirror shows the sky of the room's light: pale, cool glass
                B(-w + 0.05f, y0 + 0.05f, d, w - 0.05f, y1 - 0.05f, d + 0.005f, C(0.78f, 0.84f, 0.88f));
                break;
            }
            case FurnitureType.Fireplace:
            {
                var stone = C(0.86f, 0.84f, 0.80f);
                B(-w, 0, -d, w, H - 0.1f, d - 0.05f, stone);
                B(-w - 0.05f, H - 0.12f, -d, w + 0.05f, H, d + 0.05f, K(stone, 0.95f));   // the mantel
                B(-w * 0.55f, 0.08f, d - 0.06f, w * 0.55f, H * 0.62f, d - 0.04f, C(0.06f, 0.05f, 0.05f));   // the hearth's mouth
                B(-w * 0.35f, 0.08f, d - 0.25f, w * 0.35f, 0.18f, d - 0.07f, C(0.36f, 0.22f, 0.12f));   // logs
                B(-w * 0.2f, 0.18f, d - 0.2f, w * 0.2f, 0.3f, d - 0.1f, C(1.0f, 0.55f, 0.18f));        // embers
                B(-0.12f, H, -d + 0.05f, -0.04f, H + 0.22f, -d + 0.15f, Brass);                         // candlesticks
                B(0.04f, H, -d + 0.05f, 0.12f, H + 0.22f, -d + 0.15f, Brass);
                break;
            }
            case FurnitureType.Cobweb:
            {
                // fanned from the corner (walls at -Z and -X of the piece) toward the room, sagging
                var corner = P(-w, H, -d);
                var along = P(w, H, -d + 0.005f);
                var across = P(-w + 0.005f, H, d);
                var sag = P(-w + w * 0.7f, H * 0.35f, -d + d * 0.7f);
                var mid1 = P(-w + w * 1.3f, H * 0.6f, -d + 0.005f);
                var mid2 = P(-w + 0.005f, H * 0.6f, -d + d * 1.3f);
                var col = Web;
                Sheet(corner, along, sag, K(col, 0.9f));
                Sheet(corner, sag, across, col);
                Sheet(along, mid1, sag, K(col, 0.8f));
                Sheet(across, sag, mid2, K(col, 0.85f));
                break;
            }
            case FurnitureType.Trash:
            {
                // a burst bag and a few cans and wrappers
                var bag = h0 < 0.5f ? C(0.12f, 0.12f, 0.14f) : C(0.80f, 0.78f, 0.40f);
                B(-w * 0.6f, 0, -d * 0.6f, w * 0.3f, H, d * 0.4f, bag);
                B(w * 0.35f, 0, -d * 0.2f, w * 0.55f, 0.11f, 0, C(0.70f, 0.12f, 0.12f));
                B(-w * 0.1f, 0, d * 0.5f, w * 0.15f, 0.02f, d * 0.95f, C(0.88f, 0.88f, 0.84f));
                B(w * 0.5f, 0, d * 0.5f, w * 0.9f, 0.03f, d * 0.8f, C(0.30f, 0.50f, 0.75f));
                break;
            }
            case FurnitureType.ClothesPile:
            {
                // a heap of three or four garments in colours of its own
                for (int i = 0; i < 4; i++)
                {
                    float hi = Hash01(p.X, p.Z, 10 + i);
                    var col = Color.FromHsv(hi, 0.45f + 0.3f * Hash01(p.X, p.Z, 20 + i), 0.35f + 0.5f * Hash01(p.X, p.Z, 30 + i)).SrgbToLinear();
                    float x0 = -w + (i % 2) * w * 0.4f, z0 = -d + (i / 2) * d * 0.5f;
                    B(x0, i * 0.035f, z0, x0 + w * 1.5f, i * 0.035f + 0.05f, z0 + d * 1.4f, col);
                }
                break;
            }
            case FurnitureType.Papers:
                for (int i = 0; i < 5; i++)
                {
                    float x = (Hash01(p.X, p.Z, 40 + i) - 0.5f) * w * 1.4f, z = (Hash01(p.X, p.Z, 50 + i) - 0.5f) * d * 1.4f;
                    float y = 0.003f + i * 0.002f;
                    Sheet(P(x - 0.11f, y, z - 0.15f), P(x + 0.11f, y, z - 0.15f), P(x + 0.11f, y, z + 0.15f), C(0.92f, 0.91f, 0.86f));
                    Sheet(P(x - 0.11f, y, z - 0.15f), P(x + 0.11f, y, z + 0.15f), P(x - 0.11f, y, z + 0.15f), C(0.90f, 0.89f, 0.84f));
                }
                break;
            case FurnitureType.DirtPatch:
            {
                // two overlapping dark stains flat on the floor
                var stain = C(0.22f, 0.19f, 0.15f);
                B(-w, 0.002f, -d * 0.6f, w * 0.6f, 0.004f, d, stain);
                B(-w * 0.4f, 0.003f, -d, w, 0.005f, d * 0.3f, K(stain, 1.15f));
                break;
            }
            case FurnitureType.Bottles:
                for (int i = 0; i < 3; i++)
                {
                    var glass = i == 1 ? C(0.20f, 0.42f, 0.22f) : C(0.42f, 0.28f, 0.14f);
                    if (i == 2)   // one fallen over
                        B(-w, 0, d * 0.2f, -w + 0.28f, 0.07f, d * 0.2f + 0.07f, glass);
                    else
                    {
                        float x = -w + 0.05f + i * 0.14f;
                        B(x, 0, -d + 0.05f, x + 0.07f, 0.2f, -d + 0.12f, glass);
                        B(x + 0.02f, 0.2f, -d + 0.07f, x + 0.05f, 0.29f, -d + 0.1f, glass);
                    }
                }
                break;
            case FurnitureType.FallenChair:
            {
                // a chair on its back: the seat upright, the back flat on the floor, the legs in the air
                var wood = C(0.52f, 0.36f, 0.22f);
                B(-w, 0, -d, w, 0.05f, 0, wood);                    // the back, on the floor
                B(-w, 0, 0, w, 0.42f, 0.04f, wood);                 // the seat, standing up
                foreach (float lx in new[] { -w + 0.02f, w - 0.06f })
                {
                    B(lx, 0.02f, 0.04f, lx + 0.04f, 0.06f, d, wood); // the legs, pointing out
                    B(lx, 0.38f, 0.04f, lx + 0.04f, 0.42f, d, wood);
                }
                break;
            }
            case FurnitureType.Plank:
                B(-w, 0, -d, w, H, d, C(0.48f, 0.38f, 0.26f));
                break;
        }
        return true;
    }
}
