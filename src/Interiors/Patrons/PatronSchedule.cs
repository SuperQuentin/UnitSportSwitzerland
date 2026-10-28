namespace UnitSport.Interiors.Patrons;

/// <summary>Whose opening hours a spot keeps (<see cref="PatronSchedule.Busy"/>).</summary>
public enum PatronVenue { Beiz, Pizzeria, Cafe, FastFood, Bar, Pub, Club, Bowling, Arcade, Cinema }

/// <summary>What a patron does on its spot: how long a stay lasts and how the figure moves.</summary>
public enum PatronActivity { Dine, Coffee, Snack, Drink, Watch, Arcade, Queue, Settee, Pool, Darts, TableFootball, Dance }

/// <summary>
/// When a venue's spots are taken (#1244): pure arithmetic on the shared clock, so every peer sees the same tables
/// full with nothing sent. Time is cut, per spot, into <b>windows</b> as long as the spot's stay
/// (<see cref="WindowOf"/>, seconds of the shared clock, not of the game day: a 24-minute day would turn a dinner
/// into a minute and a half); a window is taken with the venue's <see cref="Busy"/> at its start, for a seeded part
/// of it, by a party of a seeded size. Busy-ness reads the game hour, so lunch fills a Beiz and midnight a club.
/// The figures' code (<see cref="VenuePatrons"/>) shows a change only while its spot is out of view.
/// </summary>
public static class PatronSchedule
{
    /// <summary>A taken window's stay is this share of it, at least (the rest seeded up to all of it).</summary>
    private const double StayMin = 0.65, StaySpan = 0.35;
    /// <summary>The mean of a taken window's stay, so a spot is full about <see cref="Busy"/> of the time.</summary>
    private const double StayMean = StayMin + StaySpan / 2;

    /// <summary>
    /// How busy a venue is at an hour of the game day, 0 closed to 1 full. Peaks after
    /// <c>docs/notes/terrain/venue-dining.md</c>: a Beiz and a pizzeria at noon and in the evening, a café in the
    /// morning and mid-afternoon, a bar and a pub from the evening on, a club past midnight, bowling and an arcade in
    /// the afternoon and evening.
    /// </summary>
    public static float Busy(PatronVenue venue, double hour)
    {
        double h = ((hour % 24) + 24) % 24;
        return venue switch
        {
            PatronVenue.Beiz or PatronVenue.Pizzeria => Open(h, 11, 23.5) * (float)Math.Max(0.15,
                Math.Max(Peak(h, 12.3, 1.0), Peak(h, 19.5, 1.4))),
            PatronVenue.Cafe => Open(h, 7, 19) * (float)Math.Max(0.25, Math.Max(Peak(h, 9, 1.5), Peak(h, 15.5, 1.3) * 0.85)),
            PatronVenue.FastFood => Open(h, 10.5, 24) * (float)Math.Max(0.3, Math.Max(Peak(h, 12.3, 1.0), Peak(h, 19, 1.5) * 0.8)),
            PatronVenue.Bar or PatronVenue.Pub => Open(h, 16, 26) * (float)Math.Max(0.2, Peak(h < 12 ? h + 24 : h, 21.5, 2.5)),
            PatronVenue.Club => Open(h, 22, 28) * (float)Math.Max(0.2, Peak(h < 12 ? h + 24 : h, 25.5, 1.6)),
            PatronVenue.Bowling or PatronVenue.Arcade => Open(h, 13, 24) * (float)Math.Max(0.3, Peak(h, 20, 3)),
            PatronVenue.Cinema => Open(h, 13, 24.5) * 0.85f,
            _ => 0f,
        };
    }

    /// <summary>1 inside opening hours (<paramref name="close"/> past 24 runs on after midnight), else 0.</summary>
    private static float Open(double h, double open, double close) =>
        h >= open && h < close || close > 24 && h < close - 24 ? 1f : 0f;

    /// <summary>A bump of height 1 at <paramref name="at"/>, <paramref name="width"/> hours to either side of its half.</summary>
    private static double Peak(double h, double at, double width) => Math.Exp(-0.69 * (h - at) * (h - at) / (width * width));

    /// <summary>Seconds of the shared clock a party keeps a spot, at most (a window).</summary>
    public static double WindowOf(PatronActivity a) => a switch
    {
        PatronActivity.Dine => 600,
        PatronActivity.Coffee => 360,
        PatronActivity.Snack => 240,
        PatronActivity.Drink => 480,
        PatronActivity.Watch => 900,
        PatronActivity.Arcade => 120,
        PatronActivity.Queue => 40,
        PatronActivity.Settee => 540,
        PatronActivity.Pool or PatronActivity.Darts or PatronActivity.TableFootball => 300,
        PatronActivity.Dance => 300,
        _ => 300,
    };

