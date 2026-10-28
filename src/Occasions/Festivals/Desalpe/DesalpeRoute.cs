// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-desalpe.md).

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>
/// The way the herd is driven (#1269 part 7): a polyline from the alp down to the fair, in metres east (X) and north (Y) of the fair
/// (the festival site's middle). It is a pure function of a seed and a length, so the server, every client and the decoration of the
/// alp sign and the fair ring agree with nothing sent. Routing over the real road network is a later target (the plan's 4-12 km
/// <c>RoadNetwork.Path</c>); today the route is a seeded valley road of a few hundred metres with gentle bends.
/// </summary>
public sealed class DesalpeRoute
{
    private readonly (double X, double Y)[] _pts;
    private readonly double[] _cum;

    private DesalpeRoute((double X, double Y)[] pts)
    {
        _pts = pts;
        _cum = new double[pts.Length];
        for (int i = 1; i < pts.Length; i++)
            _cum[i] = _cum[i - 1] + Math.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X) + (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y));
    }

    public IReadOnlyList<(double X, double Y)> Points => _pts;

    /// <summary>The route's length in metres.</summary>
    public double Length => _cum[^1];

    public (double X, double Y) Start => _pts[0];

    public (double X, double Y) End => _pts[^1];

    /// <summary>Uniform [0, 1) from two integers: the same on every peer.</summary>
    public static double Unit(uint a, uint b)
    {
        uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA6Bu;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFFFF) / (double)0x1000000;
    }

    /// <summary>
    /// A route that ends at the fair (0, 0) and starts roughly 85% of <paramref name="length"/> away at a bearing the seed picks, bending
    /// left and right of the straight line; its length is exactly <paramref name="length"/>.
    /// </summary>
    public static DesalpeRoute Plan(int seed, double length)
    {
        double bearing = Unit((uint)seed, 1) * Math.Tau, d = length * 0.88, amp = d * 0.10;
        double ux = Math.Cos(bearing), uy = Math.Sin(bearing), nx = -uy, ny = ux;
        const int Segments = 7;
        var pts = new (double X, double Y)[Segments + 1];
        for (int k = 0; k <= Segments; k++)
        {
            double along = d * (1 - k / (double)Segments);
            double side = k is 0 or Segments ? 0 : amp * (k % 2 == 0 ? 1 : -1) * (0.5 + Unit((uint)seed, (uint)(10 + k)));
            pts[k] = (ux * along + nx * side, uy * along + ny * side);
        }
        // bends make it longer than the straight line: scale it to the length asked for (about the fair)
        var raw = new DesalpeRoute(pts);
        double scale = length / raw.Length;
        for (int i = 0; i < pts.Length; i++) pts[i] = (pts[i].X * scale, pts[i].Y * scale);
        return new DesalpeRoute(pts);
    }

    /// <summary>The point <paramref name="s"/> metres along the route (clamped to its ends).</summary>
    public (double X, double Y) At(double s)
    {
        s = Math.Clamp(s, 0, Length);
        int i = Segment(s);
        double f = (s - _cum[i]) / Math.Max(_cum[i + 1] - _cum[i], 1e-9);
        return (_pts[i].X + (_pts[i + 1].X - _pts[i].X) * f, _pts[i].Y + (_pts[i + 1].Y - _pts[i].Y) * f);
    }

    /// <summary>The unit direction of travel at <paramref name="s"/>.</summary>
    public (double X, double Y) Tangent(double s)
    {
        int i = Segment(Math.Clamp(s, 0, Length));
        double dx = _pts[i + 1].X - _pts[i].X, dy = _pts[i + 1].Y - _pts[i].Y, l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? (1, 0) : (dx / l, dy / l);
    }

    private int Segment(double s)
    {
        int i = 0;
        while (i < _pts.Length - 2 && s > _cum[i + 1]) i++;
        return i;
    }

    /// <summary>The arc position nearest to a point, and how far the point is from the route.</summary>
    public double Project(double x, double y, out double distance)
    {
        double best = double.MaxValue, bestS = 0;
        for (int i = 0; i < _pts.Length - 1; i++)
        {
            double ax = _pts[i].X, ay = _pts[i].Y, bx = _pts[i + 1].X - ax, by = _pts[i + 1].Y - ay;
            double len2 = bx * bx + by * by, t = len2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * bx + (y - ay) * by) / len2, 0, 1);
            double px = ax + bx * t, py = ay + by * t, d2 = (x - px) * (x - px) + (y - py) * (y - py);
            if (d2 >= best) continue;
            best = d2;
            bestS = _cum[i] + t * Math.Sqrt(len2);
        }
        distance = Math.Sqrt(best);
        return bestS;
    }
}
