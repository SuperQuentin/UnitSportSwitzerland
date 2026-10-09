using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>What joins a garage door to the road in front of it (#558).</summary>
public enum LinkKind : byte
{
    /// <summary>No road to reach: the block gets no garage door.</summary>
    None,
    /// <summary>The road is a few metres off: a continuous pavement with bollards flanking the mouth and a dropped kerb.</summary>
    Sidewalk,
    /// <summary>The road is further, or a big one: a short access road forming a small T with it.</summary>
    Stub,
}

/// <summary>
/// The road link of one garage door, chosen from the road's distance and class, in tile-local
/// metres (X east, Z south). A pure function of the tile's road bytes, so every peer draws the
/// same link. <see cref="Length"/> is how far the link runs out of the door, along the door's
/// outward direction, to the road's near edge; <see cref="Tangent"/> is the road's direction there.
/// No Godot beyond its maths: tier 0 (<c>GarageLinkTests</c>).
/// </summary>
public readonly record struct GarageLink(LinkKind Kind, float Length, float RoadY, Vector2 Tangent)
{
    /// <summary>A road with its near edge this close takes a pavement; further, up to <see cref="StubReach"/>, an access road.</summary>
    public const float SidewalkReach = 8f, StubReach = 25f;

    /// <summary>A big road (10 m / 8 m Strasse) gets an access road even when it is near: a car does not cross a main road's pavement.</summary>
    public const float MajorStubFrom = 3f;

    /// <summary>The access road's width: the door and a half metre of margin each side.</summary>
    public const float StubWidth = 4.0f;

    /// <summary>How the road is read: its class, its full width (m) and its centreline as xyz triples.</summary>
    public readonly record struct Road(RoadClass Class, float Width, float[] Points);

    /// <summary>The roads a garage link can reach and so may join: not paths or tracks, not rail, not motorways.</summary>
    private static bool Street(RoadClass c) => c is >= RoadClass.Major and <= RoadClass.Lane or RoadClass.Square;

    private static bool Motorway(RoadClass c) => c is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp;

    /// <summary>
    /// How much an access road is raised over the middle of its straight line from door to road,
    /// metres, where the ground between them rises above that line (set by the footprint, which has
    /// the ground; 0 otherwise). A sine over the run, so it meets the door and the road level.
    /// </summary>
    public float Hump { get; init; }

    /// <summary>
    /// The most an access road is humped over the ground, m. Where the ground between the door and the
    /// road rises past what this lifts it over, the door is not cut (<see cref="Humpable"/>): a stub
    /// buried to the knee on a bank is worse than no garage there.
    /// </summary>
    public const float MaxHump = 0.5f;

    /// <summary>Whether a hump of <see cref="MaxHump"/> clears the ground sampled along the link.</summary>
    public static bool Humpable(IEnumerable<(float At, float Above)> samples) => HumpFor(samples, float.MaxValue) <= MaxHump;

    /// <summary>The hump needed for a link of <paramref name="length"/> over ground heights <paramref name="above"/> the line, sampled at <paramref name="at"/> (0..1 along it).</summary>
    public static float HumpFor(IEnumerable<(float At, float Above)> samples, float max = MaxHump)
    {
        float hump = 0;
        foreach (var (at, above) in samples)
        {
            float w = MathF.Sin(MathF.PI * Math.Clamp(at, 0f, 1f));
            if (w > 0.2f && above > 0) hump = Math.Max(hump, above / w);
        }
        return Math.Min(hump, max);
    }

    public bool Any => Kind != LinkKind.None;

    /// <summary>
    /// The link for a door at <paramref name="door"/> facing <paramref name="outward"/> (unit, horizontal),
    /// or <see cref="LinkKind.None"/> when the door should not be cut: no street within
    /// <see cref="StubReach"/> of its near edge in front of the door, a motorway-class road as near,
    /// or a road that runs so nearly along the door's direction that no link could meet it.
    /// </summary>
    public static GarageLink Choose(Vector2 door, Vector2 outward, IEnumerable<Road> roads)
    {
        float bestStreet = float.MaxValue, bestBlock = float.MaxValue;
        Road street = default;
        Vector2 streetFoot = default, streetTan = default;
        float streetY = 0;
        foreach (var r in roads)
        {
            bool isStreet = Street(r.Class), isBlock = Motorway(r.Class);
            if (!isStreet && !isBlock) continue;
            float half = (r.Width > 0 ? r.Width : RoadFormat.DefaultWidth(r.Class)) / 2;
            int n = r.Points.Length / 3;
            for (int i = 0; i + 1 < n; i++)
            {
                var a = new Vector2(r.Points[i * 3], r.Points[i * 3 + 2]);
                var b = new Vector2(r.Points[i * 3 + 3], r.Points[i * 3 + 5]);
                var ab = b - a;
                float len2 = ab.LengthSquared();
                float t = len2 < 1e-6f ? 0f : Mathf.Clamp((door - a).Dot(ab) / len2, 0f, 1f);
                var foot = a + ab * t;
                float edge = foot.DistanceTo(door) - half;
                if (isBlock) { bestBlock = Math.Min(bestBlock, edge); continue; }
                if (edge >= bestStreet) continue;
                bestStreet = edge;
                street = r;
                streetFoot = foot;
                streetTan = len2 < 1e-6f ? Vector2.Right : ab / MathF.Sqrt(len2);
                streetY = Mathf.Lerp(r.Points[i * 3 + 1], r.Points[i * 3 + 4], t);
            }
        }
        if (bestStreet > StubReach || bestBlock <= StubReach) return default;

        // the road must lie in front of the door, and be met by a straight link out of it
        var normal = new Vector2(-streetTan.Y, streetTan.X);
        float facing = Math.Abs(outward.Dot(normal));
        var to = streetFoot - door;
        if (facing < 0.5f || to.LengthSquared() > 1e-6f && to.Normalized().Dot(outward) < 0.5f) return default;
        float half2 = (street.Width > 0 ? street.Width : RoadFormat.DefaultWidth(street.Class)) / 2;
        // where the door's own line reaches the road's centreline, and so its near edge
        float s = to.Dot(normal) / outward.Dot(normal) - half2 / facing;
        if (s < 0.3f) s = 0.3f;
        float reach = Math.Max(bestStreet, 0f);
        var kind = reach <= SidewalkReach && !(street.Class == RoadClass.Major && reach >= MajorStubFrom)
            ? LinkKind.Sidewalk : LinkKind.Stub;
        return new GarageLink(kind, s, streetY, streetTan);
    }
}
