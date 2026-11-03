using UnitSport.Items;

namespace UnitSport.Adventure.Together;

/// <summary>A milestone a veteran can guide a newcomer through, with its two cosmetics.</summary>
public sealed record Milestone(string Id, string Name, ItemId Newcomer, ItemId Mentor);

/// <summary>
/// Mentors (#1266 part 6), pure: the milestones (a first summit, a first roped glacier crossing, a first 2 km ride, a first
/// structure of 10 pieces), who is a newcomer (never did it) and a veteran (did it 3 times), which pairs count (once per
/// pair and milestone, a newcomer mentored once a milestone ever, session keys never), the cosmetics and the titles.
/// Tier 0 (<c>MentorRulesTests</c>).
/// </summary>
public static class MentorRules
{
    public const string Summit = "summit", Glacier = "glacier", Ride = "ride", Build = "build";

    public static readonly Milestone[] All =
    {
        new(Summit, "a first summit", ItemId.ErstbesteigungCap, ItemId.BergfuehrerCap),
        new(Glacier, "a first glacier crossing", ItemId.GletscherScarf, ItemId.SeilschaftScarf),
        new(Ride, "a first ride", ItemId.MitfahrerGloves, ItemId.FahrlehrerGloves),
        new(Build, "a first build", ItemId.ZimmermannCap, ItemId.PolierHat),
    };

    public static Milestone? Of(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>A veteran did it this often.</summary>
    public const int VeteranTimes = 3;
    /// <summary>Roped glacier metres for the glacier milestone; ridden metres for the ride's.</summary>
    public const double GlacierMetres = 200, RideMetres = 2000;
    /// <summary>Pieces of a structure for the build's.</summary>
    public const int BuildPieces = 10;

    /// <summary>Mentorships (all milestones) for the two guide titles.</summary>
    public static string? Title(int mentorships) => mentorships switch
    {
        5 => "Bergführer",
        20 => "Bergführer mit eidg. Fachausweis",
        _ => null,
    };

    /// <summary>A key that is only the session's (unproven, a bot): it never counts on either side.</summary>
    public static bool SessionKey(string key) => key.StartsWith("peer:", StringComparison.Ordinal) || key == "local";

    public static bool Newcomer(TogetherProgress p, string milestone) => !p.Firsts.Contains(milestone) && !p.MentoredAs.Contains(milestone);

    public static bool Veteran(TogetherProgress p, string milestone) => p.Done.GetValueOrDefault(milestone) >= VeteranTimes;

    /// <summary>
    /// Whether <paramref name="veteranKey"/> mentoring <paramref name="newcomerKey"/> through <paramref name="milestone"/>
    /// counts now: proven keys on both sides, the one a newcomer and the other a veteran, the pair not counted before.
    /// </summary>
    public static bool Counts(string milestone, string newcomerKey, TogetherProgress newcomer, string veteranKey, TogetherProgress veteran)
    {
        if (Of(milestone) == null || SessionKey(newcomerKey) || SessionKey(veteranKey) || newcomerKey == veteranKey) return false;
        if (!Newcomer(newcomer, milestone) || !Veteran(veteran, milestone)) return false;
        return !veteran.Mentees.Contains(Pair(milestone, newcomerKey));
    }

    public static string Pair(string milestone, string key) => $"{milestone}:{TogetherProgress.Short(key)}";

    /// <summary>
    /// Writes a mentorship into both sections: the newcomer's first (and that it was mentored), the veteran's count and
    /// pair. The rewards: the newcomer's cosmetic always; the veteran's own the first time it mentors this milestone; a
    /// title at 5 and 20 mentorships.
    /// </summary>
    public static (ItemId NewcomerGets, ItemId? MentorGets, string? Title) Apply(string milestone, string newcomerKey, TogetherProgress newcomer, TogetherProgress veteran)
    {
        var m = Of(milestone)!;
        newcomer.MentoredAs.Add(milestone);
        Did(newcomer, milestone);
        veteran.Mentees.Add(Pair(milestone, newcomerKey));
        int before = veteran.Mentored.GetValueOrDefault(milestone);
        veteran.Mentored[milestone] = before + 1;
        int total = veteran.Mentored.Values.Sum();
        return (m.Newcomer, before == 0 ? m.Mentor : null, Title(total));
    }

    /// <summary>A milestone done (any way): the first noted, the count up.</summary>
    public static void Did(TogetherProgress p, string milestone)
    {
        if (!p.Firsts.Contains(milestone)) p.Firsts.Add(milestone);
        p.Done[milestone] = p.Done.GetValueOrDefault(milestone) + 1;
    }
}
