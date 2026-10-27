using System;
using System.Globalization;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>A chest's tier: what it holds and how often it is sealed (<see cref="ChestLoot"/>). Saved as a number: append only.</summary>
public enum ChestTier : byte { Common = 0, Fine = 1, Precious = 2, Luxurious = 3 }

/// <summary>What a reward spot is. Saved in no file (spots are computed), but sent as a number: append only.</summary>
public enum SpotKind : byte { Chest = 0, Edelweiss = 1, Cairn = 2 }

/// <summary>The small challenge a sealed chest asks for before it opens (<see cref="SealBook"/>). Append only.</summary>
public enum SealKind : byte
{
    None = 0,
    /// <summary>Höhenfeuer: light three wood stacks within 90 s (1 August bonfires).</summary>
    Bonfire = 1,
    /// <summary>A ring run: four to six rings in order, in a time.</summary>
    Rings = 2,
    /// <summary>Two stone plates held at once: two players, or a player and a dropped item.</summary>
    Plates = 3,
    /// <summary>Four cowbells rung in the order a fifth plays.</summary>
    Bells = 4,
    /// <summary>A guide animal (a marmot or a bearded vulture) leads to it: its arrival unseals.</summary>
    Guide = 5,
}

/// <summary>
/// One reward in the world (#1227, rewards plan 1): a chest, an edelweiss or a cairn. Computed, never stored
/// (<see cref="RewardPlacer"/>): the same on every peer for the same tile. <see cref="Alt"/> is the coarse
/// terrain's height there (a client snaps the drawn height to its fine grid). <see cref="PoiId"/> is the
/// canonical POI it stands at, if any.
/// </summary>
public sealed record RewardSpot(string Id, SpotKind Kind, double E, double N, float Alt,
    ChestTier Tier = ChestTier.Common, SealKind Seal = SealKind.None, string? PoiId = null, ulong Hash = 0, string Canton = "")
{
    /// <summary>The 1 km tile the spot stands in.</summary>
    public (int E, int N) Tile => ((int)Math.Floor(E / 1000), (int)Math.Floor(N / 1000));
}

/// <summary>
/// The rewards' shared rules (#1227): ids, editions, reaches, the hash. Every number a server check and a
/// client prompt both use lives here, so they cannot drift.
/// </summary>
public static class RewardRules
{
    /// <summary>How close a body's server position must be to a chest or an edelweiss (horizontal metres): the plan's 3 m and the published position's lag.</summary>
    public const float ClaimReachM = 4.5f;
    /// <summary>How close to a seal's stone, plate, bell or burrow (the plan's 8 m).</summary>
    public const float PropReachM = 8f;
    /// <summary>A client offers E at a chest, the gather hold at an edelweiss, within this (horizontal).</summary>
    public const float AskReachM = 2.4f;
    /// <summary>The height a claim may be off the spot's coarse height (a 10 m grid on a steep face).</summary>
    public const float ClaimSlackUpM = 18f;
    /// <summary>Claims a peer may make each second (the plan's rate limit).</summary>
    public const int ClaimsPerSecond = 4;

