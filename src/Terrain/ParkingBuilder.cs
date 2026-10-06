using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// What stands in a car park (#499), from the <c>.road</c> v3 point props the network stage wrote:
/// the ticket barrier (a housing and its boom), the pay machine or entry column, a trolley shelter
/// at a big-box store, and the blue P (4.17) on its pole. Low-poly boxes in the PS1 house style,
/// drawn into the tile's road mesh like the junction signs and the guardrails, so a car park costs
/// one more surface and no extra node.
///
/// <para>
/// Collision is the solid part only — the housing, the column, the shelter's posts and roof, the P's
/// pole. <b>The boom is deliberately not solid</b>: it is a 3 m stick at windscreen height that a
/// car drives through today. Making it stop a car means a moving body and an opening animation,
/// which belongs with whatever eventually makes a barrier open, not here.
/// </para>
/// </summary>
public static class ParkingBuilder
{
    private static readonly Color Housing = new(0.82f, 0.81f, 0.78f);    // painted steel cabinet
    private static readonly Color BoomWhite = new(0.93f, 0.93f, 0.91f);
    private static readonly Color BoomRed = new(0.78f, 0.12f, 0.12f);
    private static readonly Color Machine = new(0.32f, 0.34f, 0.36f);    // dark grey pay machine
    private static readonly Color Screen = new(0.10f, 0.12f, 0.14f);
    private static readonly Color Steel = new(0.62f, 0.63f, 0.64f);      // galvanised posts
    private static readonly Color Roof = new(0.70f, 0.72f, 0.74f);       // translucent-looking canopy
    private static readonly Color SignBlue = new(0.12f, 0.37f, 0.66f);   // SNV 640 877 parking blue
    private static readonly Color SignWhite = new(0.93f, 0.93f, 0.91f);

    /// <summary>A post runs on this far below its stored foot, so it meets the ground whatever the lattice does.</summary>
    private const float Sink = 0.4f;

    /// <summary>The boom: 3 m of arm, 8 cm deep, in 50 cm bands.</summary>
    private const float BoomLength = 3.0f, BoomDepth = 0.08f, BoomBand = 0.5f;

    /// <summary>The barrier's housing: a cabinet beside the lane.</summary>
    private const float HousingWidth = 0.35f, HousingDepth = 0.30f;

    /// <summary>The pay machine or entry column.</summary>
    private const float MachineWidth = 0.40f, MachineDepth = 0.30f;

    /// <summary>The trolley shelter: a bay's width, two bays deep is too much, so one bay and a canopy over it.</summary>
    private const float ShelterWidth = 2.4f, ShelterDepth = 4.6f, ShelterPost = 0.08f, ShelterRoof = 0.12f;

    /// <summary>The P plate, and how far its lower edge stands above the ground.</summary>
    private const float PlateSide = 0.60f, PlateLower = 1.5f, PlateThickness = 0.01f;
    private const float PoleSide = 0.07f;

    public static bool IsParkingProp(RoadPointProp p) => p.Type
        is PointPropType.TicketBarrier or PointPropType.TicketKiosk
        or PointPropType.CartShelter or PointPropType.ParkingSign;

