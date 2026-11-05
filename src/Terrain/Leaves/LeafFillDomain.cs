using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// Where the generated fill may generate, in cube cells (#1509, G2 B5): <c>FillDomain</c>'s rule with discs on
/// the Earth instead of LV95 tile boxes. A leaf is in the domain when its centre lies within <see cref="RadiusM"/> of the
/// spawn, of a place it was asked to <see cref="Reach"/>, or of the real leaves' disc. Distances are great-circle on the
/// mean sphere, so the domain is the same shape on every face and across face edges. Immutable, it only ever grows.
/// </summary>
public sealed class LeafFillDomain
{
    /// <summary>The fill's reach: <c>FallbackChunkSource.FillRadiusTiles</c>' 40 km.</summary>
    public const double RadiusM = 40_000;

    private const double EarthRadiusM = 6_371_008.8;

    /// <summary>A disc: its centre's unit normal and its angular radius (radians).</summary>
    public readonly record struct Disc(double X, double Y, double Z, double Angle)
    {
        public static Disc Around(double latDeg, double lonDeg, double radiusM)
        {
            var (x, y, z) = Normal(latDeg, lonDeg);
            return new Disc(x, y, z, radiusM / EarthRadiusM);
        }

        public bool Contains(double latDeg, double lonDeg)
        {
            var (x, y, z) = Normal(latDeg, lonDeg);
            return Math.Acos(Math.Clamp(X * x + Y * y + Z * z, -1, 1)) <= Angle;
        }
    }

    private readonly Disc[] _discs;

    /// <summary>The spawn's disc, then every disc <see cref="Reach"/> added, oldest first.</summary>
    public IReadOnlyList<Disc> Discs => _discs;

    /// <summary>The real leaves' disc grown by the radius; null with no real leaves yet.</summary>
    public Disc? RealDisc { get; }

    private LeafFillDomain(Disc[] discs, Disc? realDisc)
    {
        _discs = discs;
        RealDisc = realDisc;
    }

    public static LeafFillDomain At(double latDeg, double lonDeg, double radiusM = RadiusM) =>
        new([Disc.Around(latDeg, lonDeg, radiusM)], null);

    public bool Contains(CubeKey leaf)
    {
        var c = leaf.Centre;
        return Contains(c.LatDeg, c.LonDeg);
    }

    public bool Contains(double latDeg, double lonDeg)
    {
        if (RealDisc?.Contains(latDeg, lonDeg) == true) return true;
        foreach (var d in _discs)
            if (d.Contains(latDeg, lonDeg)) return true;
        return false;
    }

    /// <summary>The domain grown so it holds a place: itself when it already does, else with a disc round it.</summary>
    public LeafFillDomain Reach(double latDeg, double lonDeg, double radiusM = RadiusM) =>
        Contains(latDeg, lonDeg) ? this : new LeafFillDomain([.. _discs, Disc.Around(latDeg, lonDeg, radiusM)], RealDisc);

    /// <summary>
    /// The same domain with the real leaves' disc: the smallest disc round their centres (about their mean normal) grown by
    /// the radius, and never smaller than the last one (it is widened to hold it).
    /// </summary>
    public LeafFillDomain WithReal(IEnumerable<CubeKey> real, double radiusM = RadiusM)
    {
        var centres = real.Select(k => Normal(k.Centre.LatDeg, k.Centre.LonDeg)).ToList();
        if (centres.Count == 0) return this;
        double sx = centres.Sum(c => c.X), sy = centres.Sum(c => c.Y), sz = centres.Sum(c => c.Z);
        double len = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        (sx, sy, sz) = (sx / len, sy / len, sz / len);
        double angle = centres.Max(c => Math.Acos(Math.Clamp(sx * c.X + sy * c.Y + sz * c.Z, -1, 1))) + radiusM / EarthRadiusM;
        if (RealDisc is { } previous)
            angle = Math.Max(angle, Math.Acos(Math.Clamp(sx * previous.X + sy * previous.Y + sz * previous.Z, -1, 1)) + previous.Angle);
        return new LeafFillDomain(_discs, new Disc(sx, sy, sz, angle));
    }

    /// <summary>The leaves of the domain round a place, out to <paramref name="radiusM"/> (breadth-first over neighbours).</summary>
    public IEnumerable<CubeKey> LeavesAround(double latDeg, double lonDeg, double radiusM)
    {
        var near = Disc.Around(latDeg, lonDeg, radiusM);
        var start = LeafFrames.LeafAt(latDeg, lonDeg);
        var seen = new HashSet<CubeKey> { start };
        var queue = new Queue<CubeKey>([start]);
        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            if (Contains(k)) yield return k;
            foreach (var n in k.Neighbours())
            {
                var c = n.Centre;
                if (near.Contains(c.LatDeg, c.LonDeg) && seen.Add(n)) queue.Enqueue(n);
            }
        }
    }

    private static (double X, double Y, double Z) Normal(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        return (Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }
}
