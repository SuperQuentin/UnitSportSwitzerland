namespace UnitSport.Terrain.Format;

/// <summary>What a piece of a street side's profile is drawn as.</summary>
public enum StreetSurface : byte
{
    Kerb = 0,     // a kerb face, vertical or sloped
    Sidewalk = 1,
    Paving = 2,   // flush paving (a square, a tunnel approach's shoulder): no kerb
    Verge = 3,    // grass strip between the carriageway and a bike path (#120)
    Track = 4,    // separated bike path (#120)
    Buffer = 5,   // grass strip between a bike path and the sidewalk (#120)
}

/// <summary>
/// The cross-section of one side of a street (#119 sidewalks, #120 bike paths) outward from the
/// carriageway edge, as a profile of (distance from the edge, height above the road) points; shared
/// by the game (mesh, collision) and the network stage (paint heights), so both agree.
///
/// <para>
/// A sidewalk alone: a vertical kerb at the edge up to <see cref="RoadSide.KerbCm"/>, then its top.
/// With a bike path the bands are verge, path, buffer, sidewalk (<see cref="RoadSide"/>): a
/// <see cref="BikeKind.Track"/> lies at the sidewalk's height, a <see cref="BikeKind.TrackMid"/>
/// halfway down; grass strips take the path's height. Where a kerb touches the path it is a
/// straight sloped face over <see cref="SlopedKerbRun"/> (the user's choice for #120: cyclists
/// cross it), taken from the band outside it; every other kerb is vertical.
/// </para>
/// </summary>
public static class RoadStreetSection
{
    /// <summary>
    /// Plan run of a sloped kerb face beside a bike path: the 30 cm slanted kerb "A" of ASTRA's
    /// Handbuch Veloverkehr in Kreuzungen (2021) Abb. 307 (6 cm over 30 cm, 12°, across or along a
    /// path). A <see cref="BikeKind.TrackMid"/> path lies half the 12 cm kerb down: that 6 cm.
    /// </summary>
    public const float SlopedKerbRun = 0.30f;

    /// <summary>One side's profile: <see cref="D"/>/<see cref="H"/> per point, <see cref="Surface"/> per piece between two points.</summary>
    public sealed class Profile
    {
        public required float[] D { get; init; }
        public required float[] H { get; init; }
        public required StreetSurface[] Surface { get; init; }
        public int Count => D.Length;
        /// <summary>Distance from the edge to the outer end.</summary>
        public float Width => D[^1];
        /// <summary>Height of the outer end (the sidewalk's top).</summary>
        public float OuterHeight => H[^1];
    }

    /// <summary>Path height above the road for this side.</summary>
    public static float TrackHeight(RoadSide side) =>
        side.KerbCm / 100f * (side.Bike == BikeKind.TrackMid ? 0.5f : 1f);

    /// <summary>Distance from the carriageway edge to the middle of the bike path (0 without one).</summary>
    public static float TrackCentre(RoadSide side) => side.HasTrack ? (side.VergeDm + side.BikeDm * 0.5f) / 10f : 0f;

    /// <summary>
    /// The profile of a side, null when it has nothing beside the carriageway. <paramref name="lowered"/>: every kerb sloped,
    /// a crossing's kerb ramp (<see cref="RoadAttrFlags.LoweredKerbs"/>, #711: walkers and riders never meet a vertical kerb).
    /// </summary>
    public static Profile? For(RoadSide side, bool lowered = false)
    {
        if (side.OuterDm == 0) return null;
        float kerb = side.KerbCm / 100f;
        var bands = new List<(StreetSurface Surface, float Width, float Height)>();
        if (side.HasTrack && kerb > 0)
        {
            float path = TrackHeight(side);
            if (side.VergeDm > 0) bands.Add((StreetSurface.Verge, side.VergeDm / 10f, path));
            bands.Add((StreetSurface.Track, side.BikeDm / 10f, path));
            if (side.BufferDm > 0) bands.Add((StreetSurface.Buffer, side.BufferDm / 10f, path));
        }
        if (side.SidewalkDm > 0)
            bands.Add((kerb > 0 ? StreetSurface.Sidewalk : StreetSurface.Paving, side.SidewalkDm / 10f, kerb));
        if (bands.Count == 0) return null;

        var d = new List<float> { 0f };
        var h = new List<float> { 0f };
        var s = new List<StreetSurface>();
        float at = 0, height = 0;
        for (int b = 0; b < bands.Count; b++)
        {
            var (surface, width, top) = bands[b];
            float end = at + width;
            if (Math.Abs(top - height) > 1e-4f)
            {
                bool sloped = lowered || surface == StreetSurface.Track || (b > 0 && bands[b - 1].Surface == StreetSurface.Track);
                float run = sloped ? Math.Min(SlopedKerbRun, width * 0.5f) : 0f;
                d.Add(at + run); h.Add(top); s.Add(StreetSurface.Kerb);
            }
            d.Add(end); h.Add(top); s.Add(surface);
            at = end;
            height = top;
        }
        return new Profile { D = d.ToArray(), H = h.ToArray(), Surface = s.ToArray() };
    }

    /// <summary>
    /// The profile the collision is built from: every vertical step turned into a 45° chamfer
    /// (a 0.32 m foot capsule meets a vertical 12 cm kerb at 51°, at the edge of its 52° floor
    /// limit), never more than half the band it leans into.
    /// </summary>
    public static Profile Chamfered(Profile p)
    {
        var d = (float[])p.D.Clone();
        for (int i = 1; i < p.Count; i++)
        {
            if (p.Surface[i - 1] != StreetSurface.Kerb || d[i] - d[i - 1] > 1e-4f) continue;
            float step = Math.Abs(p.H[i] - p.H[i - 1]);
            float room = i + 1 < p.Count ? (p.D[i + 1] - p.D[i]) * 0.5f : 0f;
            d[i] += Math.Min(step, room);
        }
        return new Profile { D = d, H = p.H, Surface = p.Surface };
    }

    /// <summary>
    /// Where each point of the segment lies along it, 0 at the first to 1 at the last, by plan
    /// length: the parameter of <see cref="RoadSide.ShiftAt"/>.
    /// </summary>
    public static float[] Fractions(RoadSegment seg)
    {
        var p = seg.Points;
        int n = seg.PointCount;
        var t = new float[n];
        double total = 0;
        for (int i = 1; i < n; i++)
        {
            double dx = p[i * 3] - p[i * 3 - 3], dz = p[i * 3 + 2] - p[i * 3 - 1];
            total += Math.Sqrt(dx * dx + dz * dz);
            t[i] = (float)total;
        }
        for (int i = 0; i < n; i++) t[i] = total > 1e-9 ? (float)(t[i] / total) : 0f;
        return t;
    }

    /// <summary>Height of the side's surface at <paramref name="distance"/> metres out from the carriageway edge (0 on it).</summary>
    public static float HeightAt(RoadSide side, float distance)
    {
        if (distance <= 0 || For(side) is not { } p) return 0f;
        for (int i = 1; i < p.Count; i++)
        {
            if (distance > p.D[i]) continue;
            float span = p.D[i] - p.D[i - 1];
            return span < 1e-5f ? p.H[i] : p.H[i - 1] + (p.H[i] - p.H[i - 1]) * (distance - p.D[i - 1]) / span;
        }
        return p.OuterHeight;
    }
}
