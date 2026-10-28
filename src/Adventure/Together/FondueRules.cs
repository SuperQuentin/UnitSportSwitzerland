using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>What goes in the shared pot (#1266 part 5.2). Sent as a byte: append only.</summary>
public enum Ingredient : byte { Cheese = 0, Bread = 1, Wine = 2, Kirsch = 3, Potato = 4 }

/// <summary>A lost bread's forfeit (#1266 part 5.4), the table's tradition. Sent as a byte.</summary>
public enum Forfeit : byte { Line = 0, Round = 1, Yodel = 2 }

/// <summary>The server's <c>--forfeits off|mild|all</c>: mild drops the money one.</summary>
public enum ForfeitMode : byte { Off = 0, Mild = 1, All = 2 }

/// <summary>Where a pot stands (#1266 part 5.1). Sent as a byte.</summary>
public enum PotSpot : byte { Venue = 0, Hut = 1, Campfire = 2, Festival = 3 }

/// <summary>
/// The shared pot's rules (#1266 parts 5.2-5.4), pure: ingredients and what they give, the dips a cheese makes, the
/// feast's length from the pot's quality, the odds of losing one's bread, the forfeits, the raclette's scraping
/// window. Tier 0 (<c>FondueRulesTests</c>).
/// </summary>
public static class FondueRules
{
    /// <summary>Dips one cheese makes.</summary>
    public const int DipsPerCheese = 6;
    /// <summary>A pot needs this much cheese at least (and one per seat).</summary>
    public const int MinCheese = 2;
    /// <summary>Raclette: potatoes per seat.</summary>
    public const int PotatoesPerSeat = 2;
    /// <summary>Health a dip gives.</summary>
    public const float DipHeal = 6f;
    /// <summary>One dip per this many seconds at most.</summary>
    public const double DipEvery = 2;
    /// <summary>The feast's heal at the end (the fondue item's).</summary>
    public const float FeastHeal = 90f;
    /// <summary><c>Fed</c> lasts this times the quality, s: 600 at the best, 150 when it split (quality 0.25).</summary>
    public const float FedPerQuality = 600f;
    /// <summary>A split pot's quality, at most.</summary>
    public const float SplitCap = 0.25f;
    /// <summary>Seats: two at least for a session; eight at a table, sixteen at a festival.</summary>
    public const int MinSeats = 2, MaxSeats = 8, FestivalSeats = 16;
    /// <summary>The round of the money forfeit, CHF a head of the others at the table.</summary>
    public const int RoundChf = 5;
    /// <summary>Seconds to choose a forfeit; the first is the default.</summary>
    public const double ForfeitSeconds = 20;

    /// <summary>The item an ingredient is.</summary>
    public static ItemId ItemOf(Ingredient i) => i switch
    {
        Ingredient.Cheese => ItemId.Cheese,
        Ingredient.Bread => ItemId.Bread,
        Ingredient.Wine => ItemId.WhiteWine,
        Ingredient.Kirsch => ItemId.Kirsch,
        _ => ItemId.Potato,
    };

    /// <summary>The ingredient an item is, or null.</summary>
    public static Ingredient? Of(ItemId id) => id switch
    {
        ItemId.Cheese => Ingredient.Cheese,
        ItemId.Bread => Ingredient.Bread,
        ItemId.WhiteWine => Ingredient.Wine,
        ItemId.Kirsch => Ingredient.Kirsch,
        ItemId.Potato => Ingredient.Potato,
        _ => null,
    };

    /// <summary>The dips a pot of <paramref name="cheese"/> holds.</summary>
    public static int Dips(int cheese) => Math.Max(0, cheese) * DipsPerCheese;

    /// <summary>Cheese the table needs before it cooks: one a seat, two at least.</summary>
    public static int CheeseNeeded(int seats) => Math.Max(MinCheese, seats);

    /// <summary>The feast's buffs as a mask of <c>1 &lt;&lt; StaminaBuff</c>: always fed, warm with wine, hearty with kirsch.</summary>
    public static byte BuffMask(bool wine, bool kirsch) =>
        (byte)((1 << (int)StaminaBuff.Fed) | (wine ? 1 << (int)StaminaBuff.Warm : 0) | (kirsch ? 1 << (int)StaminaBuff.Hearty : 0));

    public static bool Has(byte mask, StaminaBuff b) => (mask & (1 << (int)b)) != 0;

    /// <summary>The feast's length, s, from the pot's quality (0..1).</summary>
    public static float FedSeconds(float quality) => Math.Max(60f, FedPerQuality * Math.Clamp(quality, 0f, 1f));

    /// <summary>The pot's quality at the end: the mean of its eights, less the split meter; a split pot at most <see cref="SplitCap"/>.</summary>
    public static float Quality(float meanQ, float split, bool splitted)
    {
        float q = Math.Clamp(meanQ - split, 0f, 1f);
        return splitted ? Math.Min(q, SplitCap) : q;
    }

    /// <summary>The chance a dip loses its bread: 3 %, up to 12 % more for a poorly stirred pot, 5 % more for a hurried dip.</summary>
    public static double BreadLossChance(float recentQ, bool hurried) =>
        0.03 + 0.12 * (1 - Math.Clamp(recentQ, 0f, 1f)) + (hurried ? 0.05 : 0);

    /// <summary>A dip within this many seconds of the last is hurried.</summary>
    public const double HurriedWithin = 3;

    /// <summary>Whether a forfeit may be chosen under the server's setting (and, for the round, with the money for it).</summary>
    public static bool Allowed(ForfeitMode mode, Forfeit f, long balance, int others) => mode switch
    {
        ForfeitMode.Off => false,
        ForfeitMode.Mild => f != Forfeit.Round,
        _ => f != Forfeit.Round || balance >= RoundCost(others),
    };

    /// <summary>What buying a round costs, CHF.</summary>
    public static int RoundCost(int others) => RoundChf * Math.Max(0, others);

    /// <summary>The lines of the "line" forfeit (a system chat line to the table), "{0}" the one who lost the bread.</summary>
    public static readonly string[] Lines =
    {
        "{0} buys the next round.",
        "{0} sings the next song.",
        "{0} does the dishes.",
        "{0} carries the caquelon home.",
        "{0} tells the next story.",
    };

    public static ForfeitMode ParseMode(string? s) => s?.ToLowerInvariant() switch
    {
        "off" => ForfeitMode.Off,
        "mild" => ForfeitMode.Mild,
        _ => ForfeitMode.All,
    };

    // ---- raclette: scraping the bubbling face ----------------------------------------------------

    /// <summary>The window round the bubbling's peak for a full portion, s.</summary>
    public const double ScrapeWindow = 0.6;

    /// <summary>The bubbling's period at a pot, 3-5 s, the same on every peer (from the pot's id).</summary>
    public static double BubblePeriod(int potId) => 3 + (unchecked((uint)potId * 2654435761u) % 1000) / 500.0;

    /// <summary>
    /// A scrape's quality: 1 inside the window round the peak, falling off before (raw) and after (burnt), 0.1 at least.
    /// <paramref name="sincePeak"/>: seconds from the nearest peak, negative before it.
    /// </summary>
    public static float ScrapeQ(double sincePeak)
    {
        double half = ScrapeWindow / 2, off = Math.Abs(sincePeak) - half;
        if (off <= 0) return 1f;
        // too early it is raw (a gentler fall), too late burnt (a steeper one)
        double fall = sincePeak < 0 ? 0.9 : 1.6;
        return (float)Math.Max(0.1, 1 - off * fall);
    }

    /// <summary>Seconds from the nearest bubbling peak at <paramref name="t"/> (server time), negative before it.</summary>
    public static double SincePeak(int potId, double t)
    {
        double p = BubblePeriod(potId), phase = t % p;
        return phase <= p / 2 ? phase : phase - p;
    }
}
