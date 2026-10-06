namespace UnitSport.Interiors;

/// <summary>
/// One elevator's state (#557): the ride it is on, or last made, as from-floor, to-floor and the
/// server time it set off. Everything a peer draws or decides follows from that and the clock:
/// the doors on <see cref="From"/> close, the cabin travels (a teleport, nobody sees it move), the
/// doors on <see cref="To"/> open. Idle is a ride from a floor to itself: its doors stand open.
/// Plain C#, no Godot, so the timing is unit-tested (tier 0).
/// </summary>
public readonly record struct LiftRide(int From, int To, double Start)
{
    /// <summary>Seconds the doors take to close, and to open.</summary>
    public const double Close = 0.9, Open = 0.9;
    /// <summary>A ride's travel: a start and stop, and a stretch a floor.</summary>
    public const double Base = 1.4, PerFloor = 0.6;

    /// <summary>A cabin standing at <paramref name="floor"/> with its doors open.</summary>
    public static LiftRide Idle(int floor) => new(floor, floor, 0);

    public bool Rides => From != To;
    public double Travel => Rides ? Base + PerFloor * Math.Abs(To - From) : 0;
    /// <summary>When the cabin is at <see cref="To"/> (its riders are moved then), doors still shut.</summary>
    public double Arrive => Rides ? Start + Close + Travel : Start;
    /// <summary>When the doors on <see cref="To"/> are fully open again.</summary>
    public double Done => Rides ? Arrive + Open : Start;

    /// <summary>Whether a ride is still under way: a call now waits for it.</summary>
    public bool Busy(double now) => Rides && now < Done;

    /// <summary>Whether the cabin is travelling, its doors shut on every floor.</summary>
    public bool Travelling(double now) => Rides && now >= Start + Close && now < Arrive;

    /// <summary>The floor the cabin is at (or last left).</summary>
    public int At(double now) => Rides && now < Arrive ? From : To;

    /// <summary>How open the doors on <paramref name="floor"/> stand, 0 shut to 1 open.</summary>
    public float Doors(double now, int floor)
    {
        if (!Rides || now >= Done) return floor == To ? 1f : 0f;
        if (now < Start) return floor == From ? 1f : 0f;
        if (now < Start + Close) return floor == From ? (float)(1 - (now - Start) / Close) : 0f;
        if (now < Arrive) return 0f;
        return floor == To ? (float)((now - Arrive) / Open) : 0f;
    }
}
