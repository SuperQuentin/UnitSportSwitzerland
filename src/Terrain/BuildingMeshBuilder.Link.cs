using Godot;
using UnitSport.Interiors;

namespace UnitSport.Terrain;

/// <summary>
/// What joins a garage door of a block of flats to the road in front of it (#558): a pavement with
/// a dropped kerb and bollards, or a short access road flaring into a small T with the road.
/// Appended to the door's own boxes, so a tile's buildings and all their links stay one surface.
/// </summary>
public static partial class BuildingMeshBuilder
{
    /// <summary>Where the link starts out of the facade (past the doorstep), and how far an access road lies over the ground's line.</summary>
    private const float LinkStart = 0.45f, LinkLift = 0.03f;

    /// <summary>The flare either side of the access road where it meets the road, and how long it is.</summary>
    private const float StubFlare = 1.4f, StubRound = 1.6f;

    /// <summary>
    /// Where the access road of a garage door lies, in tile-local metres: the strip from the door's
    /// step to the road's near edge, and the trapezoid that flares out to meet the road. Each is four
    /// corners, near end first, left to right; drawn raised <see cref="LinkLift"/> over a straight
    /// line between the ground at the door and the road's height, and collided as the same quads.
    /// </summary>
    public static Vector3[][] StubDeck(DoorSpot d)
    {
        var o = d.Outward;
        var t = new Vector3(-o.Z, 0, o.X);
        float len = d.Link.Length, dy = d.Link.RoadY - d.Position.Y;
        float half = GarageLink.StubWidth / 2;
        Vector3 P(float along, float out_) =>
            d.Position + t * along + o * out_ + Vector3.Up * (LinkHeight(d.Link, dy, out_) + LinkLift);
        float round = Mathf.Min(StubRound, Mathf.Max(0.3f, len - LinkStart - 0.1f));
        // the strip in pieces about a metre and a half long, so the hump over a rise in the ground can bend
        float end = len - round;
        int n = Math.Max(1, (int)Mathf.Ceil((end - LinkStart) / 1.5f));
        var quads = new List<Vector3[]>();
        for (int i = 0; i < n; i++)
        {
            float o0 = Mathf.Lerp(LinkStart, end, i / (float)n), o1 = Mathf.Lerp(LinkStart, end, (i + 1) / (float)n);
            quads.Add([P(-half, o0), P(half, o0), P(half, o1), P(-half, o1)]);
        }
        quads.Add([P(-half, end), P(half, end), P(half + StubFlare, len), P(-half - StubFlare, len)]);
        return quads.ToArray();
    }

    /// <summary>
    /// How high above the door's ground a link is at <paramref name="out_"/> metres out: the straight
    /// line to the road's height, plus <see cref="GarageLink.Hump"/> raised over the middle where the
    /// ground between them rises above that line.
    /// </summary>
    private static float LinkHeight(GarageLink link, float dy, float out_)
    {
        float u = Mathf.Clamp(out_ / link.Length, 0f, 1f);
        return dy * u + link.Hump * Mathf.Sin(Mathf.Pi * u);
    }

    /// <summary>
    /// The link in the door's frame (along the wall, out, up): ground-following slabs between the
    /// door's height and the road's, with a skirt so a slope shows no gap. The roll-up branch of
    /// <c>AppendDoor</c> calls it for a door that has one.
    /// </summary>
    private static void AppendLink(List<Vector3> v, List<Color> c, List<float> f, DoorSpot d)
    {
        var o = d.Outward;
        var t = new Vector3(-o.Z, 0, o.X);
        float len = d.Link.Length, dy = d.Link.RoadY - d.Position.Y, hw = d.Width / 2;
        float Y(float out_) => LinkHeight(d.Link, dy, out_);
        Vector3 P(float along, float out_, float up) => d.Position + t * along + o * out_ + Vector3.Up * (Y(out_) + up);
        void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 dd, Color col)
        {
            // both windings: the building shader culls back faces
            v.Add(a); v.Add(b); v.Add(cc); v.Add(a); v.Add(cc); v.Add(dd);
            v.Add(a); v.Add(cc); v.Add(b); v.Add(a); v.Add(dd); v.Add(cc);
            for (int i = 0; i < 12; i++) { c.Add(col); f.Add(0f); }
        }
        // a slab between two ends (out0..out1) and two edges (a0..a1), its top `up` over the grade
        void Slab(float a0, float a1, float out0, float out1, float up0, float up1, Color col)
        {
            Quad(P(a0, out0, up0), P(a1, out0, up0), P(a1, out1, up1), P(a0, out1, up1), col);
            var side = col * 0.8f;
            Quad(P(a0, out0, -0.4f), P(a0, out1, -0.4f), P(a0, out1, up1), P(a0, out0, up0), side);
            Quad(P(a1, out0, -0.4f), P(a1, out0, up0), P(a1, out1, up1), P(a1, out1, -0.4f), side);
        }
        var pave = new Color(0.66f, 0.65f, 0.62f).SrgbToLinear();
        var concrete = new Color(0.74f, 0.73f, 0.70f).SrgbToLinear();
        var asphalt = new Color(0.27f, 0.27f, 0.28f).SrgbToLinear();
        var kerb = new Color(0.72f, 0.71f, 0.68f).SrgbToLinear();

