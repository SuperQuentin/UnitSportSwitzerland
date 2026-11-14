using System;
using UnitSport.Core;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>
/// A body's relief below its data, by its family (#1853, #1854; the profiles of docs/plans/planets.md section 5), added
/// to the data and the craters by <see cref="BodyHeights"/>: Mars' and Titan's dunes, Europa's double ridges and cracks,
/// Ganymede's grooved lanes, Io's and Venus' lava flows. A function of the point and the body's seed (every peer the same
/// ground), in metres over the data, of a few to a hundred-odd metres, at wavelengths the data cannot hold (Mars' MOLA at
/// 463 m, the moons flat). Body-fixed points; only their direction and the body's radius count.
/// </summary>
public abstract class SurfaceProfile
{
    /// <summary>The relief at a body-fixed point, metres; <paramref name="smallest"/> leaves out features shorter than about it (a coarse mesh).</summary>
    public abstract double At(double x, double y, double z, double smallest = 0);

    /// <summary>The profile of a body, or null for the data and the craters alone.</summary>
    public static SurfaceProfile? For(BodyId body, double radius) => body switch
    {
        // Mars: transverse dunes 120 m apart, 5 m high, in fields over a third of the ground (the dark basaltic sand seas)
        BodyId.Mars => new Dunes(radius, 1853, wavelength: 120, height: 5, share: 0.33),
        // Titan: the equatorial sand seas (within 30° of the equator), longitudinal dunes 2 km apart and ~100 m high
        BodyId.Titan => new Dunes(radius, 1854, wavelength: 2_000, height: 90, share: 0.6, equatorial: 30),
        BodyId.Europa => new Ridges(radius, 1857),
        BodyId.Ganymede => new Grooves(radius, 1858),
        BodyId.Io => new LavaFlows(radius, 1859, step: 12),
        BodyId.Venus => new LavaFlows(radius, 1860, step: 8),
        _ => null,
    };

    protected readonly double Radius;
    protected readonly ulong Seed;

    protected SurfaceProfile(double radius, ulong seed)
    {
        Radius = radius;
        Seed = seed;
    }

