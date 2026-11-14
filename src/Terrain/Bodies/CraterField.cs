using System;
using System.Collections.Generic;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>
/// The craters a body's data is too coarse to hold (#1849, the cratered profile of docs/plans/planets.md section 5):
/// the Moon's LOLA heights stop at 118 m, so every bowl smaller than ~300 m is generated here, and so are the rims and
/// the soft old dishes between them.
///
/// <para>
/// <b>On the sphere, not on a face</b>: craters are seeded in a 3D grid of cells round the body (one grid a size bin),
/// each kept only near the surface, so a point finds the same craters whatever face, leaf or level asks: no seam at a
/// face's edge, no stretch at its corners. Everything is a function of (seed, point): the server and every client make
/// the same ground.
/// </para>
///
/// <para>
/// <b>How many</b>: the lunar maria's equilibrium, N(&gt;D) ≈ 0.154·D⁻² craters per km² with D in km (Neukum et al. 2001,
/// the steady state small craters reach): ~15 a km² over 100 m, ~1,500 over 10 m. Size bins double from
/// <see cref="MinDiameterM"/> to <see cref="MaxDiameterM"/>; a bin's craters are drawn from that power law within it.
/// </para>
///
/// <para>
/// <b>The shape</b>: a simple crater's parabolic bowl, its depth a share of its diameter (0.2 fresh, down to 0.04 for an
/// old one worn by the craters on it), a raised rim (4 % of D fresh), and ejecta falling off as (R/r)³ to two radii.
/// The age is seeded per crater; the young are few.
/// </para>
/// </summary>
public sealed class CraterField
{
    /// <summary>The smallest crater drawn, metres: a leaf's cells are ~1.3 m, so under 2 m a bowl is not a shape.</summary>
    public const double MinDiameterM = 2.5;

    /// <summary>The largest generated, metres: from here up the data (LOLA's 118 m, the pyramid's 167 m cells) has them.</summary>
    public const double MaxDiameterM = 320;

    /// <summary>The equilibrium's constant: N(&gt;D) = K·D⁻² craters a km², D in km.</summary>
    public const double EquilibriumK = 0.154;

    private readonly ulong _seed;
    private readonly double _radius;
    private readonly Bin[] _bins;

    private readonly record struct Bin(double DMin, double DMax, double Cell, double PerCell);

    /// <summary>A crater: its centre (body-fixed, on the reference sphere), radius, bowl depth and rim height, metres.</summary>
    public readonly record struct Crater(double X, double Y, double Z, double Radius, double Depth, double Rim);

    /// <param name="seed">The body's own (its id): two bodies never share a crater.</param>
    /// <param name="radius">The body's reference radius, metres.</param>
    /// <param name="density">A share of the equilibrium (1 on the maria; the highlands are older, saturated, the same).</param>
    public CraterField(ulong seed, double radius, double density = 1)
    {
        _seed = seed;
        _radius = radius;
        var bins = new List<Bin>();
        for (double d = MinDiameterM; d < MaxDiameterM; d *= 2)
        {
            double dMax = Math.Min(d * 2, MaxDiameterM);
            // craters a km² in [d, dMax): K (d⁻² − dMax⁻²), d in km
            double perKm2 = density * EquilibriumK * (Math.Pow(d / 1000, -2) - Math.Pow(dMax / 1000, -2));
            // a cell three of the bin's largest across: a crater and its ejecta stay within the neighbours
            double cell = 3 * dMax;
            bins.Add(new Bin(d, dMax, cell, perKm2 * cell * cell / 1e6));
        }
        _bins = bins.ToArray();
    }

    /// <summary>How many size bins, for tests and the log.</summary>
    public int Bins => _bins.Length;

