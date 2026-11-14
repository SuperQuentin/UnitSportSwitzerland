using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// <see cref="CubeBody"/>'s sampling (#1849), apart from the stage so the unit tests link it: a lattice point's
/// planetocentric place, its footprint mean from a <see cref="CylRaster.Window"/>, and the window a face reads.
/// </summary>
public static class CubeBodyHeights
{
    /// <summary>A lattice point's planetocentric latitude and longitude, degrees: what the DEMs are gridded in.</summary>
    public static (double Lat, double Lon) LatLonOf(CubeShape cube, int face, int level, long i, long j)
    {
        var p = cube.LatticeEcef(face, level, i, j, 0);
        return (Math.Atan2(p.Z, Math.Sqrt(p.X * p.X + p.Y * p.Y)) * 180 / Math.PI, Math.Atan2(p.Y, p.X) * 180 / Math.PI);
    }

    /// <summary>Half a lattice step at a face centre, metres, at a lattice level: the footprint's half side.</summary>
    public static double HalfStep(CubeShape cube, int latticeLevel) => 0.5 * cube.Shape.A * Math.PI / 2 / (1L << latticeLevel);

    /// <summary>A tile's (2^bits + 1)² heights, each from its own lattice point alone.</summary>
    public static float[] Heights(CubeShape cube, CylRaster.Window dem, CubeKey key, int bits)
    {
        int n = (1 << bits) + 1, lv = key.Level + bits;
        long gi0 = (long)key.X << bits, gj0 = (long)key.Y << bits;
        double half = HalfStep(cube, lv);
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var (lat, lon) = LatLonOf(cube, key.Face, lv, gi0 + i, gj0 + j);
                h[j * n + i] = (float)dem.Footprint(lat, lon, half, cube.Shape.A);
            }
        return h;
    }

    /// <summary>
    /// The rows and longitudes a face's tiles read, with room for the widest footprint (the coarsest level's) and two
    /// pixels: a face round a pole takes every longitude.
    /// </summary>
    public static (int Y0, int Y1, int X0, int W) WindowOf(CubeShape cube, int face, CylRaster raster, int minLevel)
    {
        const int n = 64;
        double latMin = 90, latMax = -90;
        var (_, centreLon) = LatLonOf(cube, face, 1, 1, 1);
        double dMin = 180, dMax = -180;
        bool pole = false;
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                var (lat, lon) = LatLonOf(cube, face, 6, i, j);
                latMin = Math.Min(latMin, lat);
                latMax = Math.Max(latMax, lat);
                double d = Math.IEEERemainder(lon - centreLon, 360);
                dMin = Math.Min(dMin, d);
                dMax = Math.Max(dMax, d);
            }
        // a face holding a pole sees every longitude
        int poleN = cube.FromLatLon(90, 0).Face, poleS = cube.FromLatLon(-90, 0).Face;
        if (face == poleN) { latMax = 90; pole = true; }
        if (face == poleS) { latMin = -90; pole = true; }

        double marginM = 8 * HalfStep(cube, minLevel + CubeKey.LatticeBits(minLevel));
        double marginDeg = marginM / cube.Shape.A * 180 / Math.PI + 2 * raster.Step;
        double cos = Math.Max(Math.Cos(Math.Max(Math.Abs(latMin), Math.Abs(latMax)) * Math.PI / 180), 0.05);
        int y0 = (int)Math.Floor((90 - Math.Min(90, latMax + marginDeg)) / raster.Step) - 1;
        int y1 = (int)Math.Ceiling((90 - Math.Max(-90, latMin - marginDeg)) / raster.Step) + 1;
        if (pole || dMax - dMin + 2 * marginDeg / cos >= 300) return (y0, y1, 0, raster.Width);
        double lonFrom = centreLon + dMin - marginDeg / cos;
        int x0 = (int)Math.Floor((lonFrom + 180) / raster.Step) - 1;
        int w = (int)Math.Ceiling((dMax - dMin + 2 * marginDeg / cos) / raster.Step) + 3;
        return (y0, y1, x0, w);
    }
}