    /// <summary>The point on the reference sphere under a body-fixed one.</summary>
    protected (double X, double Y, double Z) OnSphere(double x, double y, double z)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        return (x / len * Radius, y / len * Radius, z / len * Radius);
    }

    /// <summary>Smooth value noise in 3D, −1..1, at <paramref name="scale"/> metres (quintic between hashed lattice values).</summary>
    protected double Noise(double x, double y, double z, double scale, ulong salt)
    {
        x /= scale; y /= scale; z /= scale;
        long ix = (long)Math.Floor(x), iy = (long)Math.Floor(y), iz = (long)Math.Floor(z);
        double fx = x - ix, fy = y - iy, fz = z - iz;
        double ux = Fade(fx), uy = Fade(fy), uz = Fade(fz);
        double sum = 0;
        for (int c = 0; c < 8; c++)
        {
            int dx = c & 1, dy = c >> 1 & 1, dz = c >> 2 & 1;
            double w = (dx == 1 ? ux : 1 - ux) * (dy == 1 ? uy : 1 - uy) * (dz == 1 ? uz : 1 - uz);
            sum += w * Lattice(ix + dx, iy + dy, iz + dz, salt);
        }
        return sum;
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    /// <summary>A lattice point's value, −1..1.</summary>
    protected double Lattice(long i, long j, long k, ulong salt)
    {
        ulong h = Mix(Seed * 0x9E3779B97F4A7C15UL ^ salt);
        h = Mix(h ^ (ulong)i); h = Mix(h ^ (ulong)j); h = Mix(h ^ (ulong)k);
        return (h >> 11) * (2.0 / (1UL << 53)) - 1;
    }

    protected static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    protected static double Smooth(double e0, double e1, double v)
    {
        double t = Math.Clamp((v - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Waves of one direction per cell of <paramref name="cell"/> metres, blended over the eight cells round a point: within a
    /// cell the crests are straight (a constant direction, its phase from the body's centre: no stretch), and across cells
    /// the fields cross-fade as real ones do. <paramref name="wave"/> maps the phase (0..1 along one wavelength) to −1..1.
    /// </summary>
    protected double Waves(double px, double py, double pz, double cell, double wavelength, ulong salt, Func<double, double> wave)
    {
        double x = px / cell - 0.5, y = py / cell - 0.5, z = pz / cell - 0.5;
        long ix = (long)Math.Floor(x), iy = (long)Math.Floor(y), iz = (long)Math.Floor(z);
        double fx = Fade(x - ix), fy = Fade(y - iy), fz = Fade(z - iz);
        double len = Math.Sqrt(px * px + py * py + pz * pz);
        double ux = px / len, uy = py / len, uz = pz / len;
        double sum = 0;
        for (int c = 0; c < 8; c++)
        {
            int dx = c & 1, dy = c >> 1 & 1, dz = c >> 2 & 1;
            double w = (dx == 1 ? fx : 1 - fx) * (dy == 1 ? fy : 1 - fy) * (dz == 1 ? fz : 1 - fz);
            if (w <= 0) continue;
            // the cell's crest normal: a random direction made tangent at the point (so the crests stand on the ground)
            double ax = Lattice(ix + dx, iy + dy, iz + dz, salt), ay = Lattice(ix + dx, iy + dy, iz + dz, salt + 1), az = Lattice(ix + dx, iy + dy, iz + dz, salt + 2);
            double along = ax * ux + ay * uy + az * uz;
            ax -= along * ux; ay -= along * uy; az -= along * uz;
            double al = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (al < 1e-6) continue;
            double phase = (px * ax + py * ay + pz * az) / al / wavelength + Lattice(ix + dx, iy + dy, iz + dz, salt + 3);
            sum += w * wave(phase - Math.Floor(phase));
        }
        return sum;
    }

    /// <summary>Dune fields: an asymmetric wave (a long gentle stoss slope, a short steep lee) where the sand mask is on.</summary>
    public sealed class Dunes : SurfaceProfile
    {
        private readonly double _wavelength, _height, _share, _equatorial;

        public Dunes(double radius, ulong seed, double wavelength, double height, double share, double equatorial = 90) : base(radius, seed)
        {
            _wavelength = wavelength;
            _height = height;
            _share = share;
            _equatorial = equatorial;
        }

        /// <summary>How much sand there is at a point, 0..1: patches of fields over about <c>share</c> of the ground.</summary>
        public double Sand(double x, double y, double z)
        {
            var (px, py, pz) = OnSphere(x, y, z);
            double band = 1;
            if (_equatorial < 90)
            {
                double lat = Math.Abs(Math.Asin(Math.Clamp(pz / Radius, -1, 1))) * 180 / Math.PI;
                band = 1 - Smooth(_equatorial - 5, _equatorial + 5, lat);
            }
            // a noise of ±0.4 or so: the threshold that leaves `share` of it above
            double n = Noise(px, py, pz, _wavelength * 60, 11) + 0.5 * Noise(px, py, pz, _wavelength * 20, 12);
            double cut = 0.45 - 0.9 * _share;
            return band * Smooth(cut, cut + 0.15, n);
        }

        public override double At(double x, double y, double z, double smallest = 0)
        {
            if (_wavelength < 2.5 * smallest) return 0;
            double sand = Sand(x, y, z);
            if (sand <= 0) return 0;
            var (px, py, pz) = OnSphere(x, y, z);
            double crest = Waves(px, py, pz, _wavelength * 250, _wavelength, 21, t => t < 0.75 ? 2 * Smooth(0, 0.75, t) - 1 : 1 - 2 * Smooth(0.75, 1, t));
            return _height * sand * 0.5 * (crest + 1);
        }
    }

    /// <summary>Europa's double ridges along a lineament network, and narrow cracks between them.</summary>
    public sealed class Ridges : SurfaceProfile
    {
        public const double Height = 150, CrackDepth = 20;

        public Ridges(double radius, ulong seed) : base(radius, seed) { }

        public override double At(double x, double y, double z, double smallest = 0)
        {
            var (px, py, pz) = OnSphere(x, y, z);
            double h = 0;
            // two families of lineaments: the zero lines of a noise, a band of ±w round each a double ridge (a trough on the line)
            h += Double(Noise(px, py, pz, 4_000, 31), 0.15, Height);
            h += Double(Noise(px, py, pz, 2_200, 32), 0.12, Height * 0.4);
            if (smallest < 20)
            {
                double crack = Math.Abs(Noise(px, py, pz, 900, 33));
                if (crack < 0.03) h -= CrackDepth * (1 - crack / 0.03);
            }
            return h;
        }

        private static double Double(double n, double w, double height)
        {
            double d = Math.Abs(n) / w;
            if (d >= 1) return 0;
            double s = Math.Sin(Math.PI * d);
            return height * s * s;
        }
    }

    /// <summary>Ganymede's grooved terrain: lanes of parallel grooves a few km apart, between the dark cratered plains.</summary>
    public sealed class Grooves : SurfaceProfile
    {
        public const double Wavelength = 2_500, Height = 120;

        public Grooves(double radius, ulong seed) : base(radius, seed) { }

        public override double At(double x, double y, double z, double smallest = 0)
        {
            var (px, py, pz) = OnSphere(x, y, z);
            double lane = Smooth(-0.05, 0.15, Noise(px, py, pz, 25_000, 41));
            if (lane <= 0) return 0;
            double wave = Waves(px, py, pz, 40_000, Wavelength, 42, t => Math.Sin(2 * Math.PI * t));
            return Height * 0.5 * lane * wave;
        }
    }

    /// <summary>Lava plains: lobate flow fronts as low steps (Io's, Venus' basalt), over a gentle swell.</summary>
    public sealed class LavaFlows : SurfaceProfile
    {
        private readonly double _step;

        public LavaFlows(double radius, ulong seed, double step) : base(radius, seed) => _step = step;

        public override double At(double x, double y, double z, double smallest = 0)
        {
            var (px, py, pz) = OnSphere(x, y, z);
            double f = Noise(px, py, pz, 6_000, 51) + 0.45 * Noise(px, py, pz, 1_800, 52) + 0.2 * Noise(px, py, pz, 500, 53);
            // terraces: each flow front a smooth step of `_step` metres
            double t = (f + 2) * 3;
            double whole = Math.Floor(t), frac = t - whole;
            return _step * (whole + Smooth(0.75, 1, frac)) - _step * 6;
        }
    }
}