    /// <summary>
    /// The id of a spot anchored to the world: the kind's prefix and its 10 m cell's LV95 corner
    /// (<c>ch:2600150_1200340</c>), so it survives a reshuffle of anything else on the tile.
    /// </summary>
    public static string CellId(string prefix, double e, double n) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}:{(long)Math.Floor(e / 10) * 10}_{(long)Math.Floor(n / 10) * 10}");

    /// <summary>The id of a chest standing at a POI: <c>ch:&lt;poiId&gt;:&lt;slot&gt;</c> (a re-run of the preprocessor that moves the POI keeps it).</summary>
    public static string PoiChestId(string poiId, int slot) => string.Create(CultureInfo.InvariantCulture, $"ch:{poiId}:{slot}");

    public const string ChestPrefix = "ch", EdelweissPrefix = "ew", CairnPrefix = "cn";

    /// <summary>A Luxurious chest's edition (decided Oct 2026): a new one every 1 August (Swiss National Day), keyed by the year it opened.</summary>
    public static int Edition(DateTime utc) => utc.Month >= 8 ? utc.Year : utc.Year - 1;

    /// <summary>The id a chest is opened under: a Luxurious one's carries its edition, <c>ch:&lt;id&gt;#2026</c>, so it opens once a year.</summary>
    public static string OpenId(RewardSpot chest, int edition) =>
        chest.Tier == ChestTier.Luxurious ? string.Create(CultureInfo.InvariantCulture, $"{chest.Id}#{edition}") : chest.Id;

    /// <summary>The spot id of an opened id (the edition cut off).</summary>
    public static string SpotIdOf(string openId)
    {
        int hash = openId.LastIndexOf('#');
        return hash > 0 ? openId[..hash] : openId;
    }

    /// <summary>
    /// The tile a spot id names, without placing anything: a cell id carries its metres; a POI chest or a
    /// cairn needs the POI's position (<paramref name="poiAt"/>). False for an id of no known shape.
    /// </summary>
    public static bool TryTile(string id, Func<string, (double E, double N)?> poiAt, out (int E, int N) tile)
    {
        tile = default;
        id = SpotIdOf(id);
        int colon = id.IndexOf(':');
        if (colon != 2 || id.Length < 6) return false;
        string prefix = id[..2], rest = id[3..];
        if (prefix is not (ChestPrefix or EdelweissPrefix or CairnPrefix)) return false;
        int us = rest.IndexOf('_');
        if (us > 0 && rest.IndexOf(':') < 0
            && long.TryParse(rest[..us], NumberStyles.Integer, CultureInfo.InvariantCulture, out long e)
            && long.TryParse(rest[(us + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
        {
            tile = ((int)Math.Floor(e / 1000.0), (int)Math.Floor(n / 1000.0));
            return true;
        }
        // ch:<poi>:<slot> or cn:<poi>: the POI id itself holds a colon (kind:slug.hash)
        string poi = prefix == ChestPrefix ? rest[..Math.Max(0, rest.LastIndexOf(':'))] : rest;
        if (poi.Length == 0 || poiAt(poi) is not { } at) return false;
        tile = ((int)Math.Floor(at.E / 1000), (int)Math.Floor(at.N / 1000));
        return true;
    }
}

/// <summary>
/// The rewards' hash (#1227): FNV-1a 64 over the inputs, like <c>OccasionHash</c>, and a splitmix64 stream
/// from it. One stream per spot, seeded from its own cell, so adding a kind never reshuffles another.
/// </summary>
public static class AdventureHash
{
    private const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

    public static ulong Of(long a, long b, ulong salt)
    {
        ulong h = Offset;
        h = Mix(h, (ulong)a);
        h = Mix(h, (ulong)b);
        h = Mix(h, salt);
        return Finish(h);
    }

    public static ulong Of(string s, ulong salt = 0)
    {
        ulong h = Offset;
        foreach (char c in s)
        {
            h ^= c;
            h *= Prime;
        }
        return Finish(Mix(h, salt));
    }

    /// <summary>A chest's roll for one player: the chest, the player's key, and how many chests it has opened (the plan's per-player seed).</summary>
    public static ulong Of(string chestId, string playerKey, long count) => Of(chestId + "|" + playerKey, (ulong)count);

    private static ulong Mix(ulong h, ulong v)
    {
        for (int i = 0; i < 8; i++)
        {
            h ^= (byte)(v >> (i * 8));
            h *= Prime;
        }
        return h;
    }

    // FNV's low bits are weak for consecutive inputs: one splitmix finaliser spreads them
    private static ulong Finish(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }

    /// <summary>A <see cref="Random"/> seeded from a hash (both halves folded in).</summary>
    public static Random Rng(ulong h) => new((int)(h ^ (h >> 32)) & int.MaxValue);
}

/// <summary>A deterministic stream of numbers from a hash (splitmix64): the placer's, identical on every runtime.</summary>
public struct SpotRng
{
    private ulong _state;

    public SpotRng(ulong seed) => _state = seed;

    public ulong NextULong()
    {
        ulong z = _state += 0x9e3779b97f4a7c15UL;
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }

    /// <summary>[0, 1).</summary>
    public double Next() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>[min, max).</summary>
    public double Range(double min, double max) => min + (max - min) * Next();

    /// <summary>[0, n).</summary>
    public int Below(int n) => n <= 1 ? 0 : (int)(NextULong() % (ulong)n);
}
