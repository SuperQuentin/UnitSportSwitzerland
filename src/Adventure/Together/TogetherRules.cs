namespace UnitSport.Adventure.Together;

/// <summary>
/// A party member as <see cref="TogetherRules.Count"/> sees them: where they are (LV95 metres and the
/// altitude), whether they adventure now (<c>AdventureService.Active</c>, off in a round) and whether they
/// are knocked out (the replicated <c>Down</c>).
/// </summary>
public readonly record struct Mate(double E, double N, double Alt, bool Active, bool Down);

/// <summary>
/// "Playing together" (#1266, <c>docs/plans/adventure-together.md</c> part 1), in one place: a party member,
/// adventuring, not knocked out, within <see cref="Radius"/> m across and <see cref="VerticalRadius"/> m up or
/// down; at most <see cref="MaxCounted"/> counted. And what that many friends add (part 2): more from
/// gathering and the hand harvest, a shorter wait for a bite, rarer fish, rare finds, quest cash. Plain
/// C#, linked into tier 0 (<c>TogetherRulesTests</c>). Who counts is decided by the callers: the server's
/// <c>TogetherService.Near</c> for what it grants, the owner's cached count for its own loot.
/// </summary>
public static class TogetherRules
{
    /// <summary>Across, m: the same as <c>Seals.CoopRadiusM</c>, so one distance means "together" everywhere.</summary>
    public const float Radius = 60f;
    /// <summary>Up or down, m.</summary>
    public const float VerticalRadius = 40f;
    /// <summary>"Playing together" for the week's seconds and the punch card (part 7): a wider circle.</summary>
    public const float PlayRadius = 200f;
    /// <summary>Friends counted at most (the bonus stops at x1.75).</summary>
    public const int MaxCounted = 3;

    /// <summary>More harvest, gathering and catch per friend.</summary>
    public const double YieldPerFriend = 0.25;
    /// <summary>A bite comes this much sooner per friend (the mean wait is divided by 1 + this x n).</summary>
    public const double BitePerFriend = 0.15;
    /// <summary>Rare, very rare and legendary fish weigh this much more per friend.</summary>
    public const double RarePerFriend = 0.5;
    /// <summary>A catalog quest's cash, per friend near when it is paid.</summary>
    public const double CashPerFriend = 0.10;
    /// <summary>A stone stroke above <see cref="RareFindAltitude"/> gives a Bergkristall this often, alone.</summary>
    public const double RareFindBase = 0.004;
    /// <summary>Rare finds only up here, m.</summary>
    public const float RareFindAltitude = 1800f;

    /// <summary>
    /// The friends counted round a player at (<paramref name="e"/>, <paramref name="n"/>, <paramref name="alt"/>):
    /// the party members (the caller leaves the player out) who adventure, are not knocked out, and are within
    /// <paramref name="radius"/> across and <see cref="VerticalRadius"/> up or down. Capped at <see cref="MaxCounted"/>.
    /// The mode does not matter: a friend in a car beside the field counts.
    /// </summary>
    public static int Count(double e, double n, double alt, ReadOnlySpan<Mate> mates, float radius = Radius)
    {
        int count = 0;
        double r2 = (double)radius * radius;
        foreach (var m in mates)
        {
            if (!m.Active || m.Down) continue;
            double de = m.E - e, dn = m.N - n;
            if (de * de + dn * dn > r2 || Math.Abs(m.Alt - alt) > VerticalRadius) continue;
            if (++count >= MaxCounted) return MaxCounted;
        }
        return count;
    }

    private static int Clamp(int n) => Math.Clamp(n, 0, MaxCounted);

    /// <summary>
    /// What a harvest of <paramref name="baseCount"/> gives with <paramref name="n"/> friends:
    /// <c>floor(base x (1 + 0.25 n) + u)</c>, <paramref name="u"/> uniform in [0, 1): stochastic rounding, so
    /// the expected yield is exact. Every harvester rolls for themselves; nothing is split.
    /// </summary>
    public static int Yield(int baseCount, int n, double u) =>
        baseCount <= 0 ? baseCount : (int)Math.Floor(baseCount * YieldFactor(n) + Math.Clamp(u, 0, 0.999999));

    /// <summary>The harvest's factor with <paramref name="n"/> friends: 1, 1.25, 1.5, 1.75.</summary>
    public static double YieldFactor(int n) => 1 + YieldPerFriend * Clamp(n);

    /// <summary>The mean bite wait's factor: <c>1 / (1 + 0.15 n)</c>.</summary>
    public static double BiteFactor(int n) => 1 / (1 + BitePerFriend * Clamp(n));

    /// <summary>A rare fish's weight factor: <c>1 + 0.5 n</c> (x2.5 at three friends).</summary>
    public static double RareFactor(int n) => 1 + RarePerFriend * Clamp(n);

    /// <summary>The chance of a Bergkristall per stone stroke above <see cref="RareFindAltitude"/>: <c>0.4 % x (1 + 0.5 n)</c>.</summary>
    public static double RareFindChance(int n, double altitude) =>
        altitude < RareFindAltitude ? 0 : RareFindBase * (1 + RarePerFriend * Clamp(n));

    /// <summary>
    /// Whether a grant's cash takes the together bonus: a catalog quest's (<c>"quest:&lt;id&gt;"</c>), never a
    /// Postkarte card's (<c>quest:pk:</c>, which already pays its own +50 % together), a chest's or a round's.
    /// </summary>
    public static bool PaysTogetherCash(string why) =>
        why.StartsWith("quest:", StringComparison.Ordinal) && !why.StartsWith("quest:pk:", StringComparison.Ordinal);

    /// <summary>The bonus francs on a quest's <paramref name="chf"/> with <paramref name="n"/> friends near (rounded, at least 1 when any).</summary>
    public static int CashBonus(int chf, int n)
    {
        if (chf <= 0 || Clamp(n) == 0) return 0;
        return Math.Max(1, (int)Math.Round(chf * CashPerFriend * Clamp(n), MidpointRounding.AwayFromZero));
    }

    private static readonly string[] BonusTexts = Enumerable.Range(0, 32).Select(i => i == 0 ? "" : $" (+{i} together)").ToArray();

    /// <summary>The toast's together part, " (+2 together)", from a static table (nothing built per harvest); "" for none.</summary>
    public static string BonusText(int bonus) => bonus <= 0 ? "" : bonus < BonusTexts.Length ? BonusTexts[bonus] : $" (+{bonus} together)";

    private static readonly string[] ChipTexts = { "", "Together ×1", "Together ×2", "Together ×3" };

    /// <summary>The HUD chip's text for <paramref name="n"/> friends ("" at 0).</summary>
    public static string ChipText(int n) => ChipTexts[Clamp(n)];
}
