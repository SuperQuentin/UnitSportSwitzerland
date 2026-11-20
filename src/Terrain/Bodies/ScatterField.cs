using System;
using System.Collections.Generic;
using UnitSport.Core;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>
/// What lies on another body's ground beyond the boulders round fresh craters (#2016, the scatter column of
/// docs/plans/planets.md section 5): Mars' rocks by a thermal-inertia guess, the regolith's pebbles, Europa's and Ganymede's
/// ice blocks, Titan's rounded ice cobbles. One field a kind of stone (<see cref="For"/>), each a function of (seed, point):
/// every peer lays the same stones.
///
/// <para>
/// <b>On the sphere, as the craters</b> (<see cref="CraterField"/>): candidates seeded in a 3D grid of
/// <see cref="Spec.Cell"/>-metre cells round the body, kept near the surface, so no face, leaf or square has a seam. A
/// candidate stays with the field's local share (<see cref="Spec.Share"/>: Mars' rock abundance, the chaos patches of the
/// ice), and takes a radius by N(&gt;r) ∝ r⁻² between the field's smallest and largest (most small).
/// </para>
/// </summary>
public sealed class ScatterField
{
    /// <summary>The shape family a stone is drawn with (and whether it is solid).</summary>
    public enum Kind
    {
        /// <summary>Angular rock: Mars' rocks, the regolith's pebbles.</summary>
        Rock,
        /// <summary>A faceted block of water ice: Europa's and Ganymede's.</summary>
        IceBlock,
        /// <summary>A rounded ice cobble: Titan's (Huygens' landing site).</summary>
        Cobble,
    }

    /// <summary>A stone: its centre's place on the reference sphere (body-fixed), its radius, a seed for its shape, its kind.</summary>
    public readonly record struct Stone(double X, double Y, double Z, double Radius, ulong Shape, Kind Kind);

    /// <summary>
    /// A field's numbers: its kind and name, the radii (metres), the stones a square metre at a full share, the seeding
    /// cell, how far round the camera it is laid and how often again, the smallest solid radius and how far round solid
    /// (0: drawn only), and its local share (0..1, at a point on the reference sphere, body-fixed metres).
    /// </summary>
    public sealed record Spec(Kind Kind, string Name, double MinRadius, double MaxRadius, double PerM2, double Cell,
        double Reach, double MoveM, double SolidFrom, double SolidReach, Func<double, double, double, double> Share);

    public Spec Of { get; }

    private readonly ulong _seed;
    private readonly double _radius;

    public ScatterField(ulong seed, double radius, Spec spec)
    {
        _seed = seed;
        _radius = radius;
        Of = spec;
    }

    /// <summary>
    /// The fields of a body (none for most): Mars' rocks and pebbles, the cratered bodies' pebbles, the icy moons' blocks,
    /// Titan's cobbles. Io and Venus none (their profiles list none: lava plains).
    /// </summary>
    public static ScatterField[] For(BodyId body, double radius, SurfaceProfile? profile)
    {
        ulong seed = (ulong)body * 104_729 + 2016;
        var dunes = profile as SurfaceProfile.Dunes;
        var noise = new Noise(radius, seed);
        switch (body)
        {
            case BodyId.Mars:
                // the rock abundance from a thermal-inertia guess (ThermalInertia): the dusty mantles bare, the rocky plains strewn
                double Rocky(double x, double y, double z) => RockAbundance(ThermalInertia(x, y, z, dunes, noise));
                return new[]
                {
                    new ScatterField(seed, radius, new Spec(Kind.Rock, "rocks", 0.12, 1.4, 0.35, 6, 180, 50, 0.3, 60, Rocky)),
                    new ScatterField(seed + 1, radius, new Spec(Kind.Rock, "pebbles", 0.03, 0.12, 3.0, 2, 30, 10, 0, 0,
                        (x, y, z) => 0.3 + 0.7 * Rocky(x, y, z))),
                };
            case BodyId.Moon or BodyId.Mercury or BodyId.Callisto or BodyId.Phobos or BodyId.Deimos:
                // the regolith's clods and small rocks: thin everywhere, patchy (the fresh craters' boulders are BoulderField's)
                return new[]
                {
                    new ScatterField(seed, radius, new Spec(Kind.Rock, "pebbles", 0.03, 0.15, 0.5, 3, 35, 12, 0, 0,
                        (x, y, z) => 0.35 + 0.65 * Smooth(-0.3, 0.5, noise.At(x, y, z, 60, 1)))),
                };
            case BodyId.Europa or BodyId.Ganymede:
                // ice blocks in the chaos patches (a third of the ground), a few between them
                return new[]
                {
                    new ScatterField(seed, radius, new Spec(Kind.IceBlock, "ice blocks", 0.25, 2.5, 0.04, 10, 260, 70, 0.3, 90,
                        (x, y, z) => 0.08 + 0.92 * Smooth(0.0, 0.3, noise.At(x, y, z, 900, 2)))),
                };
            case BodyId.Titan:
                // Huygens' rounded cobbles, 3-15 cm, on the pale plains; none in the dune sand
                return new[]
                {
                    new ScatterField(seed, radius, new Spec(Kind.Cobble, "cobbles", 0.03, 0.12, 1.5, 2, 30, 10, 0, 0,
                        (x, y, z) => 1 - (dunes?.Sand(x, y, z) ?? 0))),
                };
            default:
                return Array.Empty<ScatterField>();
        }
    }

