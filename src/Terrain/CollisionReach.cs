using System;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Which tiles a server-run body needs collision on (#837): a small radius around it, not a
/// client's 3x3 ring of 1 km tiles. A bot walks at ~5 m/s, so 300 m of solid ground is a minute
/// of walking; a driving bot also wants the point it reaches in <see cref="LookAheadSeconds"/>,
/// and nothing is wanted high in the air. Pure, unit-tested (<c>CollisionReachTests</c>).
/// </summary>
public static class CollisionReach
{
    /// <summary>Tiles within this of the body (or its look-ahead point) get collision.</summary>
    public const double RadiusM = 300;
    /// <summary>A tile keeps its collision until every body is this far: no load/free loop on a border.</summary>
    public const double ReleaseM = 400;
    /// <summary>How far ahead along the velocity a moving body asks for ground.</summary>
    public const double LookAheadSeconds = 10;
    /// <summary>The look-ahead never reaches further than this (a fast car, a falling body).</summary>
    public const double LookAheadMaxM = 600;
    /// <summary>Above the ground by more than this, a body needs no collision (plane, wingsuit, parachute).</summary>
    public const double AirGapM = 200;
    /// <summary>The ring key's grain: the wanted set is only recomputed when a body crosses one.</summary>
    public const double CellM = 250;

    /// <summary>Horizontal distance from a point to a tile's square (0 inside it).</summary>
    public static double DistanceToTile(TileId tile, double e, double n)
    {
        double dx = Math.Max(0, Math.Max(tile.MinE - e, e - (tile.MinE + ChunkFormat.TileSizeM)));
        double dy = Math.Max(0, Math.Max(tile.MinN - n, n - (tile.MinN + ChunkFormat.TileSizeM)));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The look-ahead point: where the body will be in <see cref="LookAheadSeconds"/> at this
    /// velocity (LV95 east, north, up), clamped to <see cref="LookAheadMaxM"/> horizontally.
    /// </summary>
    public static (double E, double N, double Alt) Ahead(double e, double n, double alt, double ve, double vn, double vUp)
    {
        double de = ve * LookAheadSeconds, dn = vn * LookAheadSeconds, du = vUp * LookAheadSeconds;
        double flat = Math.Sqrt(de * de + dn * dn);
        if (flat > LookAheadMaxM)
        {
            double k = LookAheadMaxM / flat;
            de *= k; dn *= k; du *= k;
        }
        return (e + de, n + dn, alt + du);
    }

    /// <summary>
    /// Whether a body at (e, n) wants collision on <paramref name="tile"/>, or still holds it
    /// (<paramref name="holding"/>: the wider <see cref="ReleaseM"/>). <paramref name="airborne"/>
    /// is true when both the body and its look-ahead point are more than <see cref="AirGapM"/>
    /// above the ground.
    /// </summary>
    public static bool Wants(TileId tile, double e, double n, double aheadE, double aheadN, bool airborne, bool holding = false)
    {
        if (airborne) return false;
        double r = holding ? ReleaseM : RadiusM;
        return DistanceToTile(tile, e, n) <= r || DistanceToTile(tile, aheadE, aheadN) <= r;
    }

    /// <summary>The 250 m cell a point is in, packed: the ring key changes only when this does.</summary>
    public static long CellKey(double e, double n) =>
        ((long)Math.Floor(e / CellM) << 32) ^ (uint)(int)Math.Floor(n / CellM);
}
