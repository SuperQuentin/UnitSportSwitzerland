using System;
using System.Collections.Generic;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>
/// What covers another body's ground below its data (#2016, the cover column of docs/plans/planets.md section 5): which
/// family it is (<see cref="Kind"/>), and per point of the near ground what the classes there are, as shares the ground
/// shader turns into colour over the photo's (<c>shaders/common/body_cover.gdshaderinc</c>):
/// <list type="bullet">
/// <item><b>Rays</b>: the bright ejecta of fresh craters, the floor and the blanket round the rim, then rays out to
/// <see cref="RayReach"/> radii, by the crater's freshness (a function of the <see cref="CraterField"/>: every peer the same).</item>
/// <item><b>Sand</b>: Mars' and Titan's dark dune sand, where their dune fields are (<see cref="SurfaceProfile.Dunes.Sand"/>,
/// the same mask the dunes' relief is made by).</item>
/// </list>
/// The rest is the shader's own, on every tile of the body, from where the fragment is (so it never ends at a square's
/// edge): the regolith's mottling, Mars' ochre dust, Io's sulphur, and the polar ice by latitude and season
/// (<see cref="IceEdges"/>). Body-fixed points, metres.
/// </summary>
public static class BodyCover
{
    /// <summary>A body's cover family (its value is the shader's <c>cover_kind</c>).</summary>
    public enum Kind
    {
        /// <summary>No cover of its own (the Earth: its leaves; the giants: no ground).</summary>
        None = 0,
        /// <summary>The Moon, Mercury, Callisto, Phobos, Deimos: grey regolith, bright rays round fresh craters.</summary>
        Regolith = 1,
        /// <summary>Mars: ochre dust, dark basalt sand, polar caps by latitude and season.</summary>
        Mars = 2,
        /// <summary>Europa, Ganymede: water ice, fresh ice bright round young craters; Ganymede's polar frost.</summary>
        Ice = 3,
        /// <summary>Io: sulphur's yellows, whites and reds.</summary>
        Sulphur = 4,
        /// <summary>Venus: dark basalt flows.</summary>
        Basalt = 5,
        /// <summary>Titan: dark organic sand seas between its pale ice highlands.</summary>
        Titan = 6,
    }

    /// <summary>A fresh crater's rays reach this many of its radii.</summary>
    public const double RayReach = 5;

    /// <summary>How fresh a crater must be to keep its bright ejecta (0..1, <see cref="CraterField.Freshness"/>): the young ~8 %.</summary>
    public const double RaysFrom = 0.6;

    public static Kind For(BodyId body) => body switch
    {
        BodyId.Moon or BodyId.Mercury or BodyId.Callisto or BodyId.Phobos or BodyId.Deimos => Kind.Regolith,
        BodyId.Mars => Kind.Mars,
        BodyId.Europa or BodyId.Ganymede => Kind.Ice,
        BodyId.Io => Kind.Sulphur,
        BodyId.Venus => Kind.Basalt,
        BodyId.Titan => Kind.Titan,
        _ => Kind.None,
    };

    /// <summary>
    /// Where the polar ice ends on each hemisphere, degrees of latitude (90: none), for the sun standing over
    /// <paramref name="subsolarLatDeg"/> (the season). Mars: the seasonal CO₂ caps reach ~55°N and ~52°S in each
    /// hemisphere's winter and shrink to the residual caps (~80°N, ~85°S) in its summer (James et al. 1992, the MGS TES
    /// cap edges); linear in the sun's latitude between (25.2° the planet's tilt). Ganymede: its bright polar frost
    /// from ~45° (Voyager, Galileo), all year. Every other body none.
    /// </summary>
    public static (double North, double South) IceEdges(BodyId body, double subsolarLatDeg)
    {
        switch (body)
        {
            case BodyId.Mars:
                double s = Math.Clamp(subsolarLatDeg / 25.2, -1, 1);
                // northern summer (s 1): the north cap small, the south large
                return (67.5 + 12.5 * s, 68.5 - 16.5 * s);
            case BodyId.Ganymede:
                return (45, 45);
            default:
                return (90, 90);
        }
    }

    /// <summary>A point's cover shares, 0..1 each: the rays' brightness, the sand.</summary>
    public readonly record struct Sample(float Rays, float Sand);

    /// <summary>
    /// The fresh craters round a square, ready to answer per vertex (<see cref="At"/>): those of
    /// <paramref name="smallest"/> metres and up within <paramref name="reach"/> of the point, bucketed by where their rays
    /// reach. Built once a square, on its worker.
    /// </summary>
    public sealed class Region
    {
        private const double Bucket = 64;
        private readonly CraterField.Crater[] _craters;
        private readonly double[] _fresh;
        private readonly Dictionary<(long, long, long), List<int>> _buckets = new();
        private readonly SurfaceProfile.Dunes? _dunes;

        /// <summary>How many fresh craters the square's rays come from.</summary>
        public int Craters => _craters.Length;

