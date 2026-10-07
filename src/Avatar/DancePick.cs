using System;

namespace UnitSport.Avatar;

/// <summary>What a dancer does in one move slot (#728).</summary>
public enum DanceSlotKind : byte
{
    /// <summary>A move of the style's table, <see cref="DancePick.Index"/>.</summary>
    Table,
    /// <summary>Everyone near the music jumps on every beat together (#261).</summary>
    CrowdPogo,
    /// <summary>Everyone near the music winds up and jumps on beat four together (#261).</summary>
    CrowdJump,
    /// <summary>The first half of a break set: toprock, down to the floor, the six-step.</summary>
    BreakDown,
    /// <summary>The second half of a break set with a windmill, then the freeze and back up.</summary>
    BreakWindmill,
    /// <summary>The second half of a break set with a headspin, then the freeze and back up.</summary>
    BreakHeadspin,
}

/// <summary>
/// The music where one move slot starts (#728): the CD's section there, its number, and whether
/// that section still runs for two whole slots (a break set never straddles a section change).
/// Kinds as <c>Audio.Cd.SectionKind</c>: 0 calm, 1 groove, 2 peak, 3 chorus.
/// </summary>
public readonly record struct DanceSlotMusic(int SectionKind, int SectionIndex, bool FitsTwoSlots);

/// <summary>
/// Which move a dancer does in a move slot (#728), from hashes only, so every peer works out the
/// same pick for the same dancer on the same bar with nothing on the wire. Pure C# (tier 0 tests:
/// <c>DancePickTests</c>).
///
/// <list type="bullet">
/// <item>One slot in three is a crowd move when two or more dance to the same music (#261): the
/// same hash with no dancer in it, so they all land on it together.</item>
/// <item>Otherwise each dancer picks from the style's table, weighted by the section: a calm part
/// keeps to the calm and middle moves, a peak or chorus to the middle and big ones.</item>
/// <item>A new section is a new pick (the hash has its number in it), so moves change with the music.</item>
/// <item>Breakdance is rare: only for a style that breaks, in a peak or chorus long enough for a
/// whole set (two slots), one eligible slot in <see cref="BreakOdds"/> per dancer. The slot after
/// a set's first half is always its second half.</item>
/// </list>
/// </summary>
public static class DancePick
{
    /// <summary>One eligible slot in this many starts a break set, per dancer.</summary>
    public const uint BreakOdds = 5;

    /// <summary>Styles that break (by <c>MusicStyle</c> number): Electronic and HipHop.</summary>
    public static bool StyleBreaks(int style) => style is 2 or 3;

    /// <summary>
    /// The pick for <paramref name="slot"/>. <paramref name="now"/>, <paramref name="prev"/> and
    /// <paramref name="prev2"/> describe the music at the start of this slot and the two before
    /// (a set's second half looks back one slot, and that one back another).
    /// <paramref name="energy"/> grades the style's table moves: 0 calm, 1 middle, 2 big.
    /// </summary>
    public static (DanceSlotKind Kind, int Index) Pick(int slot, int style, uint seed, bool crowd,
        in DanceSlotMusic now, in DanceSlotMusic prev, in DanceSlotMusic prev2, ReadOnlySpan<byte> energy)
    {
        if (StartsBreak(slot - 1, style, seed, crowd, prev, prev2))
            return ((Own(slot - 1, style, seed, prev) >> 5 & 1u) == 0u ? DanceSlotKind.BreakWindmill : DanceSlotKind.BreakHeadspin, 0);
        uint shared = Shared(slot, style, now);
        if (crowd && shared % 3u == 1u)
            return ((shared >> 8) % 2u == 0u ? DanceSlotKind.CrowdJump : DanceSlotKind.CrowdPogo, 0);
        if (StartsBreak(slot, style, seed, crowd, now, prev)) return (DanceSlotKind.BreakDown, 0);
        return (DanceSlotKind.Table, TableIndex(Own(slot, style, seed, now), now.SectionKind, energy));
    }

    /// <summary>A break set starts in this slot: its own pick says so, and the slot before did not want one too.</summary>
    private static bool StartsBreak(int slot, int style, uint seed, bool crowd, in DanceSlotMusic now, in DanceSlotMusic prev) =>
        WantsBreak(slot, style, seed, crowd, now) && !WantsBreak(slot - 1, style, seed, crowd, prev);

    private static bool WantsBreak(int slot, int style, uint seed, bool crowd, in DanceSlotMusic m) =>
        StyleBreaks(style) && m.SectionKind >= 2 && m.FitsTwoSlots
        && !(crowd && Shared(slot, style, m) % 3u == 1u)
        && Own(slot, style, seed, m) % BreakOdds == 0u;

    /// <summary>
    /// The k-th move (k from the hash) among those the section allows: calm parts no big moves,
    /// peaks and choruses no calm ones, a groove anything; all of them when the filter leaves none.
    /// </summary>
    public static int TableIndex(uint own, int sectionKind, ReadOnlySpan<byte> energy)
    {
        if (energy.Length == 0) return 0;
        int n = 0;
        for (int i = 0; i < energy.Length; i++) if (Allowed(energy[i], sectionKind)) n++;
        if (n == 0) return (int)(own % (uint)energy.Length);
        int k = (int)(own % (uint)n);
        for (int i = 0; i < energy.Length; i++)
            if (Allowed(energy[i], sectionKind) && k-- == 0) return i;
        return 0;
    }

    private static bool Allowed(byte energy, int sectionKind) => sectionKind switch
    {
        0 => energy <= 1,
        2 or 3 => energy >= 1,
        _ => true,
    };

    /// <summary>The slot's hash with no dancer in it: the same for everyone at this music.</summary>
    private static uint Shared(int slot, int style, in DanceSlotMusic m) =>
        Hash((uint)slot * 2654435761u ^ (uint)style * 40503u ^ (uint)m.SectionIndex * 2246822519u);

    private static uint Own(int slot, int style, uint seed, in DanceSlotMusic m) => Hash(Shared(slot, style, m) ^ seed);

    public static uint Hash(uint h)
    {
        h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
        return h;
    }
}
