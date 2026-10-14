using UnitSport.Terrain.Construction;

namespace UnitSport.Loot;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>The kinds of thing a Battle Royale match fills (#194). Sent as an int: append only.</summary>
public enum MatchTable
{
    Furniture = 0, GunLocker = 1, Safe = 2, Supply = 3, Military = 4, Airdrop = 5,
    // the outdoor sites (#198)
    Bunker = 6, HighSeat = 7, HayStash = 8, SacBox = 9, Wreck = 10, FishingHut = 11,
}

/// <summary>
/// What a building site's yard container (#627) rolls during a Battle Royale match: as a building
/// in the match region rolls <see cref="MatchLoot"/> instead of its free-roam table, so does its
/// yard, seeded with the match epoch, its taken marks in the match's memory (<c>LootService</c>).
/// </summary>
public static class YardMatch
{
    /// <summary>
    /// The match table of a yard container: a supply crate's, whose building materials (planks,
    /// stone) suit a site, for the skips and the materials alike.
    /// </summary>
    public static MatchTable TableFor(SiteStockKind kind) => MatchTable.Supply;

    /// <summary>
    /// The match table yard container of site <paramref name="building"/> rolls, or null when no
    /// match covers it and it rolls the yard's own table (<c>LootTables.YardRoll</c>).
    /// <paramref name="matchEpoch"/> is <c>LootTables.MatchEpoch</c>.
    /// </summary>
    public static MatchTable? MatchTableOf(string building, SiteStockKind kind, Func<string, long?>? matchEpoch) =>
        matchEpoch?.Invoke(building) != null ? TableFor(kind) : null;
}
