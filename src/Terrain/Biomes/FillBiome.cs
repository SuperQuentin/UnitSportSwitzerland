namespace UnitSport.Terrain.Biomes;

/// <summary>
/// The earth fill's biome at a point (#1809, <c>docs/plans/fill-biomes.md</c>), by the first step of the chain that
/// decides:
/// <list type="number">
/// <item>the Swiss guard: inside the generator's own map (<see cref="GuardBandM"/> in, dithered across the band), the
/// generator unchanged, so the fill over and round Switzerland stays what it was;</item>
/// <item>land cover (<see cref="LandCover"/>; phase 3 of the plan, none yet);</item>
/// <item>the photo guess: the shipped globe colour's statistics (<see cref="PhotoStats"/>) and the latitude;</item>
/// <item>the Swiss generator, where nothing knows the point.</item>
/// </list>
/// Then the latitude's snow and tree lines (<see cref="SnowLineM"/>, <see cref="TreeLineM"/>) by altitude.
/// A pure function of its inputs on every peer: +, -, *, / and floor on doubles, no transcendental function (Windows
/// and Linux runtimes may differ in their last bit there). Thread-safe.
/// </summary>
public sealed class FillBiome
{
    /// <summary>The Swiss guard fades out over this band at the generator map's edge, metres.</summary>
    public const double GuardBandM = 20_000;

    /// <summary>The guess's samples are moved by up to half this, degrees, by smooth noise: ragged biome edges, not 0.2° cells.</summary>
    public const double WarpDeg = 0.25;

    /// <summary>The warp noise's cell, degrees.</summary>
    public const double WarpCellDeg = 0.15;

    /// <summary>The guard band's dither noise cell, degrees (~2 km).</summary>
    public const double GuardCellDeg = 0.02;

    private readonly PhotoStats? _photo;

    /// <summary>The land-cover step: a biome at a latitude and longitude, or null where it has no value. None yet (phase 3).</summary>
    public Func<double, double, Biome?>? LandCover { get; init; }

    public FillBiome(PhotoStats? photo) => _photo = photo;

    /// <summary>The chain with the photo grid this build embeds.</summary>
    public static FillBiome Default { get; } = new(PhotoStats.Embedded);

    /// <summary>
    /// The biome at a latitude, longitude (degrees) and altitude above sea level (metres), with
    /// <paramref name="insideMapM"/> how far the point lies inside the generator's own map (negative outside).
    /// </summary>
    public Biome At(double latDeg, double lonDeg, double altitudeM, double insideMapM, out BiomeSource source)
    {
        if (insideMapM >= GuardBandM || (insideMapM > 0 && ValueNoise(latDeg / GuardCellDeg, lonDeg / GuardCellDeg, 11) < insideMapM / GuardBandM))
        {
            source = BiomeSource.Guard;
            return Biome.Swiss;
        }
        Biome? b = LandCover?.Invoke(latDeg, lonDeg);
        if (b != null) source = BiomeSource.LandCover;
        else if (_photo != null)
        {
            source = BiomeSource.PhotoGuess;
            double wy = (ValueNoise(latDeg / WarpCellDeg, lonDeg / WarpCellDeg, 3) - 0.5) * WarpDeg;
            double wx = (ValueNoise(latDeg / WarpCellDeg, lonDeg / WarpCellDeg, 5) - 0.5) * WarpDeg;
            b = Guess(_photo.At(latDeg + wy, lonDeg + wx), latDeg);
        }
        else
        {
            source = BiomeSource.Generator;
            return Biome.Swiss;
        }
        return ByAltitude(b.Value, latDeg, altitudeM);
    }

    /// <summary>The latitude's snow and tree lines over a decided biome: ice over the one, alpine tundra over the other.</summary>
    public static Biome ByAltitude(Biome b, double latDeg, double altitudeM)
    {
        if (b == Biome.Sea) return b;
        if (altitudeM > SnowLineM(latDeg)) return Biome.Ice;
        if (altitudeM > TreeLineM(latDeg) && b is not (Biome.Desert or Biome.Ice)) return Biome.Tundra;
        return b;
    }