    /// <summary>
    /// A guess of Mars' thermal inertia at a point, J m⁻² K⁻¹ s⁻½ (no global map ships; the MGS TES map's range): the dust
    /// mantles (a large-scale low over ~a third of the ground, Tharsis' and Arabia's) at 40-150, the dune sand at ~250, the
    /// rocky plains and the crater floors 300-700. Smooth: a field patch is kilometres across.
    /// </summary>
    public static double ThermalInertia(double x, double y, double z, SurfaceProfile.Dunes? dunes, Noise noise)
    {
        double mantle = Smooth(-0.15, 0.25, noise.At(x, y, z, 40_000, 7) + 0.5 * noise.At(x, y, z, 12_000, 8));
        double rocky = 0.5 + 0.5 * noise.At(x, y, z, 3_000, 9);
        double ti = 120 + mantle * (220 + 380 * rocky);
        double sand = dunes?.Sand(x, y, z) ?? 0;
        return ti + (250 - ti) * sand;
    }

    /// <summary>
    /// The share of the field's full rock count at a thermal inertia (the more inertia, the more rock: Christensen 1986's
    /// rock abundance rising from ~0 under 150 to its most past ~600); sand (~250) nearly bare.
    /// </summary>
    public static double RockAbundance(double ti) => Smooth(160, 650, ti);

    /// <summary>The stones within <paramref name="reach"/> of a body-fixed point, into <paramref name="into"/> (cleared first).</summary>
    public List<Stone> Near(double x, double y, double z, double reach, List<Stone>? into = null)
    {
        var list = into ?? new List<Stone>();
        list.Clear();
        var s = Of;
        double len = Math.Sqrt(x * x + y * y + z * z);
        double px = x / len * _radius, py = y / len * _radius, pz = z / len * _radius;
        double cell = s.Cell;
        int span = (int)Math.Ceiling(reach / cell) + 1;
        long cx = (long)Math.Floor(px / cell), cy = (long)Math.Floor(py / cell), cz = (long)Math.Floor(pz / cell);
        double perCell = s.PerM2 * cell * cell;
        double q = s.MinRadius / s.MaxRadius;
        for (long i = cx - span; i <= cx + span; i++)
            for (long j = cy - span; j <= cy + span; j++)
                for (long k = cz - span; k <= cz + span; k++)
                {
                    // only the cells the surface crosses: the shell is a cell thick
                    double mx = (i + 0.5) * cell, my = (j + 0.5) * cell, mz = (k + 0.5) * cell;
                    if (Math.Abs(Math.Sqrt(mx * mx + my * my + mz * mz) - _radius) > cell) continue;
                    ulong h = Hash(_seed, (ulong)i, (ulong)j, (ulong)k);
                    int whole = (int)perCell;
                    int n = whole + (Next(ref h) < perCell - whole ? 1 : 0);
                    for (int c = 0; c < n; c++)
                    {
                        double ox = (i + Next(ref h)) * cell, oy = (j + Next(ref h)) * cell, oz = (k + Next(ref h)) * cell;
                        double u = Next(ref h), keep = Next(ref h);
                        ulong shape = Mix(h ^ 0x5CA77E2);
                        double ol = Math.Sqrt(ox * ox + oy * oy + oz * oz);
                        // a cell straddles the surface: a point within half a cell of it stands for that patch
                        if (Math.Abs(ol - _radius) > cell / 2) continue;
                        double f = _radius / ol;
                        ox *= f; oy *= f; oz *= f;
                        double dx = ox - px, dy = oy - py, dz = oz - pz;
                        if (dx * dx + dy * dy + dz * dz > reach * reach) continue;
                        if (keep >= s.Share(ox, oy, oz)) continue;
                        double r = s.MinRadius / Math.Sqrt(1 - u * (1 - q * q));
                        list.Add(new Stone(ox, oy, oz, r, shape, s.Kind));
                    }
                }
        return list;
    }

    private static double Smooth(double e0, double e1, double v)
    {
        double t = Math.Clamp((v - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static ulong Hash(ulong a, ulong b, ulong c, ulong d)
    {
        ulong h = a * 0x9E3779B97F4A7C15UL;
        h = Mix(h ^ b); h = Mix(h ^ c); h = Mix(h ^ d);
        return h;
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static double Next(ref ulong h)
    {
        h += 0x9E3779B97F4A7C15UL;
        return (Mix(h) >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>Smooth value noise on a body, −1..1 (as <see cref="SurfaceProfile"/>'s), for the fields' shares.</summary>
    public sealed class Noise
    {
        private readonly double _radius;
        private readonly ulong _seed;

        public Noise(double radius, ulong seed)
        {
            _radius = radius;
            _seed = seed;
        }

        /// <summary>The noise at a body-fixed point (put on the sphere), at <paramref name="scale"/> metres.</summary>
        public double At(double x, double y, double z, double scale, ulong salt)
        {
            double len = Math.Sqrt(x * x + y * y + z * z);
            x = x / len * _radius / scale; y = y / len * _radius / scale; z = z / len * _radius / scale;
            long ix = (long)Math.Floor(x), iy = (long)Math.Floor(y), iz = (long)Math.Floor(z);
            double fx = Fade(x - ix), fy = Fade(y - iy), fz = Fade(z - iz);
            double sum = 0;
            for (int c = 0; c < 8; c++)
            {
                int dx = c & 1, dy = c >> 1 & 1, dz = c >> 2 & 1;
                double w = (dx == 1 ? fx : 1 - fx) * (dy == 1 ? fy : 1 - fy) * (dz == 1 ? fz : 1 - fz);
                ulong h = Mix(_seed * 0x9E3779B97F4A7C15UL ^ salt);
                h = Mix(h ^ (ulong)(ix + dx)); h = Mix(h ^ (ulong)(iy + dy)); h = Mix(h ^ (ulong)(iz + dz));
                sum += w * ((h >> 11) * (2.0 / (1UL << 53)) - 1);
            }
            return sum;
        }

        private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    }
}
