using System.Collections.Concurrent;

namespace UnitSport.Core;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// FNV-1a over a string's UTF-16 units: the seed every peer derives from a building key. Never
/// <c>string.GetHashCode</c>, which .NET randomises per process. <c>InteriorGenerator.StableHash</c>
/// is this function, so plans, loot and shops all agree.
///
/// <para>
/// <b>Buildings on leaves</b> (<c>--frame earth</c>, #1658): a leaf's buildings are keyed by its stand-in tile and their
/// index in the leaf (<c>Terrain.Leaves.LeafStandIn</c>), names no other build or world shares, so every choice seeded
/// from them (the floor plan, shop or bank, cellar, locks, loot, a site's crane) came out other than for the same house
/// elsewhere. <see cref="Alias"/> names each such key after the building's Earth identity (C5's <c>BuildingId</c>, its
/// footprint's centre, #1514), and a seed that starts with the key (<c>key</c>, <c>key|what</c>, <c>key#n</c>, a door's
/// <c>key_slot</c>) hashes as if it started with that name instead: one identity for every per-building choice, the one
/// G2 D re-keys the stores by. Only stand-in keys are looked up (their tile's E has six digits, an LV95 tile's four), so
/// the flat world's seeds are untouched until D deletes them.
/// </para>
/// </summary>
public static class Fnv
{
    public static int Hash(string s)
    {
        if (_aliased && StandIn(s) is { } seeded) s = seeded;
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return (int)h;
    }

    /// <summary>The hash as a fraction in [0, 1): a stable "dice roll" for one question about one key.</summary>
    public static double Unit(string s) => (uint)Hash(s) / 4294967296.0;

    private static readonly ConcurrentDictionary<string, string> Aliases = new();
    private static volatile bool _aliased;

    /// <summary>
    /// Seeds from <paramref name="key"/> (a stand-in building key, <c>E_N_I</c>) hash as from <paramref name="name"/>
    /// from now on (#1658). Every peer registers the same pairs from the same leaves, so they keep agreeing.
    /// </summary>
    public static void Alias(string key, string name)
    {
        Aliases[key] = name;
        _aliased = true;
    }

    /// <summary>The name a seed stands for: null when it does not start with an aliased key.</summary>
    internal static string? StandIn(string s)
    {
        // a stand-in key's tile E has six digits ("100123_..."); anything else is no stand-in
        if (s.Length < 8 || s[6] != '_') return null;
        for (int i = 0; i < 6; i++) if (s[i] is < '0' or > '9') return null;
        // the key: E_N_I, up to the first '|' or '#', or a door's fourth "_slot"
        int end = s.IndexOfAny(Separators);
        if (end < 0) end = s.Length;
        int us = 0, third = -1;
        for (int i = 0; i < end; i++)
            if (s[i] == '_' && ++us == 3) { third = i; break; }
        int keyEnd = third >= 0 ? third : end;
        return Aliases.TryGetValue(s[..keyEnd], out var name) ? name + s[keyEnd..] : null;
    }

    private static readonly char[] Separators = ['|', '#'];
}
