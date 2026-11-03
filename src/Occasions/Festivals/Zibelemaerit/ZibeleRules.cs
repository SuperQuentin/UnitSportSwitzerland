// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-zibelemaerit.md).

using UnitSport.Items;

namespace UnitSport.Occasions.Festivals.Zibelemaerit;

/// <summary>What a stall sells: onion braids, the onion tart, the flour soup, confetti eggs.</summary>
public enum ZibeleStallKind { Zopf, Chueche, Mehlsuppe, Konfetti }

/// <summary>One stall of the market: where it stands (LV95) and which way its counter faces.</summary>
public readonly record struct ZibeleStall(int Index, ZibeleStallKind Kind, double E, double N, float Yaw);

/// <summary>
/// The Zibelemärit's rules (#1373, part of #1269): Bern's onion market on the fourth Monday of November, one day. Four stalls in a row on the square (the first use
/// of a stall each day is a free taste, a stamp; after that the same key buys its ware for Batzen) and the Konfettischlacht: confetti eggs (and Räppli) thrown
/// on the square count, every fifth throw pays. Pure, a function of the site alone, so the server, every client and the props agree with nothing sent.
/// </summary>
public static class ZibeleRules
{
    public const string FestivalId = "zibelemaerit";

    /// <summary>The stall-hop (day 1) and the confetti fight (day 1).</summary>
    public const string StallGame = "zibelemarkt", ConfettiGame = "konfetti";

    public const int Stalls = 4;

    private const double Spacing = 5, RowOffset = 4;

    /// <summary>How near a stall's middle a body must be to taste or buy, m; the server allows <see cref="ServerSlack"/> more for lag.</summary>
    public const double StallReach = 5.5, ServerSlack = 6;

    /// <summary>A stall's free taste, once a day each; tasting all four adds the Marktgang.</summary>
    public const int StampBatzen = 6, StampPoints = 20, MarktgangBatzen = 12, MarktgangPoints = 60;

    /// <summary>The most a day's stall-hop can pay: four tastes and the Marktgang.</summary>
    public const int DayMax = Stalls * StampBatzen + MarktgangBatzen;

    /// <summary>The confetti fight: a throw within <see cref="ThrowReach"/> of the site counts, <see cref="ThrowGap"/> s apart; every <see cref="ThrowsPerPlay"/>th pays.</summary>
    public const double ThrowReach = 120, ThrowGap = 0.25;

    public const int ThrowsPerPlay = 5, ThrowBatzen = 2, ThrowPoints = 8, ConfettiDayCap = 16;

    /// <summary>The whole token cap of the festival: what its two games can pay in its one day.</summary>
    public const int TokenCap = DayMax + ConfettiDayCap;

    /// <summary>The stalls of a market held at (<paramref name="e"/>, <paramref name="n"/>): a row along an east-west lane, counters facing south (the lane).</summary>
    public static ZibeleStall[] Layout(double e, double n)
    {
        var list = new ZibeleStall[Stalls];
        for (int i = 0; i < Stalls; i++)
            list[i] = new ZibeleStall(i, (ZibeleStallKind)i, e + (i - 1.5) * Spacing, n + RowOffset, 0f);
        return list;
    }

    public static ZibeleStall[] Layout(ResolvedSite site) => Layout(site.E, site.N);

    public static double Dist(double e, double n, double e2, double n2) => Math.Sqrt((e - e2) * (e - e2) + (n - n2) * (n - n2));

    /// <summary>The nearest stall within <paramref name="reach"/> of a body, or -1.</summary>
    public static int NearestStall(ResolvedSite site, double e, double n, double reach = StallReach)
    {
        int best = -1;
        double bestD = reach * reach;
        foreach (var s in Layout(site))
        {
            double d = (s.E - e) * (s.E - e) + (s.N - n) * (s.N - n);
            if (d <= bestD) { bestD = d; best = s.Index; }
        }
        return best;
    }

    public static bool NearSite(ResolvedSite site, double e, double n, double slack = 0) => Dist(e, n, site.E, site.N) <= ThrowReach + slack;

    // ---- the wares -------------------------------------------------------------------------------------------

    public static ItemId ItemOf(ZibeleStallKind kind) => kind switch
    {
        ZibeleStallKind.Zopf => ItemId.Zwiebelzopf,
        ZibeleStallKind.Chueche => ItemId.Zibelechueche,
        ZibeleStallKind.Mehlsuppe => ItemId.Mehlsuppe,
        _ => ItemId.Konfettiei,
    };

    /// <summary>How many of the item one purchase hands over: six confetti eggs, one of anything else.</summary>
    public static int CountOf(ZibeleStallKind kind) => kind == ZibeleStallKind.Konfetti ? 6 : 1;

    /// <summary>What one purchase costs, Batzen (1 Batzen = CHF 1 when converted).</summary>
    public static int PriceOf(ZibeleStallKind kind) => kind switch
    {
        ZibeleStallKind.Zopf => 6,
        ZibeleStallKind.Chueche => 4,
        ZibeleStallKind.Mehlsuppe => 3,
        _ => 3,
    };

    public static string WareName(ZibeleStallKind kind) => kind switch
    {
        ZibeleStallKind.Zopf => "Zwiebelzopf",
        ZibeleStallKind.Chueche => "Zibelechueche",
        ZibeleStallKind.Mehlsuppe => "Mehlsuppe",
        _ => "6 confetti eggs",
    };

    public static string StallName(ZibeleStallKind kind) => kind switch
    {
        ZibeleStallKind.Zopf => "Onion braid stall",
        ZibeleStallKind.Chueche => "Onion tart stall",
        ZibeleStallKind.Mehlsuppe => "Mehlsuppe kettle",
        _ => "Konfetti stall",
    };

    // ---- the stall-hop ---------------------------------------------------------------------------------------

    public const int AllStamps = (1 << Stalls) - 1;

    public static bool Stamped(int mask, int stall) => stall is >= 0 and < Stalls && (mask & (1 << stall)) != 0;

    public static int Stamp(int mask, int stall) => stall is >= 0 and < Stalls ? mask | (1 << stall) : mask;

    public static int Count(int mask)
    {
        int n = 0;
        for (int i = 0; i < Stalls; i++) if ((mask & (1 << i)) != 0) n++;
        return n;
    }
}
