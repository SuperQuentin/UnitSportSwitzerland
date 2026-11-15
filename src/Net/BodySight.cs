using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// The line of sight between two places on one body other than the Earth (#1852, C3b of docs/plans/planets.md section 10):
/// the body's sphere is the only occluder (the server holds no leaves there; the Earth keeps <see cref="EarthSight"/>). The
/// sphere is the body's polar radius less a margin (2 km, or a tenth of a small moon's), so relief never hides a player
/// it should not: the ridges in between err toward "seen", as the Earth's horizon lattice does.
/// </summary>
public static class BodySight
{
    /// <summary>The sphere sits this far under the polar radius, metres (at most a tenth of the radius).</summary>
    public const double MarginM = 2000;

    /// <summary>Whether the straight line between <paramref name="a"/> and <paramref name="b"/> (one body) stays outside its sphere.</summary>
    public static bool Clear(GlobalPos a, GlobalPos b)
    {
        if (a.Body != b.Body) return false;
        var shape = CelestialBody.Of(a.Body).Shape;
        double margin = System.Math.Min(MarginM, 0.1 * shape.B);
        // under the lower of the two too: two players down a basin deeper than the margin see each other across its floor
        double low = System.Math.Min(Length(a.Ecef), Length(b.Ecef));
        return Clear(a.Ecef, b.Ecef, System.Math.Min(shape.B, low) - margin);
    }

    private static double Length(Terrain.Format.EcefPos p) => System.Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);

    /// <summary>Whether the segment between two body-fixed points passes no nearer the centre than <paramref name="radius"/>.</summary>
    public static bool Clear(Terrain.Format.EcefPos a, Terrain.Format.EcefPos b, double radius)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        double len2 = dx * dx + dy * dy + dz * dz;
        // the point of the segment nearest the centre (the origin of the body's frame)
        double t = len2 > 0 ? System.Math.Clamp(-(a.X * dx + a.Y * dy + a.Z * dz) / len2, 0, 1) : 0;
        double px = a.X + t * dx, py = a.Y + t * dy, pz = a.Z + t * dz;
        return px * px + py * py + pz * pz >= radius * radius;
    }
}
