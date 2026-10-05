using Godot;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The cargo plane's line over the region and its first circle (#207), in zone metres (east, north of the centre).
/// Rebuilt by every peer from the match state (<see cref="BrState.Seed"/>, <see cref="BrState.FlightStart"/>,
/// <see cref="BrState.FlightAlt"/>) and the shared clock, like the zone: nothing is sent while it flies.
/// <para>
/// A straight line at a seeded angle, offset up to 30 % of the first circle's radius from its centre
/// (the circle is sized for the field, #447). The doors open where the plane is over both that circle
/// and the square, so nobody jumps before the zone; the plane starts <see cref="Lead"/> metres before
/// that point. They close <see cref="DoorMargin"/> before it leaves; whoever is still aboard then is
/// pushed out. The zone's clock (<see cref="BrState.Started"/>) starts when the doors close.
/// </para>
/// </summary>
public readonly struct BrFlight
{
    /// <summary>Metres flown before the doors open: time to look around and to stream the ground in.</summary>
    public const float Lead = 1500f;
    /// <summary>Metres before the far edge at which the doors close, so the last ones out still land inside.</summary>
    public const float DoorMargin = 300f;
    /// <summary>Metres flown on after the doors close, before the plane is gone.</summary>
    public const float Trail = 4000f;
    /// <summary>
    /// Cruise, m/s (290 km/h); faster on a test pace (<c>--brpace</c>), at most twice: four times flew
    /// 320 m/s across real terrain, far more tile streaming than a match ever asks for.
    /// </summary>
    public const float Cruise = 80f;
    /// <summary>Height above the highest ground under the line, and the lowest it ever flies (m above sea).</summary>
    public const float Clearance = 650f, MinAltitude = 1200f;

    /// <summary>Where the plane starts, and its heading (unit, zone metres).</summary>
    public readonly Vector2 From, Dir;
    /// <summary>Metres along the line: the doors open, the doors close, the plane is gone.</summary>
    public readonly float Opens, Closes, Gone;
    public readonly float Speed, Altitude;
    public readonly double Start;

    /// <param name="centre">The first circle's centre, zone metres (<see cref="ZoneSchedule.CentreOf"/> 0).</param>
    /// <param name="radius">The first circle's radius; 0 for the full circle of an unknown field.</param>
    public BrFlight(int seed, float side, float pace, double start, float altitude, Vector2 centre = default, float radius = 0f)
    {
        if (radius <= 0f) (centre, radius) = (Vector2.Zero, ZoneSchedule.FirstRadius(side, 0));
        var (angle, offset) = Line(seed, radius);
        Dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        var through = centre + new Vector2(-Dir.Y, Dir.X) * offset;
        // over the square and over the circle (the offset is across the line, so the circle is ±half its chord)
        var (sIn, sOut) = Chord(through, Dir, side * 0.5f);
        float halfChord = Mathf.Sqrt(Math.Max(0f, radius * radius - offset * offset));
        float tIn = Math.Max(sIn, -halfChord), tOut = Math.Min(sOut, halfChord);
        From = through + Dir * (tIn - Lead);
        Opens = Lead;
        // a short chord over a small circle keeps most of itself open
        Closes = Lead + Math.Max(0f, tOut - tIn - Math.Min(DoorMargin, (tOut - tIn) * 0.2f));
        Gone = Closes + DoorMargin + Trail;
        Speed = Cruise * Math.Clamp(1f / Math.Max(pace, 0.01f), 1f, 2f);
        Altitude = altitude;
        Start = start;
    }

    public BrFlight(BrState s) : this(s.Seed, s.Side, s.Pace, s.FlightStart, s.FlightAlt, s.Zone().CentreOf(0), s.Zone().RadiusOf(0)) { }

    /// <summary>The seeded angle (radians, from east towards north) and offset from the circle's centre (m).</summary>
    public static (float Angle, float Offset) Line(int seed, float radius)
    {
        var rng = new Random(seed ^ 0x2c1b3c6d);
        float angle = (float)(rng.NextDouble() * Math.Tau);
        float offset = (float)(rng.NextDouble() * 2 - 1) * radius * 0.3f;
        return (angle, offset);
    }

    /// <summary>Where a line (point, unit direction) enters and leaves a square of half side h about the origin.</summary>
    private static (float In, float Out) Chord(Vector2 p, Vector2 d, float h)
    {
        float tIn = float.NegativeInfinity, tOut = float.PositiveInfinity;
        for (int axis = 0; axis < 2; axis++)
        {
            float pa = p[axis], da = d[axis];
            if (Math.Abs(da) < 1e-6f) continue;   // parallel to this pair of sides, inside them (|offset| < h)
            float a = (-h - pa) / da, b = (h - pa) / da;
            tIn = Math.Max(tIn, Math.Min(a, b));
            tOut = Math.Min(tOut, Math.Max(a, b));
        }
        return (tIn, tOut);
    }

    /// <summary>Server clock at which the doors open, close, and the plane is gone.</summary>
    public double OpensAt => Start + Opens / Speed;
    public double ClosesAt => Start + Closes / Speed;
    public double GoneAt => Start + Gone / Speed;

    /// <summary>Metres flown at a server time (clamped to the flight).</summary>
    public float Flown(double now) => Mathf.Clamp((float)((now - Start) * Speed), 0f, Gone);

    /// <summary>The plane's zone position at a server time.</summary>
    public Vector2 At(double now) => From + Dir * Flown(now);

    /// <summary>The doors are open: over the first circle.</summary>
    public bool DoorsOpen(double now) => now >= OpensAt && now < ClosesAt;

    /// <summary>Where the line crosses the first circle: door-open point to door-close point.</summary>
    public (Vector2 A, Vector2 B) JumpStretch => (From + Dir * Opens, From + Dir * Closes);

    /// <summary>The plane's altitude: the highest ground under its line plus <see cref="Clearance"/>.</summary>
    public static float AltitudeOver(BrFlight line, Func<Vector2, double> ground)
    {
        double top = 0;
        for (float t = 0; t <= line.Gone; t += 100f) top = Math.Max(top, ground(line.From + line.Dir * t));
        return Math.Max(MinAltitude, (float)top + Clearance);
    }
}
