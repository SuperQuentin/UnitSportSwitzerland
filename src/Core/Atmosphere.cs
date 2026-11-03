namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// The US Standard Atmosphere 1976 (#1430 H5, docs/plans/ellipsoid-handover.md): temperature, pressure
/// and density from sea level to the edge of space. Seven layers of constant lapse rate in geopotential
/// altitude to 86 km (the troposphere to 11 km, isothermal to 20, then +1, +2.8, 0, −2.8, −2 K/km), the
/// standard's own table from 86 to 150 km (log-linear between rows), and no air above 150 km. Altitudes
/// are geometric, metres above sea level (what <c>GlobalPos.Alt</c> is, near enough).
/// </summary>
public static class Atmosphere
{
    /// <summary>Sea level: 288.15 K, 101,325 Pa, 1.225 kg/m³.</summary>
    public const double SeaLevelTemperature = 288.15, SeaLevelPressure = 101_325, SeaLevelDensity = 1.225;

    /// <summary>The standard's gravity and the specific gas constant of dry air, J/(kg·K).</summary>
    public const double G0 = 9.80665, R = 287.05287;

    /// <summary>The Earth radius the standard turns geometric into geopotential altitude with, metres.</summary>
    public const double EarthRadius = 6_356_766;

    /// <summary>The top of the layered part (86 km geometric) and of any air at all.</summary>
    public const double LayersTopM = 86_000, TopM = 150_000;

    // the layers' bases in geopotential metres, their lapse rates (K/m) and base temperatures; the base
    // pressures follow from them (computed once below, so the layers meet exactly)
    private static readonly double[] BaseH = { 0, 11_000, 20_000, 32_000, 47_000, 51_000, 71_000 };
    private static readonly double[] Lapse = { -0.0065, 0, 0.001, 0.0028, 0, -0.0028, -0.002 };
    private static readonly double[] BaseT = new double[7];
    private static readonly double[] BaseP = new double[7];

    // the standard's table above 86 km: geometric km, K, Pa, kg/m³
    private static readonly double[] HighZ = { 86, 90, 95, 100, 110, 120, 130, 140, 150 };
    private static readonly double[] HighT = { 186.87, 186.87, 188.42, 195.08, 240.0, 360.0, 469.27, 559.63, 634.39 };
    private static readonly double[] HighP = { 0.37338, 0.18359, 0.075966, 0.032011, 0.0071042, 0.0025382, 0.0012505, 0.00072028, 0.00045422 };
    private static readonly double[] HighRho = { 6.958e-6, 3.416e-6, 1.393e-6, 5.604e-7, 9.708e-8, 2.222e-8, 8.152e-9, 3.831e-9, 2.076e-9 };

    static Atmosphere()
    {
        BaseT[0] = SeaLevelTemperature;
        BaseP[0] = SeaLevelPressure;
        for (int i = 1; i < BaseH.Length; i++)
        {
            double dh = BaseH[i] - BaseH[i - 1];
            BaseT[i] = BaseT[i - 1] + Lapse[i - 1] * dh;
            BaseP[i] = LayerPressure(i - 1, BaseH[i]);
        }
    }

    /// <summary>Geopotential altitude of a geometric one, metres.</summary>
    public static double Geopotential(double z) => EarthRadius * z / (EarthRadius + z);

    /// <summary>Temperature at a geometric altitude, K (the molecular-scale one below 86 km: 0.04 % off at 86 km).</summary>
    public static double Temperature(double z)
    {
        if (z >= LayersTopM) return High(HighT, Math.Min(z, TopM), log: false);
        double h = Geopotential(Math.Max(z, 0));
        int i = Layer(h);
        return BaseT[i] + Lapse[i] * (h - BaseH[i]);
    }

    /// <summary>Pressure at a geometric altitude, Pa; zero above <see cref="TopM"/>.</summary>
    public static double Pressure(double z)
    {
        if (z > TopM) return 0;
        if (z >= LayersTopM) return High(HighP, z, log: true);
        double h = Geopotential(Math.Max(z, 0));
        int i = Layer(h);
        return LayerPressure(i, h);
    }

    /// <summary>Density at a geometric altitude, kg/m³: 1.225 at sea level, 4.0·10⁻³ at 40 km, 5.6·10⁻⁷ at 100 km, zero above 150 km.</summary>
    public static double Density(double z)
    {
        if (z > TopM) return 0;
        if (z >= LayersTopM) return High(HighRho, z, log: true);
        double h = Geopotential(Math.Max(z, 0));
        int i = Layer(h);
        return LayerPressure(i, h) / (R * (BaseT[i] + Lapse[i] * (h - BaseH[i])));
    }

    /// <summary>Density over sea level's, the σ of the airspeed indicator.</summary>
    public static double Sigma(double z) => Density(z) / SeaLevelDensity;

    /// <summary>The layer holding a geopotential altitude (clamped to the layered part).</summary>
    private static int Layer(double h)
    {
        int i = BaseH.Length - 1;
        while (i > 0 && h < BaseH[i]) i--;
        return i;
    }

    /// <summary>The barometric formula within layer <paramref name="i"/>.</summary>
    private static double LayerPressure(int i, double h)
    {
        double tb = BaseT[i], l = Lapse[i], dh = h - BaseH[i];
        return l == 0
            ? BaseP[i] * Math.Exp(-G0 * dh / (R * tb))
            : BaseP[i] * Math.Pow(tb / (tb + l * dh), G0 / (R * l));
    }

    /// <summary>The table above 86 km, interpolated linearly or in log space.</summary>
    private static double High(double[] values, double z, bool log)
    {
        double km = z / 1000;
        int i = 0;
        while (i < HighZ.Length - 2 && km > HighZ[i + 1]) i++;
        double t = (km - HighZ[i]) / (HighZ[i + 1] - HighZ[i]);
        return log
            ? Math.Exp(Math.Log(values[i]) + t * (Math.Log(values[i + 1]) - Math.Log(values[i])))
            : values[i] + t * (values[i + 1] - values[i]);
    }
}
