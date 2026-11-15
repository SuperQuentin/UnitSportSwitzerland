using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>What a stamp does to the ground inside it.</summary>
public enum StampMode : byte
{
    /// <summary>The generated craters fade out; the data's own slope stays (a historic lander on its real ground).</summary>
    Calm,

    /// <summary>The ground goes to one height (the data's at the centre, plus an offset): a colony's plateau, a pad.</summary>
    Level,

    /// <summary>
    /// A fresh crater of radius <see cref="SurfaceStamp.RadiusM"/> added to the ground (an S-IVB's impact, #1939): a
    /// parabolic bowl a fifth of its diameter deep, a rim 4 % of it, ejecta to two radii; the generated craters calmed
    /// inside it.
    /// </summary>
    Crater,
}

/// <summary>
/// One place where the ground is made ready for something standing on it (#1939, docs/plans/moon-surface.md section 2):
/// a disc round <see cref="Centre"/>, or with <see cref="End"/> a strip from one to the other (a runway). Inside
/// <see cref="RadiusM"/> the stamp holds fully; across <see cref="FalloffM"/> it eases back to the natural ground.
/// </summary>
/// <param name="Centre">The unit normal (body-fixed) of the disc's centre, or of the strip's first end.</param>
/// <param name="End">The strip's other end (a unit normal), or null for a disc.</param>
/// <param name="RadiusM">The disc's radius, or the strip's half width, metres.</param>
/// <param name="FalloffM">The band past it where the stamp fades out, metres.</param>
/// <param name="Mode">Calm or Level.</param>
/// <param name="OffsetM">Level only: metres over the data's height at the centre (a strip: at each end, linear between).</param>
public readonly record struct SurfaceStamp(EcefPos Centre, EcefPos? End, double RadiusM, double FalloffM, StampMode Mode, double OffsetM = 0);

/// <summary>
/// The stamps on a body's ground (#1939): a short list, each checked with one dot product against its bounding cap, so a
/// point far from every site pays a few multiplications. Applied inside <see cref="BodyHeights"/>, the one height
/// function every peer and every ground square asks: the walker, the rover, the shuttle and the buildings all meet the
/// same ground.
///
/// <para>
/// Outside every stamp's falloff the height is unchanged to the bit. Strips are measured in the tangent plane at their
/// middle (a 3 km runway on the Moon bends 0.6 m over its length: within its falloff's ease).
/// </para>
/// </summary>
public sealed class SurfaceStamps
{
    private readonly Entry[] _entries;

    private sealed class Entry
    {
        public SurfaceStamp Stamp;
        public EcefPos Mid;          // the cap's centre (unit)
        public double CosReach;      // dot(n, Mid) under this: outside the falloff
        public double HalfLength;    // strips: metres from the middle to each end, along the axis
        public EcefPos Axis, Side;   // strips: the tangent along and across at the middle
        public double Radius;        // the body's
        public double LevelA = double.NaN, LevelB = double.NaN;
    }

    /// <param name="stamps">The stamps, in the order they apply (a later one wins where two overlap).</param>
    /// <param name="bodyRadius">The body's reference radius, metres.</param>
    public SurfaceStamps(IEnumerable<SurfaceStamp> stamps, double bodyRadius)
    {
        var list = new List<Entry>();
        foreach (var s in stamps)
        {
            var e = new Entry { Stamp = s, Radius = bodyRadius };
            double reach = s.Mode == StampMode.Crater ? 2 * s.RadiusM : s.RadiusM + s.FalloffM;
            if (s.End is { } end)
            {
                e.Mid = Unit(s.Centre + end);
                double angle = Math.Acos(Math.Clamp(Dot(s.Centre, end), -1, 1));
                e.HalfLength = angle * bodyRadius / 2;
                var along = end - s.Centre;
                along = Unit(Sub(along, Scale(e.Mid, Dot(along, e.Mid))));
                e.Axis = along;
                e.Side = Cross(e.Mid, along);
                reach += e.HalfLength;
            }
            else e.Mid = Unit(s.Centre);
            // the cap a little wider than the reach: the chord and the tangent plane round it
            e.CosReach = Math.Cos(Math.Min(Math.PI, reach * 1.01 / bodyRadius + 1e-9));
            list.Add(e);
        }
        _entries = list.ToArray();
    }