    /// <summary>A spot's window at a moment: its number, its start and its length.</summary>
    public readonly record struct Window(long No, double Start, double Length);

    /// <summary>The window of spot <paramref name="key"/> at <paramref name="now"/>; each spot's windows are shifted by its key.</summary>
    public static Window WindowAt(uint key, PatronActivity a, double now)
    {
        double len = WindowOf(a), shift = Unit(key, 0x51u) * len;
        long no = (long)Math.Floor((now + shift) / len);
        return new Window(no, no * len - shift, len);
    }

    /// <summary>
    /// How many sit at spot group <paramref name="key"/> (a table, a stool, a cabinet) of <paramref name="seats"/>
    /// places at <paramref name="now"/>, and for which window (a new window is a new party). 0 when it is empty:
    /// the window not taken, before its party came or after it left, or the venue closed.
    /// <paramref name="busy"/> is the venue's <see cref="Busy"/> at a moment of the shared clock (its game hour),
    /// times anything else that keeps people away (a terrace in the cold).
    /// </summary>
    public static (int Party, long Window) Party(uint key, int seats, PatronActivity a, double now,
        Func<double, float> busy)
    {
        if (seats <= 0) return (0, 0);
        var w = WindowAt(key, a, now);
        uint k = Mix(key, (uint)w.No, (uint)(w.No >> 32));
        // taken with the venue's busy-ness as the window starts, and only while it stays open
        float atStart = busy(w.Start);
        if (Unit(k, 1) >= Math.Min(1.0, atStart / StayMean) || busy(now) <= 0) return (0, w.No);
        double stay = (StayMin + StaySpan * Unit(k, 2)) * w.Length;
        double from = w.Start + (w.Length - stay) * Unit(k, 3);
        if (now < from || now >= from + stay) return (0, w.No);
        return (PartySize(k, seats), w.No);
    }

    /// <summary>How many come together to a spot group of <paramref name="seats"/>: mostly two, sometimes alone or a few more.</summary>
    public static int PartySize(uint k, int seats)
    {
        if (seats <= 1) return 1;
        double u = Unit(k, 4);
        int n = u < 0.18 ? 1 : u < 0.68 ? 2 : u < 0.86 ? 3 : 4 + (int)(Unit(k, 5) * (seats - 3));
        return Math.Clamp(n, 1, seats);
    }

    /// <summary>
    /// Where in a group's places a party sits: the first of <paramref name="party"/> places, counted from this
    /// offset round the group. Places come in facing pairs (<see cref="PatronSpots"/>), so an even offset keeps a
    /// couple across the table from each other.
    /// </summary>
    public static int FirstSeat(uint key, long window, int seats) =>
        seats <= 1 ? 0 : 2 * (int)(Unit(Mix(key, (uint)window, 7u), 6) * ((seats + 1) / 2)) % seats;

    /// <summary>
    /// A counter's queue at <paramref name="now"/>: the number of the customer at its front (one served every
    /// <paramref name="serve"/> seconds, each a new person) and how many wait, at most <paramref name="most"/>.
    /// The length follows <paramref name="busy"/>, seeded per serving so it breathes.
    /// </summary>
    public static (long Front, int Length) Queue(uint key, double now, double serve, int most, Func<double, float> busy)
    {
        double shift = Unit(key, 0x71u) * serve;
        long front = (long)Math.Floor((now + shift) / serve);
        float b = busy(now);
        if (b <= 0) return (front, 0);
        double want = b * most * (0.55 + 0.6 * Unit(Mix(key, (uint)front, 0x9u), 7));
        return (front, Math.Clamp((int)Math.Round(want), 0, most));
    }

    /// <summary>The progress through the current serving, 0..1 (the queue steps forward at its start).</summary>
    public static double QueuePhase(uint key, double now, double serve)
    {
        double shift = Unit(key, 0x71u) * serve;
        double x = (now + shift) / serve;
        return x - Math.Floor(x);
    }

    /// <summary>A look number for a person: a spot, its window, its place in the party.</summary>
    public static int Look(uint key, long window, int place) => (int)(Mix(key, (uint)window, (uint)place * 0x9E37u + 3u) % 997u);

    /// <summary>A stable hash of a spot from its place in a plan (centimetres) and a salt.</summary>
    public static uint KeyOf(int xCm, int zCm, int floor, uint salt) => Mix((uint)xCm, (uint)zCm, (uint)floor * 0x85EBCA6Bu ^ salt);

    public static uint Mix(uint a, uint b, uint c)
    {
        uint h = 2166136261u;
        h = (h ^ a) * 16777619u;
        h = (h ^ b) * 16777619u;
        h = (h ^ c) * 16777619u;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    /// <summary>A number in [0, 1) from a hash and a salt.</summary>
    public static double Unit(uint k, uint salt) => Mix(k, salt, 0xA511E9B3u) / 4294967296.0;
}
