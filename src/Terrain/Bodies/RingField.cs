using System;
using System.Collections.Generic;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>
/// A ring's ice chunks (#1854, Saturn's rings): every chunk is a function of (seed, cell), so every client makes the same
/// ones wherever it stands, and only those near the camera are ever made. The ring's space is cut into cells of
/// <see cref="CellM"/> along the radius and the arc (a row's arc cells the whole circle at its radius), each the whole
/// slab thick; a cell holds a count by the ring's opacity at its radius (the ring map's alpha: the dense B ring, the
/// Cassini division nearly empty), each chunk somewhere in the cell and the slab, its size by a power law (most small,
/// a few house-sized), its shape a seed. Body-fixed coordinates (Saturn's: Z its pole, the rings in its equator), metres.
/// </summary>
public sealed class RingField
{
    /// <summary>A cell's side along the radius and the arc, metres.</summary>
    public const double CellM = 100;

    /// <summary>
    /// The smallest and largest chunk radius, metres. The real particles are mostly centimetres to a few metres; these are
    /// a game's (a field one sees and flies through, its colliders worth having), the law's slope kept.
    /// </summary>
    public const double MinRadiusM = 1, MaxRadiusM = 30;

    /// <summary>The size law's slope: N(>R) ∝ R^-Slope (flatter than the craters': more big ones to see).</summary>
    public const double Slope = 1.5;

    /// <summary>A chunk: its centre (body-fixed), its radius, and a seed for its shape, spin and tint.</summary>
    public readonly record struct Chunk(double X, double Y, double Z, double Radius, ulong Shape);

    private readonly ulong _seed;
    private readonly double _inner, _outer, _half, _perCell;
    private readonly Func<double, double> _opacity;

    /// <param name="seed">The ring's own (the body's id): two rings never share a chunk.</param>
    /// <param name="inner">The ring's inner radius, metres.</param>
    /// <param name="outer">Its outer radius.</param>
    /// <param name="halfThickness">Half its thickness, metres: chunks stand within it of the equator.</param>
    /// <param name="opacity">The ring's opacity at a radius, 0 to 1 (its map's alpha).</param>
    /// <param name="perCell">The chunks a cell holds where the opacity is 1.</param>
    public RingField(ulong seed, double inner, double outer, double halfThickness, Func<double, double> opacity, double perCell = 6)
    {
        _seed = seed;
        _inner = inner;
        _outer = outer;
        _half = halfThickness;
        _opacity = opacity;
        _perCell = perCell;
    }

    public double Inner => _inner;
    public double Outer => _outer;
    public double HalfThickness => _half;

    /// <summary>The ring's arc cells at a row (the whole circle at its middle radius).</summary>
    private int ArcCells(int row) => Math.Max(1, (int)Math.Floor(2 * Math.PI * (_inner + (row + 0.5) * CellM) / CellM));

    /// <summary>
    /// The chunks within <paramref name="reach"/> of a body-fixed point, into <paramref name="into"/> (cleared first): only
    /// the cells that can hold one are visited.
    /// </summary>
    public List<Chunk> Near(double x, double y, double z, double reach, List<Chunk>? into = null)
    {
        var list = into ?? new List<Chunk>();
        list.Clear();
        double r = Math.Sqrt(x * x + y * y);
        double maxSize = MaxRadiusM;
        if (Math.Abs(z) > _half + reach + maxSize || r < _inner - reach - maxSize || r > _outer + reach + maxSize) return list;
        int rows = (int)Math.Ceiling((_outer - _inner) / CellM);
        int row0 = Math.Max(0, (int)Math.Floor((r - reach - _inner) / CellM)), row1 = Math.Min(rows - 1, (int)Math.Floor((r + reach - _inner) / CellM));
        double theta = Math.Atan2(y, x);
        if (theta < 0) theta += 2 * Math.PI;
        double reach2 = (reach + maxSize) * (reach + maxSize);
        for (int row = row0; row <= row1; row++)
        {
            int n = ArcCells(row);
            double rowR = Math.Max(_inner + row * CellM, 1);
            double dTheta = Math.Min(Math.PI, (reach + CellM) / rowR);
            int a0 = (int)Math.Floor((theta - dTheta) / (2 * Math.PI) * n), a1 = (int)Math.Floor((theta + dTheta) / (2 * Math.PI) * n);
            if (a1 - a0 >= n) { a0 = 0; a1 = n - 1; }
            double opacity = Math.Clamp(_opacity(_inner + (row + 0.5) * CellM), 0, 1);
            if (opacity <= 0) continue;
            for (int a = a0; a <= a1; a++)
            {
                int cell = ((a % n) + n) % n;
                ulong h = Hash(_seed, (ulong)row, (ulong)cell);
                double mean = _perCell * opacity;
                int count = (int)mean + (Next(ref h) < mean - (int)mean ? 1 : 0);
                for (int k = 0; k < count; k++)
                {
                    double cr = _inner + (row + Next(ref h)) * CellM;
                    double ct = (cell + Next(ref h)) / n * 2 * Math.PI;
                    double cz = (Next(ref h) * 2 - 1) * _half;
                    // N(>R) ∝ R^-Slope: most chunks small, a few the size of a house
                    double q = Math.Pow(MinRadiusM / MaxRadiusM, Slope);
                    double radius = MinRadiusM / Math.Pow(1 - Next(ref h) * (1 - q), 1 / Slope);
                    ulong shape = Mix(h ^ 0xC0FFEE);
                    double cx = cr * Math.Cos(ct), cy = cr * Math.Sin(ct);
                    double dx = cx - x, dy = cy - y, dz = cz - z;
                    if (dx * dx + dy * dy + dz * dz <= reach2) list.Add(new Chunk(cx, cy, cz, radius, shape));
                }
            }
        }
        return list;
    }

    // a SplitMix64 stream from the cell's key: the same numbers on every peer and run (as CraterField's)
    private static ulong Hash(ulong a, ulong b, ulong c)
    {
        ulong h = a * 0x9E3779B97F4A7C15UL;
        h = Mix(h ^ b); h = Mix(h ^ (c + 0x632BE59BD9B4E019UL));
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
