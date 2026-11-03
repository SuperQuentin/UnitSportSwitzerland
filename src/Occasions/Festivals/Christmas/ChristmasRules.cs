// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-christmas.md).

using UnitSport.Items;

namespace UnitSport.Occasions.Festivals.Christmas;

/// <summary>What a stall sells: one ware each.</summary>
public enum StallKind { Gluehwein, Raclette, Gritibaenz, Ornaments }

/// <summary>One stall of the market: where it stands (LV95) and which way its counter faces.</summary>
public readonly record struct MarketStall(int Index, StallKind Kind, double E, double N, float Yaw);

/// <summary>
/// The Christmas market's rules (#1348, part of #1269): the festival's id and games, where the stalls and the Samichlaus stand round the site's middle,
/// what a stall sells and for how many Batzen, and what a taste and the Samichlaus pay. A stall's first visit of the day is a free taste (the
/// stall-hop: a stamp, a few Batzen and points); after that the same key buys the stall's ware. Pure, a function of the site alone, so the
/// server, every client and the props agree with nothing sent.
/// </summary>
public static class ChristmasRules
{
    /// <summary>The festival's id (the occasion's id, not the Christmas season's: the sky is the season's) and its two games.</summary>
    public const string FestivalId = "christmasmarket";

    /// <summary>The stall-hop from day 1; the Samichlaus on day 12 (6 December; day 1 is 25 November).</summary>
    public const string StallGame = "stallhop", SamichlausGame = "samichlaus";

    public const int SamichlausDay = 12;

    /// <summary>Stalls: two rows of four along an east-west lane, 4 m apart along the lane, 4 m either side of it.</summary>
    public const int Stalls = 8;

    private const double Spacing = 4, RowOffset = 4;

    /// <summary>How near a stall's middle a body must be to taste or buy, and how near the Samichlaus to be given his bag, m.</summary>
    public const double StallReach = 5.5, SamichlausReach = 4.5;

    /// <summary>The Samichlaus stands at the lane's east end, the tree at its west end, m from the site's middle.</summary>
    public const double SamichlausOffset = 12, TreeOffset = 12;

    /// <summary>A stall's free taste, once a day each: Batzen and party points; tasting all eight in a day adds the Marktgang.</summary>
    public const int StampBatzen = 8, StampPoints = 20, MarktgangBatzen = 16, MarktgangPoints = 60;

    /// <summary>The Samichlaus's bag on 6 December, once a player: Batzen and points (and a Grittibänz and two mandarins).</summary>
    public const int SamichlausBatzen = 30, SamichlausPoints = 80;

    /// <summary>The stalls of a market held at (<paramref name="e"/>, <paramref name="n"/>): row 0 on the north side facing south, row 1 on the south side facing north.</summary>
    public static MarketStall[] Layout(double e, double n)
    {
        var list = new MarketStall[Stalls];
        for (int i = 0; i < Stalls; i++)
        {
            int row = i / 4, col = i % 4;
            double x = (col - 1.5) * Spacing;
            // the kinds alternate so each row offers all four wares, the second row shifted by two
            var kind = (StallKind)((col + row * 2) % 4);
            list[i] = new MarketStall(i, kind, e + x, n + (row == 0 ? RowOffset : -RowOffset), row == 0 ? 0f : MathF.PI);
        }
        return list;
    }

    public static MarketStall[] Layout(ResolvedSite site) => Layout(site.E, site.N);

    /// <summary>The Samichlaus's spot, and the market tree's.</summary>
    public static (double E, double N) SamichlausAt(ResolvedSite site) => (site.E + SamichlausOffset, site.N);

    public static (double E, double N) TreeAt(ResolvedSite site) => (site.E - TreeOffset, site.N);

    /// <summary>The nearest stall within <see cref="StallReach"/> of a body, or -1.</summary>
    public static int NearestStall(ResolvedSite site, double e, double n)
    {
        int best = -1;
        double bestD = StallReach * StallReach;
        foreach (var s in Layout(site))
        {
            double d = (s.E - e) * (s.E - e) + (s.N - n) * (s.N - n);
            if (d <= bestD) { bestD = d; best = s.Index; }
        }
        return best;
    }

    public static bool NearSamichlaus(ResolvedSite site, double e, double n)
    {
        var (se, sn) = SamichlausAt(site);
        return (se - e) * (se - e) + (sn - n) * (sn - n) <= SamichlausReach * SamichlausReach;
    }

    // ---- the wares -------------------------------------------------------------------------------------------

    /// <summary>The item a stall sells: the market's Glühwein mug, raclette, a Grittibänz, a bauble.</summary>
    public static ItemId ItemOf(StallKind kind) => kind switch
    {
        StallKind.Gluehwein => ItemId.GluehweinMug,
        StallKind.Raclette => ItemId.Raclette,
        StallKind.Gritibaenz => ItemId.Grittibaenz,
        _ => ItemId.ChristmasBauble,
    };

    /// <summary>What one ware costs, Batzen (1 Batzen = CHF 1 when converted: a mug is the price of a real one).</summary>
    public static int PriceOf(StallKind kind) => kind switch
    {
        StallKind.Gluehwein => 4,
        StallKind.Raclette => 8,
        StallKind.Gritibaenz => 3,
        _ => 12,
    };

    public static string WareName(StallKind kind) => kind switch
    {
        StallKind.Gluehwein => "Glühwein",
        StallKind.Raclette => "Raclette",
        StallKind.Gritibaenz => "Grittibänz",
        _ => "Christmas bauble",
    };

    public static string StallName(StallKind kind) => kind switch
    {
        StallKind.Gluehwein => "Glühwein stall",
        StallKind.Raclette => "Raclette stall",
        StallKind.Gritibaenz => "Bakery stall",
        _ => "Ornaments stall",
    };

    // ---- the stall-hop ---------------------------------------------------------------------------------------

    /// <summary>The mask with every stall's bit set.</summary>
    public const int AllStamps = (1 << Stalls) - 1;

    public static bool Stamped(int mask, int stall) => stall is >= 0 and < Stalls && (mask & (1 << stall)) != 0;

    public static int Stamp(int mask, int stall) => stall is >= 0 and < Stalls ? mask | (1 << stall) : mask;

    public static int Count(int mask)
    {
        int n = 0;
        for (int i = 0; i < Stalls; i++) if ((mask & (1 << i)) != 0) n++;
        return n;
    }

    /// <summary>The most a day's stall-hop can pay: eight tastes and the Marktgang. The game's daily cap.</summary>
    public const int DayMax = Stalls * StampBatzen + MarktgangBatzen;
}
