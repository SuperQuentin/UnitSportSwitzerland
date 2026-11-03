// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-fasnacht.md).

namespace UnitSport.Occasions.Festivals.Fasnacht;

/// <summary>
/// The Fasnacht's rules (#1357, part of #1269): two festivals sharing one ground, Luzern's (<c>easter-52 .. +5</c>, six days from the Schmutziger Donnerstag)
/// and Basel's (<c>easter-41 .. +2</c>, the three days from the Monday after Ash Wednesday). Each is a function of the site alone, so the server, every client
/// and the props agree with nothing sent: the <b>Larven stall</b> (a free mask and a bag of Räppli once a day), the <b>Guggenmusik stage</b> (drum, trumpet and
/// sousaphone, see <see cref="GuggenChart"/>), Räppli (confetti) thrown anywhere at the festival, and in Basel the <b>Morgestraich</b> lantern. Pure.
/// </summary>
public static class FasnachtRules
{
    /// <summary>The two festivals' ids: Luzern's and Basel's (a region without either gets the relocation rule's town).</summary>
    public const string FestivalId = "fasnacht", BaselId = "baslerfasnacht";

    /// <summary>The games: all open on day 1; the Morgestraich only in Basel's.</summary>
    public const string LarvenGame = "larven", GuggenGame = "guggen", RaeppliGame = "raeppli", MorgestraichGame = "morgestraich";

    public static bool IsFasnacht(string id) => id is FestivalId or BaselId;

    // ---- the ground ------------------------------------------------------------------------------------------

    /// <summary>The Larven stall stands this far west of the site's middle, the Guggen stage east and a little north, the Morgestraich lantern north, m.</summary>
    public const double StallOffsetE = -9, StageOffsetE = 14, StageOffsetN = 6, LanternOffsetN = 11;

    /// <summary>How near a body must be to the stall, the stage and the lantern (the server allows <see cref="ServerSlack"/> more for lag), m.</summary>
    public const double StallReach = 5, StageReach = 7, LanternReach = 6, ServerSlack = 6;

    /// <summary>A throw of Räppli counts when the thrower is this near the site's middle, m.</summary>
    public const double RaeppliReach = 160;

    public static (double E, double N) StallAt(ResolvedSite s) => (s.E + StallOffsetE, s.N);

    public static (double E, double N) StageAt(ResolvedSite s) => (s.E + StageOffsetE, s.N + StageOffsetN);

    public static (double E, double N) LanternAt(ResolvedSite s) => (s.E, s.N + LanternOffsetN);

    public static double Dist(double e, double n, (double E, double N) p) => Math.Sqrt((e - p.E) * (e - p.E) + (n - p.N) * (n - p.N));

    public static bool NearStall(ResolvedSite s, double e, double n, double slack = 0) => Dist(e, n, StallAt(s)) <= StallReach + slack;

    public static bool NearStage(ResolvedSite s, double e, double n, double slack = 0) => Dist(e, n, StageAt(s)) <= StageReach + slack;

    public static bool NearLantern(ResolvedSite s, double e, double n, double slack = 0) => Dist(e, n, LanternAt(s)) <= LanternReach + slack;

    // ---- the stall: Larven and Räppli ----------------------------------------------------------------------------

    /// <summary>Masks to choose from (<c>ItemId.LarveWaggis</c> .. <c>LarveTeufel</c> 506-509); the stall hands one out by a hash of the player and the day.</summary>
    public const int MaskKinds = 4;

    /// <summary>What the stall gives once a festival day: the mask, a bag of Räppli, and the day's Batzen and party points.</summary>
    public const int StallRaeppli = 30, StallBatzen = 6, StallPoints = 20, StallDayCap = 6;

    /// <summary>The mask (0 to <see cref="MaskKinds"/> - 1) a player is handed on a festival day: a stable hash, so a check knows it and a repeat shows the same.</summary>
    public static int MaskFor(string playerKey, int day)
    {
        uint h = 2166136261;
        foreach (char c in playerKey) h = (h ^ c) * 16777619;
        h = (h ^ (uint)day) * 16777619;
        return (int)(h % MaskKinds);
    }

    /// <summary>Räppli thrown: every <see cref="ThrowsPerPlay"/>th throw at the festival is a play (3 Batzen, 10 points), at most <see cref="RaeppliDayCap"/> Batzen a day.</summary>
    public const int ThrowsPerPlay = 10, RaeppliBatzen = 3, RaeppliPoints = 10, RaeppliDayCap = 24;

    /// <summary>Throws are believed no faster than this apart, s (the item's own cooldown is 0.35).</summary>
    public const double ThrowGap = 0.25;

    // ---- the Morgestraich -------------------------------------------------------------------------------------------

    /// <summary>
    /// Basel's Morgestraich: at 04:00 on day 1 the street lights go out and the lanterns are lit; it ends at 05:30 (the sky is up at 06:00 in this game's day).
    /// Hours of game time.
    /// </summary>
    public const double MorgestraichFrom = 4.0, MorgestraichTo = 5.5;

    public const int MorgestraichBatzen = 40, MorgestraichPoints = 100, MorgestraichDayCap = 40;

    /// <summary>Whether the Morgestraich is on: Basel's festival, its first day, between 04:00 and 05:30.</summary>
    public static bool Morgestraich(string festival, int day, double hour) =>
        festival == BaselId && day == 1 && hour >= MorgestraichFrom && hour < MorgestraichTo;

    /// <summary>Street lights are out (1 is a normal night, 0 is dark) while the Morgestraich is on; the lanterns' own light is not touched.</summary>
    public static float Lamps(string festival, int day, double hour) => Morgestraich(festival, day, hour) ? 0f : 1f;

    /// <summary>What is shouted at four in the morning.</summary>
    public const string Saying = "Morgestraich, vorwärts marsch!";

    // ---- the mask ids ----------------------------------------------------------------------------------------------------

    /// <summary>The first <c>Headwear</c> of the masks (Waggis 5, Alti Dante 6, Harlekin 7, Teufel 8, Fritschi 9, Ueli 10).</summary>
    public const int FirstHeadwear = 5, LastHeadwear = 10;
}
