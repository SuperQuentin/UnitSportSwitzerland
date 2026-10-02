namespace UnitSport.World;

/// <summary>
/// The wave field's constants and maths (#299), the single source of truth: plain C# (no Godot),
/// unit-tested, read by <see cref="WaterField"/> for physics and pushed by it to the shader globals
/// that <c>shaders/common/waves.gdshaderinc</c> evaluates the same sum with. Nothing about the waves
/// is written anywhere else: the shader only has the formula, every number comes from here.
///
/// <para>
/// A sum of <see cref="Count"/> Gerstner waves. Each is pinned to the ground and the clock so every
/// peer, and the GPU, gets the same surface with nothing replicated but the sea state:
/// <list type="bullet">
/// <item>Its wave vector is a whole number of cycles over <see cref="PeriodM"/> (9.6 km, the period
/// of the shader's <c>world_origin_offset</c>), so a point's phase depends only on its LV95
/// position modulo 9.6 km: the floating origin can move without the waves jumping.</item>
/// <item>Its angular frequency is a whole number of cycles over <see cref="LoopS"/>, so the time
/// is the synchronized server clock modulo 20 min (<see cref="WaveTime"/>), small enough for a
/// float on the GPU after days of uptime, with no jump at the wrap. Deep-water dispersion
/// (omega² = g k) is kept to within a fraction of a percent by the rounding.</item>
/// </list>
/// Coordinates are "pattern" XZ: X = LV95 E, Z = -LV95 N (world axes), both modulo 9.6 km.
/// </para>
/// </summary>
public static class WaveSpectrum
{
    public const int Count = 6;

    /// <summary>Spatial period, metres: <c>OriginShifter.PatternPeriodM</c>.</summary>
    public const double PeriodM = 9600;

    /// <summary>Time period, seconds: every wave repeats exactly after it.</summary>
    public const double LoopS = 1200;

    public const double Gravity = 9.81;

    /// <summary>
    /// Horizontal over vertical amplitude (Gerstner steepness): sharp crests, flat troughs. The sum
    /// of k·A·Q stays under 0.45 at the gamey state, far from the loops at 1.
    /// </summary>
    public const float Chop = 1.2f;

    /// <summary>One wave: its numbers, and its amplitude at calm and at gamey.</summary>
    public readonly record struct Wave(int Nx, int Nz, int Cycles, float Phase, float AmpCalm, float AmpGamey, float Growth)
    {
        public double Kx => 2 * Math.PI * Nx / PeriodM;
        public double Kz => 2 * Math.PI * Nz / PeriodM;
        public double K => Math.Sqrt(Kx * Kx + Kz * Kz);
        public double Omega => 2 * Math.PI * Cycles / LoopS;
        public double LengthM => PeriodM / Math.Sqrt((double)Nx * Nx + (double)Nz * Nz);

        /// <summary>Amplitude at a sea state: calm plus a curve to gamey; long swells grow late.</summary>
        public float Amplitude(float seaState) =>
            AmpCalm + (AmpGamey - AmpCalm) * MathF.Pow(Math.Clamp(seaState, 0f, 1f), Growth);
    }

    /// <summary>
    /// The waves, longest first. Wavelengths 64, 41, 27, 17.5, 11.3 and 8.2 m, spread over ±70°
    /// round the prevailing direction. Gamey (1): about 1.25 m of summed amplitude, a 1.5-2 m swell
    /// crest to trough on a big lake; calm (0): a few centimetres, nothing long. The shortest is
    /// four of the near mesh's 2 m squares long: a wave drawn with fewer vertices aliases into a
    /// false lattice over a whole lake (seen on the Petit Lac with 4.3 m waves); shorter ripples
    /// are the shaders' shading only.
    /// Cycles: omega = sqrt(g k), rounded to whole cycles per <see cref="LoopS"/>.
    /// </summary>
    public static readonly Wave[] Waves = Build(
        (64.0, 20.0, 0.00f, 0.50f, 2.0f, 0.3f),
        (41.0, -15.0, 0.00f, 0.32f, 1.8f, 2.1f),
        (27.0, 45.0, 0.004f, 0.20f, 1.4f, 4.4f),
        (17.5, -40.0, 0.008f, 0.12f, 1.0f, 1.7f),
        (11.3, 70.0, 0.010f, 0.07f, 0.7f, 5.6f),
        (8.2, -65.0, 0.012f, 0.04f, 0.5f, 3.1f));

    private static Wave[] Build(params (double Length, double Deg, float Calm, float Gamey, float Growth, float Phase)[] specs)
    {
        var waves = new Wave[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            var (length, deg, calm, gamey, growth, phase) = specs[i];
            double cycles = PeriodM / length;
            double a = deg * Math.PI / 180;
            int nx = (int)Math.Round(cycles * Math.Cos(a)), nz = (int)Math.Round(cycles * Math.Sin(a));
            double k = 2 * Math.PI / PeriodM * Math.Sqrt((double)nx * nx + (double)nz * nz);
            int loops = (int)Math.Round(Math.Sqrt(Gravity * k) * LoopS / (2 * Math.PI));
            waves[i] = new Wave(nx, nz, loops, phase, calm, gamey, growth);
        }
        return waves;
    }

    /// <summary>The wave clock: the synchronized server time modulo <see cref="LoopS"/>.</summary>
    public static double WaveTime(double serverNow)
    {
        double t = serverNow % LoopS;
        return t < 0 ? t + LoopS : t;
    }