    /// <summary>The photo guess (<c>docs/plans/fill-biomes.md</c>, "The photo guess"): a cell's statistics and latitude.</summary>
    public static Biome Guess(PhotoStats.Sample s, double latDeg)
    {
        double lat = Math.Abs(latDeg);
        double luma = (s.R + s.G + s.B) / 3, green = s.G - (s.R + s.B) / 2, warm = s.R - s.B;
        if (s.Sea >= 0.5) return Biome.Sea;
        if (s.SnowMonths >= 10.5 || (luma > 0.75 && Math.Abs(warm) < 0.06)) return Biome.Ice;
        if (lat >= 55 && s.SnowMonths >= 6) return Biome.Tundra;
        // bare and warm: sand and rock (the Sahara 0.57 luma, green 0.02, warm 0.26); a red one is darker (the outback)
        if ((green < 0.035 && warm > 0.12 && luma > 0.38) || (green < 0.0 && warm > 0.2)) return Biome.Desert;
        // dry grass: barely green and warm (Kazakhstan 0.27, 0.04, 0.15)
        if (green < 0.06 && warm > 0.10 && luma > 0.2) return lat <= 23.5 ? Biome.Savanna : Biome.Steppe;
        // the wet tropics: dark green all year (the Amazon 0.10, 0.10, 0.06)
        if (lat <= 23.5 && luma < 0.15 && green > 0.06) return Biome.TropicalForest;
        // the rest of the tropics and subtropics, warm and not dark: savanna (the Serengeti, the cerrado)
        if (lat <= 28 && luma >= 0.15 && warm > 0.09) return Biome.Savanna;
        // dark under snow for months: the taiga (Finland 0.10, 5 snowy months)
        if (lat >= 50 && s.SnowMonths >= 4 && luma < 0.3) return Biome.Boreal;
        return Biome.Swiss;
    }

    private static readonly (double Lat, double M)[] SnowLine =
        [(0, 4900), (25, 5500), (35, 4800), (46, 2900), (60, 1500), (70, 600), (80, 200), (90, 0)];

    private static readonly (double Lat, double M)[] TreeLine =
        [(0, 3800), (25, 4000), (35, 3300), (46, 2100), (60, 900), (68, 300), (72, 0), (90, 0)];

    /// <summary>The permanent snow line at a latitude, metres (2,900 at 46°: <c>CoverPalette.SnowLine</c>).</summary>
    public static double SnowLineM(double latDeg) => Piecewise(SnowLine, Math.Abs(latDeg));

    /// <summary>The tree line at a latitude, metres (2,100 at 46°, as the generator's Alps).</summary>
    public static double TreeLineM(double latDeg) => Piecewise(TreeLine, Math.Abs(latDeg));

    private static double Piecewise((double Lat, double M)[] table, double lat)
    {
        for (int k = 1; k < table.Length; k++)
            if (lat <= table[k].Lat)
            {
                var (a, b) = (table[k - 1], table[k]);
                return a.M + (b.M - a.M) * (lat - a.Lat) / (b.Lat - a.Lat);
            }
        return table[^1].M;
    }

    /// <summary>A deterministic hash of three integers in [0, 1).</summary>
    public static double Hash01(long x, long y, int salt)
    {
        ulong h = (ulong)x * 0x9E3779B97F4A7C15UL ^ (ulong)y * 0xC2B2AE3D27D4EB4FUL ^ (ulong)salt * 0x165667B19E3779F9UL;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>Smooth value noise in [0, 1) on a unit lattice (quintic fade): polynomials and floor only.</summary>
    public static double ValueNoise(double x, double y, int salt)
    {
        double fx = Math.Floor(x), fy = Math.Floor(y);
        long ix = (long)fx, iy = (long)fy;
        double u = Fade(x - fx), v = Fade(y - fy);
        double a = Hash01(ix, iy, salt), b = Hash01(ix + 1, iy, salt), c = Hash01(ix, iy + 1, salt), d = Hash01(ix + 1, iy + 1, salt);
        double s = a + (b - a) * u, n = c + (d - c) * u;
        return s + (n - s) * v;

        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    }
}
