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

    // ---- the slot (#1372): the owner's rules, pure ----

    /// <summary>Seconds in a slot after which a body that cannot climb out is set on the lip (the server's flag lasts at most this long too).</summary>
    public const double RescueSeconds = 60;
    /// <summary>How far under the surface the body must have been before "back at the top" counts as out (m).</summary>
    public const double DeepEnough = 2;
    /// <summary>How far under the lip a roped fall is caught: the rope runs over the lip, so the body hangs this far under it (m).</summary>
    public const double CatchDepth = 3;

    /// <summary>The rope holds the fall: the partner stands (not climbing, not riding) and is within the rope's length of the break.</summary>
    public static bool CaughtByRope(bool partnerStands, double ropeDistance, double ropeLength) =>
        partnerStands && ropeDistance <= ropeLength;

    /// <summary>
    /// A body is out of the slot: it has been deep, and its feet are back at the ground (<paramref name="bodyY"/> against
    /// <paramref name="groundY"/> under it). Never out while it never went down (the first ticks of the fall).
    /// </summary>
    public static bool Out(double bodyY, double groundY, double deepestBelowGround) =>
        deepestBelowGround <= -DeepEnough && bodyY >= groundY;

    /// <summary>A body that has been in the slot too long: no stamina, no rope. It is set on the lip.</summary>
    public static bool Stuck(double secondsInside) => secondsInside >= RescueSeconds;

    /// <summary>
    /// The strip's heading in the world's plane: (E, N) per metre of its length, so a caller can turn the slot along it.
    /// </summary>
    public static (double E, double N) Heading(double angle) => (Math.Cos(angle), Math.Sin(angle));

    /// <summary>The point of the strip's line (through <paramref name="c"/> at its angle) nearest to (<paramref name="e"/>, <paramref name="n"/>): where the slot opens.</summary>
    public static (double E, double N) OnLine((double E, double N, double Angle) c, double e, double n)
    {
        var (he, hn) = Heading(c.Angle);
        double along = (e - c.E) * he + (n - c.N) * hn;
        return (c.E + he * along, c.N + hn * along);
    }
}