        public Region(CraterField? craters, SurfaceProfile? profile, EcefPos at, double reach, double smallest)
        {
            _dunes = profile as SurfaceProfile.Dunes;
            var list = new List<CraterField.Crater>();
            var fresh = new List<double>();
            if (craters != null)
                foreach (var c in craters.Near(at.X, at.Y, at.Z, reach + RayReach * CraterField.MaxDiameterM / 2, Math.Max(smallest, CraterField.MinDiameterM)))
                {
                    double f = CraterField.Freshness(c);
                    if (f < RaysFrom || 2 * c.Radius < smallest) continue;
                    list.Add(c);
                    fresh.Add((f - RaysFrom) / (1 - RaysFrom));
                }
            _craters = list.ToArray();
            _fresh = fresh.ToArray();
            for (int i = 0; i < _craters.Length; i++)
            {
                var c = _craters[i];
                double r = RayReach * c.Radius;
                long x0 = (long)Math.Floor((c.X - r) / Bucket), x1 = (long)Math.Floor((c.X + r) / Bucket);
                long y0 = (long)Math.Floor((c.Y - r) / Bucket), y1 = (long)Math.Floor((c.Y + r) / Bucket);
                long z0 = (long)Math.Floor((c.Z - r) / Bucket), z1 = (long)Math.Floor((c.Z + r) / Bucket);
                for (long x = x0; x <= x1; x++)
                    for (long y = y0; y <= y1; y++)
                        for (long z = z0; z <= z1; z++)
                        {
                            if (!_buckets.TryGetValue((x, y, z), out var b)) _buckets[(x, y, z)] = b = new List<int>(2);
                            b.Add(i);
                        }
            }
        }

        /// <summary>
        /// The cover at a point (a unit normal or any body-fixed point; scaled onto the craters' sphere by
        /// <paramref name="radius"/>). <paramref name="raysFrom"/> leaves out the rays of craters under it (a coarser square's).
        /// Thread-safe: read only.
        /// </summary>
        public Sample At(EcefPos n, double radius, double raysFrom = 0)
        {
            double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            double px = n.X / len * radius, py = n.Y / len * radius, pz = n.Z / len * radius;
            double rays = 0;
            if (_buckets.TryGetValue(((long)Math.Floor(px / Bucket), (long)Math.Floor(py / Bucket), (long)Math.Floor(pz / Bucket)), out var b))
                foreach (int i in b)
                {
                    if (2 * _craters[i].Radius < raysFrom) continue;
                    rays = Math.Max(rays, Ray(_craters[i], _fresh[i], px, py, pz));
                }
            double sand = _dunes?.Sand(px, py, pz) ?? 0;
            return new Sample((float)rays, (float)sand);
        }
    }

    /// <summary>
    /// A fresh crater's brightness at a point on the sphere: its floor and walls (fresh rock, 0.7), the continuous ejecta
    /// blanket to 1.6 radii (bright, falling off), then rays to <see cref="RayReach"/> radii, eight to fourteen of them,
    /// narrowing and fading outward; all by its freshness (0..1 over <see cref="RaysFrom"/>).
    /// </summary>
    public static double Ray(in CraterField.Crater c, double fresh, double px, double py, double pz)
    {
        double dx = px - c.X, dy = py - c.Y, dz = pz - c.Z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double s = d / c.Radius;
        if (s >= RayReach) return 0;
        if (s < 1) return fresh * 0.7;
        double blanket = s < 1.6 ? 1 - Smooth(1.0, 1.6, s) * 0.6 : 0.4 * (1 - Smooth(1.6, 2.2, s));
        // the rays: an angle round the crater's own up, a seeded count and phase
        double cl = Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z);
        double ux = c.X / cl, uy = c.Y / cl, uz = c.Z / cl;
        double ax, ay, az;
        if (Math.Abs(uz) < 0.9) { ax = -uy; ay = ux; az = 0; } else { ax = 0; ay = -uz; az = uy; }
        double al = Math.Sqrt(ax * ax + ay * ay + az * az);
        ax /= al; ay /= al; az /= al;
        double bx = uy * az - uz * ay, by = uz * ax - ux * az, bz = ux * ay - uy * ax;
        double angle = Math.Atan2(dx * bx + dy * by + dz * bz, dx * ax + dy * ay + dz * az);
        ulong h = (ulong)BitConverter.DoubleToInt64Bits(c.X * 0.37 + c.Y * 0.61 + c.Z);
        h = Mix(h);
        int count = 8 + (int)(h % 7);
        double phase = (h >> 8 & 0xFFFF) / 65535.0 * 2 * Math.PI, wobble = (h >> 24 & 0xFFFF) / 65535.0 * 2 * Math.PI;
        double wave = 0.5 + 0.5 * Math.Cos(count * angle + phase + 0.8 * Math.Sin(3 * angle + wobble));
        // narrower outward: the ray's core only, far out
        double narrow = 0.55 + 0.35 * Smooth(1.2, RayReach, s);
        double ray = Smooth(narrow, 1.0, wave) * (1 - Smooth(1.4, RayReach, s));
        return fresh * Math.Max(blanket, ray * 0.8);
    }

    private static double Smooth(double e0, double e1, double v)
    {
        double t = Math.Clamp((v - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
