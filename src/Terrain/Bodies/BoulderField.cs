using System;
using System.Collections.Generic;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>
/// Boulders round fresh craters (#1849, the cratered profile's scatter, docs/plans/planets.md section 5): an impact that
/// digs through the regolith throws blocks out over its rim, and they stay until the small craters grind them down. So
/// the young craters of the <see cref="CraterField"/> (<see cref="CraterField.Freshness"/> over <see cref="FreshFrom"/>)
/// big enough to reach the bedrock (<see cref="MinCraterM"/>) get a few boulders each, in the ejecta ring from the rim
/// to two radii, most small, the largest a fifteenth of the crater. Everything is a function of the crater: every peer
/// makes the same blocks. Body-fixed places on the reference sphere (the ground's height is laid under them), metres.
/// </summary>
public sealed class BoulderField
{
    /// <summary>The smallest crater with boulders, metres: smaller ones stay in the regolith (the Moon's few metres).</summary>
    public const double MinCraterM = 10;

    /// <summary>How fresh a crater must be to keep its boulders (0..1, see <see cref="CraterField.Freshness"/>).</summary>
    public const double FreshFrom = 0.55;

    /// <summary>The smallest boulder, metres of radius, and the boulders a crater gets per metre of its diameter (fresh).</summary>
    public const double MinRadiusM = 0.3, PerMetre = 0.3;

    /// <summary>A boulder: its centre's place on the reference sphere (body-fixed), its radius and a seed for its shape.</summary>
    public readonly record struct Boulder(double X, double Y, double Z, double Radius, ulong Shape);

    private readonly CraterField _craters;
    private readonly ulong _seed;

    public BoulderField(CraterField craters, ulong seed)
    {
        _craters = craters;
        _seed = seed;
    }

    /// <summary>The boulders within <paramref name="reach"/> of a body-fixed point, into <paramref name="into"/> (cleared first).</summary>
    public List<Boulder> Near(double x, double y, double z, double reach, List<Boulder>? into = null)
    {
        var list = into ?? new List<Boulder>();
        list.Clear();
        double len = Math.Sqrt(x * x + y * y + z * z);
        // a crater's boulders lie within two of its radii: those up to the largest crater's reach out can throw in here
        foreach (var c in _craters.Near(x, y, z, reach + CraterField.MaxDiameterM, MinCraterM))
        {
            double fresh = CraterField.Freshness(c);
            if (fresh < FreshFrom) continue;
            double r = Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z);
            // the crater's own frame on the sphere: its up, and two ways across
            double cx = c.X / r, cy = c.Y / r, cz = c.Z / r;
            double ax, ay, az;
            if (Math.Abs(cz) < 0.9) { ax = -cy; ay = cx; az = 0; } else { ax = 0; ay = -cz; az = cy; }
            double al = Math.Sqrt(ax * ax + ay * ay + az * az);
            ax /= al; ay /= al; az /= al;
            double bx = cy * az - cz * ay, by = cz * ax - cx * az, bz = cx * ay - cy * ax;
            ulong h = Mix(_seed ^ Mix((ulong)BitConverter.DoubleToInt64Bits(c.X) ^ Mix((ulong)BitConverter.DoubleToInt64Bits(c.Y) ^ (ulong)BitConverter.DoubleToInt64Bits(c.Z))));
            double d = 2 * c.Radius;
            int n = (int)(d * PerMetre * (fresh - FreshFrom) / (1 - FreshFrom) + Next(ref h));
            double largest = Math.Max(MinRadiusM * 1.5, d / 15);
            for (int i = 0; i < n; i++)
            {
                // out over the rim, thinning with the distance: from the rim to two radii
                double s = 1 / Math.Sqrt(1 - Next(ref h) * 0.75);
                double angle = Next(ref h) * 2 * Math.PI;
                double off = s * c.Radius / r;   // radians on the sphere
                double ca = Math.Cos(angle) * off, sa = Math.Sin(angle) * off;
                double px = cx + ax * ca + bx * sa, py = cy + ay * ca + by * sa, pz = cz + az * ca + bz * sa;
                double pl = Math.Sqrt(px * px + py * py + pz * pz);
                px = px / pl * r; py = py / pl * r; pz = pz / pl * r;
                // most small: N(>R) ∝ R^-2 between the smallest and the crater's largest
                double q = MinRadiusM / largest;
                double radius = MinRadiusM / Math.Sqrt(1 - Next(ref h) * (1 - q * q));
                ulong shape = Mix(h ^ 0xB0D1E5);
                double dx = px - x / len * r, dy = py - y / len * r, dz = pz - z / len * r;
                if (dx * dx + dy * dy + dz * dz <= (reach + radius) * (reach + radius)) list.Add(new Boulder(px, py, pz, radius, shape));
            }
        }
        return list;
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
