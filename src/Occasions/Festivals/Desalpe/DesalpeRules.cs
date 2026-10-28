// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-desalpe.md).

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>
/// The Désalpe's rules (#1269 part 7): the herd, how a cow is dressed, the score of a drive and what it pays. A cow's dress is one byte:
/// 0 bare, else 1 + (shape - 1) * 6 + colour with three flower headdresses (<see cref="Shapes"/>) in six colours. A cow's bell is not
/// chosen: the Leitkuh wears the biggest Treichel, every third cow a middle one, the rest a small one (<see cref="BellSize"/>).
/// </summary>
public static class DesalpeRules
{
    /// <summary>The festival's id (the occasion's id) and its three games.</summary>
    public const string FestivalId = "desalpe";

    /// <summary>Decorating the herd, from day 1; the drive from day 3; the vote of the fair from day 4.</summary>
    public const string DressGame = "schmuck", DriveGame = "alpabzug", VoteGame = "wahl";

    public const int Shapes = 3, Colours = 6;

    /// <summary>Cows in the herd (the plan allows up to 40; 14 is two flanks' work).</summary>
    public const int Cows = 14;

    /// <summary>Metres of route: the production drive and the one under <c>--festival test</c> (a check cannot wait a quarter of an hour).</summary>
    public const double RouteMeters = 700, TestRouteMeters = 110;

    /// <summary>The flight zone of a body on foot and of one mounted, m.</summary>
    public const float ZoneFoot = 5f, ZoneMounted = 9f;

    /// <summary>How near the herd a body counts as a driver of it for the pay, and how near a cow a body must be to dress it, m.</summary>
    public const double DriverReach = 60, DressReach = 6;

    /// <summary>How near the sign at the alp a body must be to start the drive, how near the fair's ring to vote, m.</summary>
    public const double StartReach = 30, VoteReach = 30;

    /// <summary>The Leitkuh's average pace counting her waits, m/s: a drive on par takes <c>length / ParPace</c> seconds.</summary>
    public const double ParPace = 0.85;

    /// <summary>After this long without everyone arriving the drive ends with the cows that are there (a multiple of par).</summary>
    public const double TimeoutOfPar = 3.0;

    /// <summary>The herd stands at the fair this long after arriving, then a fresh herd is up at the alp for the next drive.</summary>
    public const double FairSeconds = 120;

    /// <summary>How long after the Leitkuh arrived the drive waits for stragglers.</summary>
    public const double StragglerSeconds = 45;

    // ---- dress -----------------------------------------------------------------------------------

    public static int ShapeOf(byte dress) => dress == 0 ? 0 : 1 + (dress - 1) / Colours;

    public static int ColourOf(byte dress) => dress == 0 ? 0 : (dress - 1) % Colours;

    public static byte Dress(int shape, int colour) =>
        shape <= 0 ? (byte)0 : (byte)(1 + (Math.Clamp(shape, 1, Shapes) - 1) * Colours + ((colour % Colours) + Colours) % Colours);

    /// <summary>The next headdress shape (bare, then the three, then bare again), keeping the colour.</summary>
    public static byte NextShape(byte dress) => Dress((ShapeOf(dress) + 1) % (Shapes + 1), ColourOf(dress));

    /// <summary>The next colour of the same shape; a bare cow gets the first shape first.</summary>
    public static byte NextColour(byte dress) => dress == 0 ? Dress(1, 0) : Dress(ShapeOf(dress), ColourOf(dress) + 1);

    public static bool Valid(byte dress) => dress <= Shapes * Colours;

    /// <summary>1 small, 2 middle, 3 big: the Leitkuh wears the big one.</summary>
    public static int BellSize(int cow) => cow == 0 ? 3 : cow % 3 == 0 ? 2 : 1;

    public static int Decorated(byte[] dress) => dress.Count(d => d != 0);

    // ---- the drive's score -----------------------------------------------------------------------

    /// <summary>Par for a route of this length, seconds.</summary>
    public static double ParSeconds(double routeLength) => routeLength / ParPace;

    /// <summary>
    /// Cows that arrived x 50, plus 10 for each of them that wears a headdress, plus up to 100 for a drive under par (all of it at half of par,
    /// none at par or later).
    /// </summary>
    public static int Score(int arrived, int arrivedDressed, double seconds, double par)
    {
        if (arrived <= 0) return 0;
        double fast = Math.Clamp((par - seconds) / (par * 0.5), 0, 1);
        return arrived * 50 + arrivedDressed * 10 + (int)Math.Round(fast * 100);
    }

    /// <summary>A drive pays one Batzen for every 8 points (a perfect drive of 14 cows is about 120).</summary>
    public static int BatzenFor(int score) => score / 8;

    /// <summary>What dressing one bare cow pays: (Batzen, points). Once a cow, a run and a player.</summary>
    public const int DressBatzen = 4, DressPoints = 10;

    /// <summary>What voting for a herd pays: (Batzen, points). Once an instance.</summary>
    public const int VoteBatzen = 20, VotePoints = 40;
}