    /// <summary>Pattern X of an LV95 east: E modulo <see cref="PeriodM"/>.</summary>
    public static double PatternX(double lv95E) => Mod(lv95E);

    /// <summary>Pattern Z of an LV95 north: -N modulo <see cref="PeriodM"/> (world Z points south).</summary>
    public static double PatternZ(double lv95N) => Mod(-lv95N);

    private static double Mod(double v)
    {
        double m = v % PeriodM;
        return m < 0 ? m + PeriodM : m;
    }

    /// <summary>Each wave's amplitude at a sea state, into <paramref name="amp"/> (length <see cref="Count"/>).</summary>
    public static void Amplitudes(float seaState, Span<float> amp)
    {
        for (int i = 0; i < Count; i++) amp[i] = Waves[i].Amplitude(seaState);
    }

    /// <summary>
    /// The displacement of the surface point at rest at pattern (<paramref name="x"/>,
    /// <paramref name="z"/>) at wave time <paramref name="t"/>, with every amplitude times
    /// <paramref name="scale"/> (fetch x depth, 0..1): dx, dz horizontal, dy up from the still level.
    /// The shader's <c>water_wave_displace</c> is this, term for term.
    /// </summary>
    public static void Displace(double x, double z, double t, float scale, ReadOnlySpan<float> amp,
        out double dx, out double dy, out double dz)
    {
        dx = dy = dz = 0;
        if (scale <= 0f) return;
        for (int i = 0; i < Count; i++)
        {
            var w = Waves[i];
            double a = amp[i] * scale;
            if (a == 0) continue;
            double k = w.K;
            double theta = w.Kx * x + w.Kz * z - w.Omega * t + w.Phase;
            double s = Math.Sin(theta), c = Math.Cos(theta);
            double h = Chop * a;
            dx -= h * w.Kx / k * s;
            dz -= h * w.Kz / k * s;
            dy += a * c;
        }
    }

    /// <summary>
    /// The surface's normal (x, y, z, unit, y up) at the point at rest at pattern (x, z): the cross
    /// product of the displaced surface's two tangents, exact for the Gerstner sum.
    /// </summary>
    public static void Normal(double x, double z, double t, float scale, ReadOnlySpan<float> amp,
        out double nx, out double ny, out double nz)
    {
        // tangents d/dx0 = (1 + ax, bx, cx), d/dz0 = (az, bz, 1 + cz)
        double ax = 0, bx = 0, cx = 0, az = 0, bz = 0, cz = 0;
        if (scale > 0f)
            for (int i = 0; i < Count; i++)
            {
                var w = Waves[i];
                double a = amp[i] * scale;
                if (a == 0) continue;
                double k = w.K, dxn = w.Kx / k, dzn = w.Kz / k;
                double theta = w.Kx * x + w.Kz * z - w.Omega * t + w.Phase;
                double s = Math.Sin(theta), c = Math.Cos(theta);
                double h = Chop * a;
                // d(theta)/dx0 = Kx, d(theta)/dz0 = Kz
                ax -= h * dxn * c * w.Kx; bx -= a * s * w.Kx; cx -= h * dzn * c * w.Kx;
                az -= h * dxn * c * w.Kz; bz -= a * s * w.Kz; cz -= h * dzn * c * w.Kz;
            }
        // n = Tz x Tx
        double tx0 = 1 + ax, tx1 = bx, tx2 = cx;
        double tz0 = az, tz1 = bz, tz2 = 1 + cz;
        nx = tz1 * tx2 - tz2 * tx1;
        ny = tz2 * tx0 - tz0 * tx2;
        nz = tz0 * tx1 - tz1 * tx0;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12) { nx = 0; ny = 1; nz = 0; return; }
        nx /= len; ny /= len; nz /= len;
    }

    /// <summary>The velocity (m/s) of the water particle at rest at pattern (x, z): d/dt of <see cref="Displace"/>.</summary>
    public static void Velocity(double x, double z, double t, float scale, ReadOnlySpan<float> amp,
        out double vx, out double vy, out double vz)
    {
        vx = vy = vz = 0;
        if (scale <= 0f) return;
        for (int i = 0; i < Count; i++)
        {
            var w = Waves[i];
            double a = amp[i] * scale;
            if (a == 0) continue;
            double k = w.K;
            double theta = w.Kx * x + w.Kz * z - w.Omega * t + w.Phase;
            double s = Math.Sin(theta), c = Math.Cos(theta);
            double h = Chop * a;
            // d(theta)/dt = -omega
            vx += h * w.Kx / k * c * w.Omega;
            vz += h * w.Kz / k * c * w.Omega;
            vy += a * s * w.Omega;
        }
    }

    /// <summary>
    /// The height of the surface above the still level over pattern (x, z), where the Gerstner
    /// sum moved some other rest point to: finds that point by fixed-point iteration (the
    /// horizontal motion is small next to the wavelengths, so four steps land within millimetres).
    /// </summary>
    public static double HeightAt(double x, double z, double t, float scale, ReadOnlySpan<float> amp, int iterations = 4)
    {
        double x0 = x, z0 = z, dx, dy, dz;
        for (int i = 0; i < iterations; i++)
        {
            Displace(x0, z0, t, scale, amp, out dx, out _, out dz);
            x0 = x - dx;
            z0 = z - dz;
        }
        Displace(x0, z0, t, scale, amp, out _, out dy, out _);
        return dy;
    }
}