    /// <summary>Appends every car park prop of the tile to a road mesh under construction.</summary>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        var mesh = new Mesh(vertices, colors, uvs, uv2s, indices);
        foreach (var p in tile.PointProps)
        {
            if (!IsParkingProp(p)) continue;
            var (foot, front, right) = Frame(p);
            switch (p.Type)
            {
                case PointPropType.TicketBarrier: Barrier(mesh, p, foot, front, right); break;
                case PointPropType.TicketKiosk: Kiosk(mesh, p, foot, front, right); break;
                case PointPropType.CartShelter: Shelter(mesh, p, foot, front, right); break;
                default: Sign(mesh, p, foot, front, right); break;
            }
        }
    }

    /// <summary>Collision for the solid parts: the housing, the column, the shelter, the P's pole. Never the boom.</summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var p in tile.PointProps)
        {
            if (!IsParkingProp(p) || (p.Flags & PropFlags.Solid) == 0) continue;
            var (foot, front, right) = Frame(p);
            switch (p.Type)
            {
                case PointPropType.TicketBarrier:
                    Box(faces, foot, front, right, HousingDepth, HousingWidth, p.Height, Sink);
                    break;
                case PointPropType.TicketKiosk:
                    Box(faces, foot, front, right, MachineDepth, MachineWidth, p.Height, Sink);
                    break;
                case PointPropType.CartShelter:
                    // the shelter as one block: a player walking into it is stopped by its side, and
                    // four thin posts plus a roof would be four times the faces for no gain
                    Box(faces, foot, front, right, ShelterDepth, ShelterWidth, p.Height, 0f);
                    break;
                default:
                    Box(faces, foot, front, right, PoleSide, PoleSide, p.Height, Sink);
                    break;
            }
        }
        return faces.ToArray();
    }

    // ---- the props ---------------------------------------------------------------------------

    /// <summary>
    /// The housing beside the lane with the boom across it. <c>Variant</c> 1 lifts the boom to
    /// vertical (a barrier standing open), 0 leaves it down across the way in.
    /// </summary>
    private static void Barrier(Mesh m, RoadPointProp p, Vector3 foot, Vector3 front, Vector3 right)
    {
        var housing = Housing.SrgbToLinear();
        AppendBox(m, housing, foot, front, right, HousingDepth, HousingWidth, p.Height, Sink);

        // the boom leaves the housing's top, across the lane (to the driver's left from the housing)
        var hinge = foot + Vector3.Up * (p.Height - BoomDepth) + right * (HousingWidth * 0.5f);
        bool up = p.Variant == 1;
        var along = up ? Vector3.Up : -right;
        var thick = up ? right : Vector3.Up;

        // banded red and white, 50 cm at a time, as every Swiss boom is
        for (float s = 0; s < BoomLength - 1e-3f; s += BoomBand)
        {
            float len = Mathf.Min(BoomBand, BoomLength - s);
            var colour = ((int)(s / BoomBand) % 2 == 0 ? BoomRed : BoomWhite).SrgbToLinear();
            var a = hinge + along * s;
            AppendBoxBetween(m, colour, a, along * len, front * BoomDepth, thick * BoomDepth);
        }
    }

    /// <summary>The pay-and-display machine: a dark column with a screen panel facing the driver.</summary>
    private static void Kiosk(Mesh m, RoadPointProp p, Vector3 foot, Vector3 front, Vector3 right)
    {
        AppendBox(m, Machine.SrgbToLinear(), foot, front, right, MachineDepth, MachineWidth, p.Height, Sink);

        // the screen, just proud of the front face so it does not z-fight with it
        var centre = foot + Vector3.Up * (p.Height - 0.35f) + front * (MachineDepth * 0.5f + 0.005f);
        var screen = Screen.SrgbToLinear();
        m.Quad(screen,
            centre - right * 0.13f - Vector3.Up * 0.10f, centre + right * 0.13f - Vector3.Up * 0.10f,
            centre + right * 0.13f + Vector3.Up * 0.10f, centre - right * 0.13f + Vector3.Up * 0.10f);
    }

    /// <summary>A trolley shelter: four posts and a flat canopy over one bay, open on the aisle side.</summary>
    private static void Shelter(Mesh m, RoadPointProp p, Vector3 foot, Vector3 front, Vector3 right)
    {
        var steel = Steel.SrgbToLinear();
        float h = p.Height - ShelterRoof;
        float hw = ShelterWidth * 0.5f - ShelterPost, hd = ShelterDepth * 0.5f - ShelterPost;

        foreach (float sx in (float[])[-hw, hw])
            foreach (float sz in (float[])[-hd, hd])
                AppendBox(m, steel, foot + right * sx + front * sz, front, right,
                    ShelterPost * 2, ShelterPost * 2, h, Sink);

        // the canopy, a slab over the whole footprint
        var roof = Roof.SrgbToLinear();
        AppendBoxBetween(m, roof,
            foot + Vector3.Up * h - right * (ShelterWidth * 0.5f) - front * (ShelterDepth * 0.5f),
            right * ShelterWidth, front * ShelterDepth, Vector3.Up * ShelterRoof);

        // the back wall, so trolleys read as sheltered rather than as a table
        AppendBoxBetween(m, roof,
            foot + Vector3.Up * 0.9f - right * (ShelterWidth * 0.5f) - front * (ShelterDepth * 0.5f),
            right * ShelterWidth, front * 0.06f, Vector3.Up * (h - 0.9f));
    }

    /// <summary>The blue P (SSV 4.17): a square plate, white letter, on a pole.</summary>
    private static void Sign(Mesh m, RoadPointProp p, Vector3 foot, Vector3 front, Vector3 right)
    {
        AppendBox(m, Steel.SrgbToLinear(), foot, front, right, PoleSide, PoleSide, p.Height, Sink);

        float lower = Mathf.Max(PlateLower, p.Height - PlateSide);
        var centre = foot + Vector3.Up * (lower + PlateSide * 0.5f) + front * (PoleSide * 0.5f + PlateThickness);
        float h = PlateSide * 0.5f;
        var blue = SignBlue.SrgbToLinear();
        m.Quad(blue, centre - right * h - Vector3.Up * h, centre + right * h - Vector3.Up * h,
            centre + right * h + Vector3.Up * h, centre - right * h + Vector3.Up * h);

        // the P: a stem and a bowl, as two bars and a block — at a sign's scale that is the letter
        var white = SignWhite.SrgbToLinear();
        var face = centre + front * 0.004f;
        float s = PlateSide;
        m.Quad(white,
            face - right * (s * 0.18f) - Vector3.Up * (s * 0.30f), face - right * (s * 0.06f) - Vector3.Up * (s * 0.30f),
            face - right * (s * 0.06f) + Vector3.Up * (s * 0.30f), face - right * (s * 0.18f) + Vector3.Up * (s * 0.30f));
        m.Quad(white,
            face - right * (s * 0.06f) + Vector3.Up * (s * 0.06f), face + right * (s * 0.20f) + Vector3.Up * (s * 0.06f),
            face + right * (s * 0.20f) + Vector3.Up * (s * 0.30f), face - right * (s * 0.06f) + Vector3.Up * (s * 0.30f));
    }

    // ---- small mesh helpers ------------------------------------------------------------------

    /// <summary>The four lists a road mesh is built into, so a prop can be drawn without passing five arguments.</summary>
    private sealed class Mesh(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        public void Quad(Color colour, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i0 = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            for (int k = 0; k < 4; k++) { colors.Add(colour); uvs.Add(Vector2.Zero); uv2s.Add(Vector2.Zero); }
            // the road material is cull_disabled, so winding only needs to be consistent
            indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
            indices.Add(i0); indices.Add(i0 + 2); indices.Add(i0 + 3);
        }
    }

    private static (Vector3 Foot, Vector3 Front, Vector3 Right) Frame(RoadPointProp p)
    {
        // heading about +Y; 0 = facing -Z, the same convention as RoadSignBuilder
        var front = new Vector3(-Mathf.Sin(p.Heading), 0f, -Mathf.Cos(p.Heading));
        var right = new Vector3(front.Z, 0f, -front.X);
        return (new Vector3(p.X, p.Y, p.Z), front, right);
    }

    /// <summary>An upright box on <paramref name="foot"/>, <paramref name="depth"/> along front and <paramref name="width"/> across.</summary>
    private static void AppendBox(Mesh m, Color colour, Vector3 foot, Vector3 front, Vector3 right,
        float depth, float width, float height, float sink) =>
        AppendBoxBetween(m, colour,
            foot - front * (depth * 0.5f) - right * (width * 0.5f) + Vector3.Down * sink,
            right * width, front * depth, Vector3.Up * (height + sink));

    /// <summary>A box from one corner and three edge vectors.</summary>
    private static void AppendBoxBetween(Mesh m, Color colour, Vector3 origin, Vector3 u, Vector3 v, Vector3 w)
    {
        var o = origin;
        m.Quad(colour, o, o + u, o + u + v, o + v);                               // bottom
        m.Quad(colour, o + w, o + u + w, o + u + v + w, o + v + w);               // top
        m.Quad(colour, o, o + u, o + u + w, o + w);                               // front
        m.Quad(colour, o + v, o + u + v, o + u + v + w, o + v + w);               // back
        m.Quad(colour, o, o + v, o + v + w, o + w);                               // left
        m.Quad(colour, o + u, o + u + v, o + u + v + w, o + u + w);               // right
    }

    /// <summary>The same box as collision faces.</summary>
    private static void Box(List<Vector3> faces, Vector3 foot, Vector3 front, Vector3 right,
        float depth, float width, float height, float sink)
    {
        var o = foot - front * (depth * 0.5f) - right * (width * 0.5f) + Vector3.Down * sink;
        var u = right * width;
        var v = front * depth;
        var w = Vector3.Up * (height + sink);

        foreach (var (a, b, c, d) in (( Vector3, Vector3, Vector3, Vector3)[])
        [
            (o, o + u, o + u + v, o + v),
            (o + w, o + u + w, o + u + v + w, o + v + w),
            (o, o + u, o + u + w, o + w),
            (o + v, o + u + v, o + u + v + w, o + v + w),
            (o, o + v, o + v + w, o + w),
            (o + u, o + u + v, o + u + v + w, o + u + w),
        ])
        {
            faces.Add(a); faces.Add(b); faces.Add(c);
            faces.Add(a); faces.Add(c); faces.Add(d);
        }
    }
}
