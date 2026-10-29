// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-sechselaeuten.md).

namespace UnitSport.Occasions.Festivals.Sechselaeuten;

/// <summary>
/// The Sechseläuten's rules (#1358, part of #1269): Zürich's spring festival, the third Monday of April. At 18:00 game time the guilds light the
/// pyre under the <b>Böögg</b>, a ten metre snowman with a head stuffed with firecrackers; the whole square bets on how long it takes the head to
/// burst. Here a bet is <see cref="Stake"/> Batzen on one of twelve five-minute buckets, closed at the light; the server draws the time from a fit to
/// the real burns (median about 13 minutes, a long tail) and tells nobody until the bang; the pool is shared out by closeness. Pure: the ground is a
/// function of the site alone, the draw of a seed, the shares of the bets.
/// </summary>
public static class SechselaeutenRules
{
    public const string FestivalId = "sechselaeuten";

    /// <summary>The two games, both open on day 1: the bet on the Böögg, and the Zunft's purse that starts a player's betting.</summary>
    public const string BetGame = "wette", PurseGame = "purse";

    // ---- the evening ---------------------------------------------------------------------------------------

    /// <summary>The sky clock hour the guilds light the pyre at, and the hour after which an evening is too late to light it.</summary>
    public const double LightHour = 18.0, LightEndHour = 22.0;

    /// <summary>Whether the pyre is lit now: the sky crossed <see cref="LightHour"/> since the last look (<paramref name="lastHour"/>: never a first look).</summary>
    public static bool Lights(double? lastHour, double hour) =>
        lastHour is { } last && last < LightHour && hour >= LightHour && hour < LightEndHour;

    /// <summary>Real seconds that one minute of the burn lasts: the head of a median burn (13 minutes) bursts 78 s after the light; the longest (75 minutes) after 7.5 min.</summary>
    public const double DefaultSecondsPerMinute = 6.0;

    /// <summary>A finished round stays on the books this long (the embers, the result, a late player's look) before the next round opens its bets, s.</summary>
    public const double DoneHold = 60;

    // ---- the draw ------------------------------------------------------------------------------------------

    /// <summary>The fit to the real burns: log-normal, median 13 minutes, sigma 0.6 (a burn over 40 minutes is about one in thirty), clamped to 3..75 minutes.</summary>
    public const double MedianMinutes = 13, Sigma = 0.6, MinMinutes = 3, MaxMinutes = 75;

    /// <summary>Minutes until the head bursts, drawn from <paramref name="seed"/> alone (Box-Muller on a seeded generator).</summary>
    public static double Draw(int seed)
    {
        var rng = new Random(seed);
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return Math.Clamp(MedianMinutes * Math.Exp(Sigma * z), MinMinutes, MaxMinutes);
    }

    // ---- the buckets ---------------------------------------------------------------------------------------

    public const int Buckets = 12, BucketMinutes = 5;

    /// <summary>The bucket (1 to 12) a burn time falls in: 1 is up to 5 minutes, 12 is over 55.</summary>
    public static int BucketOf(double minutes) => Math.Clamp((int)Math.Ceiling(minutes / BucketMinutes), 1, Buckets);

    /// <summary>"10-15 min" for bucket 3, "55+ min" for the last, "0-5 min" for the first.</summary>
    public static string Label(int bucket) => bucket >= Buckets ? $"{(Buckets - 1) * BucketMinutes}+ min" : $"{(bucket - 1) * BucketMinutes}-{bucket * BucketMinutes} min";

    /// <summary>The bucket a bet starts on in the picker: the one of the median burn.</summary>
    public static int DefaultBucket => BucketOf(MedianMinutes);

    // ---- the pay -------------------------------------------------------------------------------------------

    /// <summary>A bet costs this many Batzen; one bet per player and round.</summary>
    public const int Stake = 10;

    /// <summary>The Zunft's purse (once a day) is what a player bets with; a bet that is placed and played pays a few Batzen and points on top of any share.</summary>
    public const int PurseBatzen = 40, PursePoints = 20, PlayBatzen = 5, PlayPoints = 30;

