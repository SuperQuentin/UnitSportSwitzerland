// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-escalade.md).

namespace UnitSport.Occasions.Festivals.Escalade;

/// <summary>
/// The Escalade's rules (#1351, part of #1269): Geneva's festival of the night of 11-12 December 1602. Three games round the site's middle, all a function
/// of the site alone (the server, every client and the props agree with nothing sent): the <b>Marmite</b> (the party's youngest and oldest by account age
/// smash it together, with the saying), the <b>Course de l'Escalade</b> (a timed loop of six gates through the old town, back to the start line) and the
/// <b>soupe</b> (a pot that feeds the whole party at once). Pure.
/// </summary>
public static class EscaladeRules
{
    public const string FestivalId = "escalade";

    /// <summary>The three games, all open from day 1.</summary>
    public const string MarmiteGame = "marmite", CourseGame = "course", SoupeGame = "soupe";

    /// <summary>What the Genevans shout as the marmite breaks: "Thus perish the enemies of the Republic!"</summary>
    public const string Saying = "Ainsi périssent les ennemis de la République!";

    /// <summary>How near a body must be to the marmite, the pot, the start line and a gate, m.</summary>
    public const double MarmiteReach = 5.0, PotReach = 4.5, StartReach = 5.0, GateReach = 6.0;

    /// <summary>A party member within this of the marmite or the pot takes part (the youngest and oldest are picked among them), m.</summary>
    public const double CrewReach = 12.0;

    /// <summary>The first of the pair to take the marmite holds it this long for the other to join, s.</summary>
    public const double HoldSeconds = 6;

    // ---- the ground -------------------------------------------------------------------------------------------

    /// <summary>The marmite stands at the site's middle, the soup pot 9 m east of it, the start line 12 m west of it, m.</summary>
    public const double PotOffset = 9, StartOffset = -12;

    /// <summary>The course: six gates on a ring this far from the middle, m.</summary>
    public const double RingRadius = 55;

    public const int Gates = 6;

    public static (double E, double N) MarmiteAt(ResolvedSite site) => (site.E, site.N);

    public static (double E, double N) PotAt(ResolvedSite site) => (site.E + PotOffset, site.N);

    public static (double E, double N) StartAt(ResolvedSite site) => (site.E + StartOffset, site.N);

    /// <summary>
    /// The course's points in order: the six gates round the ring (an arc of 6/7 of the circle, leaving the start line's side), then the start line again
    /// as the finish. Index <see cref="Gates"/> is the finish.
    /// </summary>
    public static (double E, double N)[] Path(ResolvedSite site)
    {
        var path = new (double, double)[Gates + 1];
        for (int k = 0; k < Gates; k++)
        {
            double a = Math.PI + (k + 1) * (2 * Math.PI / (Gates + 1));
            path[k] = (site.E + RingRadius * Math.Cos(a), site.N + RingRadius * Math.Sin(a));
        }
        path[Gates] = StartAt(site);
        return path;
    }

    /// <summary>The length of the whole course from the start line, m.</summary>
    public static double CourseLength(ResolvedSite site)
    {
        var path = Path(site);
        var (e, n) = StartAt(site);
        double len = 0;
        foreach (var (pe, pn) in path)
        {
            len += Math.Sqrt((pe - e) * (pe - e) + (pn - n) * (pn - n));
            (e, n) = (pe, pn);
        }
        return len;
    }

    /// <summary>The time a good jog makes (5.5 m/s), s.</summary>
    public static double ParSeconds(ResolvedSite site) => CourseLength(site) / 5.5;

    /// <summary>A run ends unfinished after this long, s.</summary>
    public static double MaxSeconds(ResolvedSite site) => Math.Max(180, ParSeconds(site) * 4);

    /// <summary>A finish quicker than a sprint (12 m/s over the whole course) is not a run: it was a jump through the map.</summary>
    public static bool Plausible(ResolvedSite site, double seconds, double metresPerSecond = SprintSpeed) => seconds >= CourseLength(site) / metresPerSecond;

    /// <summary>The fastest anyone is believed to cover the course, m/s.</summary>
    public const double SprintSpeed = 12.0;

    public static double Dist(double e, double n, (double E, double N) at) => Math.Sqrt((e - at.E) * (e - at.E) + (n - at.N) * (n - at.N));

    public static bool NearMarmite(ResolvedSite site, double e, double n) => Dist(e, n, MarmiteAt(site)) <= MarmiteReach;

    public static bool NearPot(ResolvedSite site, double e, double n) => Dist(e, n, PotAt(site)) <= PotReach;

    public static bool NearStart(ResolvedSite site, double e, double n) => Dist(e, n, StartAt(site)) <= StartReach;

    // ---- the Course: pay ----------------------------------------------------------------------------------------

    /// <summary>Base pay of a finished run, plus up to <see cref="SpeedBatzen"/> for beating the par; points are three times the Batzen.</summary>
    public const int CourseBatzen = 20, SpeedBatzen = 40, PointsPerBatzen = 3;

    /// <summary>The Batzen of a finished run: 20, plus 40 at 80 % of the par or better, fading to nothing at 150 % of it.</summary>
    public static int CoursePay(double seconds, double par)
    {
        double t = Math.Clamp((1.5 * par - seconds) / (0.7 * par), 0, 1);
        return CourseBatzen + (int)Math.Round(SpeedBatzen * t);
    }

    // ---- the Marmite ----------------------------------------------------------------------------------------------

    /// <summary>A party member at the marmite: the progress key, the account's creation (unix seconds) and a name.</summary>
    public readonly record struct Member(string Key, long Created, string Name);

    /// <summary>
    /// The youngest and the oldest account among the members present (a lone player is both). Ties fall to the key's ordinal order, so every caller
    /// picks the same two. Null for nobody.
    /// </summary>
    public static (Member Youngest, Member Oldest)? Pair(IReadOnlyList<Member> members)
    {
        if (members.Count == 0) return null;
        var young = members[0];
        var old = members[0];
        for (int i = 1; i < members.Count; i++)
        {
            var m = members[i];
            // one total order, (creation, key): the oldest is its least, the youngest its greatest, so two people are never the same one
            if (Older(young, m)) young = m;
            if (Older(m, old)) old = m;
        }
        return (young, old);
    }

    private static bool Older(Member a, Member b) => a.Created < b.Created || (a.Created == b.Created && string.CompareOrdinal(a.Key, b.Key) < 0);

    /// <summary>The marmite's pay for each member present: Batzen, the pair's extra, points and the chocolate pieces handed round.</summary>
    public const int SmashBatzen = 30, SmashPairBatzen = 10, SmashPoints = 100, ChocolatePieces = 3;

    // ---- the soupe ----------------------------------------------------------------------------------------------------

    /// <summary>A bowl of soup a day each, for everyone of the party at the pot: Batzen and points.</summary>
    public const int SoupeBatzen = 8, SoupePoints = 20;

    /// <summary>The most one day pays in each game: the marmite's pair share, the course twice over at its best, the soup.</summary>
    public const int MarmiteDayCap = SmashBatzen + SmashPairBatzen, CourseDayCap = 120, SoupeDayCap = SoupeBatzen;
}
