namespace UnitSport.Adventure.Together;

/// <summary>
/// Hidden crevasses on glaciers (#1266 part 4), pure and the same on every peer: the glacier is cut into 12 m cells by
/// LV95 metres; 4 % of the cells hold a crevasse, a strip 1.5 m wide across the cell at an angle of its own. Only on
/// glacier cover (the caller's test: the cover raster is kept near the owner). Tier 0 (<c>CrevassesTests</c>).
/// </summary>
public static class Crevasses
{
    public const double Cell = 12;
    public const double Chance = 0.04;
    public const double Width = 1.5;

    /// <summary>A cell's key (its indices packed): what the owner remembers per cell it entered.</summary>
    public static long CellKey(double e, double n) => ((long)Math.Floor(e / Cell) << 32) ^ (uint)(long)Math.Floor(n / Cell);

    private static ulong Hash(long ce, long cn)
    {
        ulong z = (ulong)ce * 0x9E3779B97F4A7C15UL ^ (ulong)cn * 0xC2B2AE3D27D4EB4FUL ^ 0x1266CAFEUL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>The crevasse of the cell holding (<paramref name="e"/>, <paramref name="n"/>): its centre and heading (radians), or null.</summary>
    public static (double E, double N, double Angle)? InCell(double e, double n)
    {
        long ce = (long)Math.Floor(e / Cell), cn = (long)Math.Floor(n / Cell);
        ulong h = Hash(ce, cn);
        if ((h & 0xFFFFFF) / (double)0x1000000 >= Chance) return null;
        double angle = ((h >> 24) & 0xFFFF) / 65536.0 * Math.PI;
        double ox = (((h >> 40) & 0xFF) / 255.0 - 0.5) * 4, oy = (((h >> 48) & 0xFF) / 255.0 - 0.5) * 4;
        return ((ce + 0.5) * Cell + ox, (cn + 0.5) * Cell + oy, angle);
    }

    /// <summary>Whether a point on a glacier stands on a hidden crevasse (inside its cell, within half its width of the strip's line).</summary>
    public static bool At(double e, double n, bool onGlacier = true)
    {
        if (!onGlacier || InCell(e, n) is not { } c) return false;
        // the strip's line through the centre at its angle: the distance across it
        double de = e - c.E, dn = n - c.N;
        double across = Math.Abs(-Math.Sin(c.Angle) * de + Math.Cos(c.Angle) * dn);
        return across <= Width / 2;
    }
}