    /// <summary>
    /// The relief the craters make at a point (body-fixed, metres; only its direction counts), added to the data's
    /// ground: a bowl's floor negative, a rim positive, 0 far from any. <paramref name="largest"/> leaves out bins over it
    /// (a coarse level that has them in its data already); <paramref name="smallest"/> leaves out the bins under it (a
    /// coarse mesh that cannot hold them: the medium ground's 16 m cells keep the bowls from 40 m).
    /// </summary>
    public double At(double x, double y, double z, double largest = MaxDiameterM, double smallest = 0)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        double px = x / len * _radius, py = y / len * _radius, pz = z / len * _radius;
        double h = 0;
        for (int b = 0; b < _bins.Length; b++)
        {
            var bin = _bins[b];
            if (bin.DMin > largest) break;
            if (bin.DMin < smallest) continue;
            long cx = (long)Math.Floor(px / bin.Cell), cy = (long)Math.Floor(py / bin.Cell), cz = (long)Math.Floor(pz / bin.Cell);
            for (long i = cx - 1; i <= cx + 1; i++)
                for (long j = cy - 1; j <= cy + 1; j++)
                    for (long k = cz - 1; k <= cz + 1; k++)
                        h += CellRelief(b, bin, i, j, k, px, py, pz);
        }
        return h;
    }

    /// <summary>
    /// The craters of <paramref name="minDiameter"/> and up whose centre is within <paramref name="reach"/> of a point
    /// (body-fixed, on or near the surface): the boulders round fresh rims (<see cref="BoulderField"/>).
    /// </summary>
    public IEnumerable<Crater> Near(double x, double y, double z, double reach, double minDiameter)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        double px = x / len * _radius, py = y / len * _radius, pz = z / len * _radius;
        for (int b = 0; b < _bins.Length; b++)
        {
            var bin = _bins[b];
            if (bin.DMax <= minDiameter) continue;
            int span = (int)Math.Ceiling(reach / bin.Cell) + 1;
            long cx = (long)Math.Floor(px / bin.Cell), cy = (long)Math.Floor(py / bin.Cell), cz = (long)Math.Floor(pz / bin.Cell);
            for (long i = cx - span; i <= cx + span; i++)
                for (long j = cy - span; j <= cy + span; j++)
                    for (long k = cz - span; k <= cz + span; k++)
                        foreach (var c in CratersIn(b, i, j, k))
                        {
                            if (2 * c.Radius < minDiameter) continue;
                            double dx = c.X - px, dy = c.Y - py, dz = c.Z - pz;
                            if (dx * dx + dy * dy + dz * dz <= reach * reach) yield return c;
                        }
        }
    }

    /// <summary>A crater's freshness, 0 old .. 1 just made: its depth over its diameter against the fresh 0.2.</summary>
    public static double Freshness(in Crater c) => Math.Clamp((c.Depth / (2 * c.Radius) - 0.04) / 0.16, 0, 1);

    /// <summary>The craters a cell holds (a few at most), for tests and the boulders round fresh rims.</summary>
    public IEnumerable<Crater> CratersIn(int bin, long i, long j, long k)
    {
        var b = _bins[bin];
        ulong h = Hash(_seed, (ulong)bin, (ulong)i, (ulong)j, (ulong)k);
        int n = Count(b.PerCell, ref h);
        for (int c = 0; c < n; c++)
            if (Make(b, i, j, k, ref h) is { } crater) yield return crater;
    }

    private double CellRelief(int bin, Bin b, long i, long j, long k, double px, double py, double pz)
    {
        ulong h = Hash(_seed, (ulong)bin, (ulong)i, (ulong)j, (ulong)k);
        int n = Count(b.PerCell, ref h);
        double sum = 0;
        for (int c = 0; c < n; c++)
        {
            if (Make(b, i, j, k, ref h) is not { } crater) continue;
            double dx = px - crater.X, dy = py - crater.Y, dz = pz - crater.Z;
            double r2 = dx * dx + dy * dy + dz * dz, R = crater.Radius;
            if (r2 >= 4 * R * R) continue;
            sum += Profile(Math.Sqrt(r2) / R, crater.Depth, crater.Rim);
        }
        return sum;
    }

    /// <summary>
    /// A simple crater's relief at <paramref name="s"/> radii from its centre: a parabolic bowl from the floor (−depth
    /// below the old ground, plus the rim) up to the rim at 1, then ejecta as (1/s)³, gone smoothly at 2.
    /// </summary>
    public static double Profile(double s, double depth, double rim)
    {
        if (s < 1) return rim - (depth + rim) * (1 - s * s);
        if (s >= 2) return 0;
        double fade = 1 - (s - 1) * (s - 1);   // 1 at the rim, 0 at two radii, flat there
        return rim / (s * s * s) * fade;
    }

    /// <summary>How many craters a cell holds: the mean's whole part, and one more with the fraction's chance.</summary>
    private static int Count(double mean, ref ulong h)
    {
        int whole = (int)mean;
        return whole + (Next(ref h) < mean - whole ? 1 : 0);
    }

    /// <summary>One crater of a cell: a point in the cell kept only near the sphere (so the surface gets a fair share), its size by the power law, its age.</summary>
    private Crater? Make(Bin b, long i, long j, long k, ref ulong h)
    {
        double x = (i + Next(ref h)) * b.Cell, y = (j + Next(ref h)) * b.Cell, z = (k + Next(ref h)) * b.Cell;
        double u = Next(ref h), age = Next(ref h);
        double len = Math.Sqrt(x * x + y * y + z * z);
        // a cell straddles the surface: a point within half a cell of it stands for that patch of ground
        if (Math.Abs(len - _radius) > b.Cell / 2) return null;
        double s = _radius / len;
        // N(>D) ∝ D⁻²: D = DMin / sqrt(1 − u (1 − (DMin/DMax)²))
        double q = b.DMin / b.DMax;
        double d = b.DMin / Math.Sqrt(1 - u * (1 - q * q));
        // most craters are old: depth 0.04-0.2 D, rim 0.01-0.04 D, the fresh ones the few with age near 0
        double fresh = Math.Pow(1 - age, 6);
        return new Crater(x * s, y * s, z * s, d / 2, d * (0.04 + 0.16 * fresh), d * (0.008 + 0.032 * fresh));
    }

    // a SplitMix64 stream from the cell's key: the same numbers on every peer and run
    private static ulong Hash(ulong a, ulong b, ulong c, ulong d, ulong e)
    {
        ulong h = a * 0x9E3779B97F4A7C15UL;
        h = Mix(h ^ b); h = Mix(h ^ c); h = Mix(h ^ d); h = Mix(h ^ e);
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
}
