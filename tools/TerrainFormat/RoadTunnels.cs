namespace UnitSport.Terrain.Format;

/// <summary>
/// Tunnel bore geometry shared by the runtime mesh (<c>RoadMeshBuilder</c>), the road blend
/// (<c>TerrainMeshBuilder.ComputeRoadBlend</c>, which keeps ground over every bore) and the player's
/// safety net (a body inside a bore is not under the ground).
///
/// <para>
/// A bore's clear height is its class's (<see cref="RoadFormat.TunnelHeight"/>), lowered only where
/// an at-grade road crosses over it closer than that (it may not poke through the street above),
/// never under <see cref="MinClear"/>. It used to be lowered to the raw terrain's cover, which over a
/// city underpass is the open trench in the elevation model: a 2.4 m bore a car scraped (#119).
/// </para>
/// </summary>
public static class RoadTunnels
{
    public const float MinClear = 3.2f;

    /// <summary>What a crossing road above needs between the bore's crown and its own surface.</summary>
    public const float DeckUnderRoad = 0.6f;

    /// <summary>A bore's floor (and what counts as inside it) reaches this far past its walls: over the mouth's hole.</summary>
    public const float FloorMargin = 0.6f;

    /// <summary>The ground over a bore lies at least this far above its crown.</summary>
    public const float GroundOverCrown = 0.3f;

    /// <summary>
    /// A tunnel end is a mouth only where the bore meets the surface: the ground there lies less
    /// than this above its crown. TLM leaves gaps in underground lines (a station hall, a junction),
    /// and their ends lie 15 m under a city: each one opened as an entrance (#119).
    /// </summary>
    public const double MouthCover = 3.0;

    /// <summary>Whether a tunnel end at road height <paramref name="y"/> under ground <paramref name="ground"/> can be a mouth.</summary>
    public static bool AtSurface(double ground, double y, double clearHeight) =>
        double.IsNaN(ground) || ground - (y + clearHeight) < MouthCover;

    public static bool IsBore(RoadSegment s) =>
        (s.Flags & RoadFlags.Tunnel) != 0 && s.PointCount >= 2 && s.Class <= RoadClass.Square;

    public static float HalfWidth(RoadSegment s) => RoadFormat.TunnelWidth(s.Class) * 0.5f;

    /// <summary>Clear height of a tunnel segment of <paramref name="tile"/>.</summary>
    public static float ClearHeight(RoadSegment tunnel, RoadTile tile)
    {
        float height = RoadFormat.TunnelHeight(tunnel.Class);
        float half = HalfWidth(tunnel);
        var p = tunnel.Points;
        foreach (var road in tile.Segments)
        {
            if (!RoadEmbankment.IsAtGrade(road) || road.Class > RoadClass.Square || road.PointCount < 2) continue;
            var q = road.Points;
            double reach = half + road.Width * 0.5;
            for (int j = 0; j < road.PointCount; j++)
            {
                double x = q[j * 3], z = q[j * 3 + 2];
                if (!Nearest(p, tunnel.PointCount, x, z, out double d, out double y) || d > reach) continue;
                double room = q[j * 3 + 1] - y - DeckUnderRoad;
                if (room > 0 && room < height) height = (float)room;
            }
        }
        return Math.Max(height, MinClear);
    }

    /// <summary>
    /// Whether a tile-local point lies inside one of the tile's bores: within its half width of the
    /// centreline (plan), from a metre under its road to its crown. <paramref name="bores"/> from
    /// <see cref="Bores"/>.
    /// </summary>
    public static bool Inside(IReadOnlyList<(float[] Points, int Count, float Half, float Height)> bores, double x, double y, double z)
    {
        foreach (var b in bores)
            if (Nearest(b.Points, b.Count, x, z, out double d, out double road) && d <= b.Half + FloorMargin
                && y > road - 1.0 && y < road + b.Height + 0.5)
                return true;
        return false;
    }

    /// <summary>Every bore of a tile: centreline, half width and clear height.</summary>
    public static List<(float[] Points, int Count, float Half, float Height)> Bores(RoadTile tile)
    {
        var result = new List<(float[], int, float, float)>();
        foreach (var s in tile.Segments)
            if (IsBore(s)) result.Add((s.Points, s.PointCount, HalfWidth(s), ClearHeight(s, tile)));
        return result;
    }

    /// <summary>Plan distance from (x, z) to the polyline and the line's height at that foot.</summary>
    public static bool Nearest(float[] p, int count, double x, double z, out double distance, out double height)
    {
        distance = double.MaxValue;
        height = 0;
        for (int i = 0; i + 1 < count; i++)
        {
            double ax = p[i * 3], az = p[i * 3 + 2], bx = p[i * 3 + 3], bz = p[i * 3 + 5];
            double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
            double t = l2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0, 1);
            double ex = ax + dx * t - x, ez = az + dz * t - z, d = Math.Sqrt(ex * ex + ez * ez);
            if (d >= distance) continue;
            distance = d;
            height = p[i * 3 + 1] + (p[i * 3 + 4] - p[i * 3 + 1]) * t;
        }
        return distance < double.MaxValue;
    }
}