    /// <summary>The most a day pays in each game (the pool's shares are not earned, they are staked Batzen coming back, so they are outside it).</summary>
    public const int PurseDayCap = PurseBatzen, BetDayCap = 160;

    /// <summary>Cervelats grilled on the embers: one a round and player.</summary>
    public const int Cervelats = 1;

    /// <summary>How near a body must be to the pyre to bet or grill, and to the Zunft's Weibel to take the purse, m.</summary>
    public const double PyreReach = 14, WeibelReach = 4;

    /// <summary>A bet: who, under which name, on which bucket, the Batzen staked (0 for a bot, who plays for the crowd and wins nothing).</summary>
    public readonly record struct Bet(string Key, string Name, int Bucket, int Stake, bool Bot);

    /// <summary>
    /// A bet's weight against the actual bucket: the exact bucket weighs four, the neighbour a quarter, then a ninth and a sixteenth of that again
    /// (weight = (4 if exact) / (1 + distance)^2) times the stake.
    /// </summary>
    public static double Weight(int bucket, int hit, int stake)
    {
        int d = Math.Abs(bucket - hit);
        return stake * (d == 0 ? 4.0 : 1.0) / ((1.0 + d) * (1.0 + d));
    }

    /// <summary>
    /// The pool (every human stake) shared out by closeness: each human bet gets its weight's part of the pool, in whole Batzen that add up to
    /// the pool exactly (the largest remainders take the odd ones, ties by key). Bots win nothing. A lone bettor gets the stake back.
    /// </summary>
    public static Dictionary<string, int> Share(IReadOnlyList<Bet> bets, double minutes)
    {
        var shares = new Dictionary<string, int>(StringComparer.Ordinal);
        var humans = bets.Where(b => !b.Bot && b.Stake > 0).ToList();
        int pool = humans.Sum(b => b.Stake);
        if (humans.Count == 0 || pool == 0) return shares;
        int hit = BucketOf(minutes);
        double total = humans.Sum(b => Weight(b.Bucket, hit, b.Stake));
        var parts = humans.Select(b => (b.Key, Exact: pool * Weight(b.Bucket, hit, b.Stake) / total)).ToList();
        int given = 0;
        foreach (var (key, exact) in parts)
        {
            int whole = (int)Math.Floor(exact);
            shares[key] = whole;
            given += whole;
        }
        foreach (var (key, _) in parts.OrderByDescending(p => p.Exact - Math.Floor(p.Exact)).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (given >= pool) break;
            shares[key]++;
            given++;
        }
        return shares;
    }

    // ---- the ground ----------------------------------------------------------------------------------------

    /// <summary>The pyre stands at the site's middle; the Zunft's Weibel (who hands out the purse) 9 m east and 3 m south of it, m.</summary>
    public const double WeibelOffsetE = 9, WeibelOffsetN = -3;

    /// <summary>The pyre is ten metres high on a square base this wide, m.</summary>
    public const double PyreHeight = 10, PyreBase = 5;

    public static (double E, double N) PyreAt(ResolvedSite site) => (site.E, site.N);

    public static (double E, double N) WeibelAt(ResolvedSite site) => (site.E + WeibelOffsetE, site.N + WeibelOffsetN);

    public static double Dist(double e, double n, (double E, double N) at) => Math.Sqrt((e - at.E) * (e - at.E) + (n - at.N) * (n - at.N));

    public static bool NearPyre(ResolvedSite site, double e, double n) => Dist(e, n, PyreAt(site)) <= PyreReach;

    public static bool NearWeibel(ResolvedSite site, double e, double n) => Dist(e, n, WeibelAt(site)) <= WeibelReach;

    /// <summary>The six guild banners round the square: their distance from the middle and their count (a ring, hashed per tile by the decor).</summary>
    public const int Banners = 6;

    public const double BannerRing = 34;

    public static (double E, double N) BannerAt(ResolvedSite site, int k)
    {
        double a = k * (2 * Math.PI / Banners) + 0.5;
        return (site.E + BannerRing * Math.Cos(a), site.N + BannerRing * Math.Sin(a));
    }
}