    /// <summary>How many stamps.</summary>
    public int Count => _entries.Length;

    /// <summary>
    /// The ground at a unit normal with the stamps applied: <paramref name="data"/> the data's height there,
    /// <paramref name="craters"/> the generated relief there, <paramref name="dataAt"/> the data's height anywhere (a
    /// Level stamp's height is the data's at its centre, read once and kept).
    /// </summary>
    public double Apply(EcefPos n, double data, double craters, Func<EcefPos, double> dataAt)
    {
        double h = data + craters;
        foreach (var e in _entries)
        {
            if (Dot(n, e.Mid) < e.CosReach) continue;
            if (e.Stamp.Mode == StampMode.Crater)
            {
                double r = Math.Acos(Math.Clamp(Dot(n, e.Mid), -1, 1)) * e.Radius;
                h += Bowl(r / e.Stamp.RadiusM, 2 * e.Stamp.RadiusM) - craters * Math.Clamp(2 - r / e.Stamp.RadiusM, 0, 1);
                continue;
            }
            double t = Weight(e, n, out double along);
            if (t <= 0) continue;
            if (e.Stamp.Mode == StampMode.Calm)
                h -= craters * t;
            else
                h += (LevelAt(e, along, dataAt) - h) * t;
        }
        return h;
    }

    /// <summary>Whether any stamp touches a unit normal (its falloff included).</summary>
    public bool Touches(EcefPos n)
    {
        foreach (var e in _entries)
            if (Dot(n, e.Mid) >= e.CosReach && (e.Stamp.Mode == StampMode.Crater || Weight(e, n, out _) > 0)) return true;
        return false;
    }

    /// <summary>1 inside the stamp, smoothly to 0 across its falloff; <paramref name="along"/> a strip's place along it (0..1).</summary>
    private static double Weight(Entry e, EcefPos n, out double along)
    {
        var s = e.Stamp;
        double d;
        along = 0;
        if (s.End == null)
        {
            // great-circle distance from the centre
            d = Math.Acos(Math.Clamp(Dot(n, e.Mid), -1, 1)) * e.Radius;
        }
        else
        {
            // the tangent plane at the middle: the point projected, its distance to the segment
            double k = Dot(n, e.Mid);
            if (k <= 0) return 0;
            double x = Dot(n, e.Axis) / k * e.Radius, y = Dot(n, e.Side) / k * e.Radius;
            double cx = Math.Clamp(x, -e.HalfLength, e.HalfLength);
            d = Math.Sqrt((x - cx) * (x - cx) + y * y);
            along = e.HalfLength > 0 ? (cx + e.HalfLength) / (2 * e.HalfLength) : 0;
        }
        if (d <= s.RadiusM) return 1;
        if (s.FalloffM <= 0 || d >= s.RadiusM + s.FalloffM) return 0;
        double t = 1 - (d - s.RadiusM) / s.FalloffM;
        return t * t * (3 - 2 * t);
    }

    /// <summary>A fresh crater's relief at <paramref name="x"/> radii from its centre, <paramref name="d"/> its diameter.</summary>
    public static double Bowl(double x, double d)
    {
        double depth = 0.2 * d, rim = 0.04 * d;
        if (x < 1) return (depth + rim) * x * x - depth;
        if (x < 2) return rim * (Math.Pow(1 / x, 3) - 0.125) / 0.875;
        return 0;
    }

    private static double LevelAt(Entry e, double along, Func<EcefPos, double> dataAt)
    {
        // read once a stamp; two threads may both read it, they get the same height
        if (double.IsNaN(e.LevelA)) e.LevelA = dataAt(e.Stamp.Centre) + e.Stamp.OffsetM;
        if (e.Stamp.End is not { } end) return e.LevelA;
        if (double.IsNaN(e.LevelB)) e.LevelB = dataAt(end) + e.Stamp.OffsetM;
        return e.LevelA + (e.LevelB - e.LevelA) * along;
    }

    private static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static EcefPos Scale(EcefPos a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    private static EcefPos Sub(EcefPos a, EcefPos b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static EcefPos Cross(EcefPos a, EcefPos b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static EcefPos Unit(EcefPos a)
    {
        double l = a.Length;
        return new EcefPos(a.X / l, a.Y / l, a.Z / l);
    }
}