        if (d.Link.Kind == LinkKind.Sidewalk)
        {
            // the pavement runs on past the mouth either side, kerb high; the mouth's own strip is
            // dropped to the road's level over its last metre and a half: the dropped kerb
            float mouth = hw + 0.25f, wing = hw + 1.4f;
            Slab(-wing, -mouth, LinkStart, len, 0.12f, 0.12f, pave);
            Slab(mouth, wing, LinkStart, len, 0.12f, 0.12f, pave);
            float drop = Mathf.Max(LinkStart + 0.1f, len - 1.5f);
            Slab(-mouth, mouth, LinkStart, drop, 0.12f, 0.12f, concrete);
            Slab(-mouth, mouth, drop, len, 0.12f, 0.02f, concrete);
            // the bollards stand either side of the mouth, at the kerb end where a car crosses
            foreach (float side in new[] { -1f, 1f })
                AppendBollard(v, c, f, P(side * (mouth + 0.4f), Mathf.Max(LinkStart + 0.4f, len - 0.5f), 0.12f));
        }
        else
        {
            foreach (var q in StubDeck(d)) Quad(q[0], q[1], q[2], q[3], asphalt);
            // a kerb stone line either side, so the stub reads as a made road and not a stain
            float half = GarageLink.StubWidth / 2;
            float round = Mathf.Min(StubRound, Mathf.Max(0.3f, len - LinkStart - 0.1f));
            Slab(-half - 0.15f, -half, LinkStart, len - round, 0.1f, 0.1f, kerb);
            Slab(half, half + 0.15f, LinkStart, len - round, 0.1f, 0.1f, kerb);
        }
    }

    /// <summary>A street bollard (#558): a six-sided post with a pale band under the cap, shown in the model viewer with its garage door (<c>PortalDemo</c>).</summary>
    private static void AppendBollard(List<Vector3> v, List<Color> c, List<float> f, Vector3 foot)
    {
        const int sides = 6;
        const float r = 0.08f, h = 0.85f;
        var post = new Color(0.22f, 0.23f, 0.25f).SrgbToLinear();
        var band = new Color(0.90f, 0.90f, 0.88f).SrgbToLinear();
        void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 dd, Color col)
        {
            v.Add(a); v.Add(b); v.Add(cc); v.Add(a); v.Add(cc); v.Add(dd);
            v.Add(a); v.Add(cc); v.Add(b); v.Add(a); v.Add(dd); v.Add(cc);
            for (int i = 0; i < 12; i++) { c.Add(col); f.Add(0f); }
        }
        var top = foot + Vector3.Up * h;
        for (int k = 0; k < sides; k++)
        {
            float a0 = Mathf.Tau * k / sides, a1 = Mathf.Tau * (k + 1) / sides;
            var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * r;
            var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * r;
            Quad(foot + d0, foot + d1, foot + d1 + Vector3.Up * (h - 0.2f), foot + d0 + Vector3.Up * (h - 0.2f), post);
            Quad(foot + d0 + Vector3.Up * (h - 0.2f), foot + d1 + Vector3.Up * (h - 0.2f),
                foot + d1 + Vector3.Up * (h - 0.1f), foot + d0 + Vector3.Up * (h - 0.1f), band);
            Quad(foot + d0 + Vector3.Up * (h - 0.1f), foot + d1 + Vector3.Up * (h - 0.1f), top + d1, top + d0, post);
            Quad(top, top + d0, top + d1, top + d1, post);
        }
    }

    /// <summary>
    /// The collision of the access roads of a tile's garage doors (#558): the same quads
    /// <see cref="StubDeck"/> draws, both ways up, so a vehicle driving up to the door has a road
    /// under it and a one-sided shape cannot swallow it. A pavement link needs none: it is a few
    /// centimetres on the ground, which the terrain already collides.
    /// </summary>
    public static Vector3[] LinkFaces(DoorSpot[]? doors)
    {
        if (doors == null) return [];
        var faces = new List<Vector3>();
        foreach (var d in doors)
        {
            if (d.Link.Kind != LinkKind.Stub) continue;
            foreach (var q in StubDeck(d))
            {
                faces.AddRange([q[0], q[1], q[2], q[0], q[2], q[3]]);
                faces.AddRange([q[0], q[2], q[1], q[0], q[3], q[2]]);
            }
        }
        return faces.ToArray();
    }
}
