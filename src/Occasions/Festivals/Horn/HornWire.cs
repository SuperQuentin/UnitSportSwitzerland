// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-alphorn.md).

using System.Globalization;
using System.Text;

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>A seat in a song: the chart's seed, the voice this player sings, and how many voices are in the song so far.</summary>
public readonly record struct HornSeat(int Seed, HornPart Part, int Voices);

/// <summary>What a finished song paid one voice: the score, the harmony multiplier (in hundredths), the Batzen granted and the board points.</summary>
public readonly record struct HornResult(HornPart Part, int Points, int Max, int Harmony100, int Voices, int Batzen, int BoardPoints);

/// <summary>
/// How a song crosses the wire: the blows as <c>partial,on,off;...</c> in invariant culture, milliseconds. Strict about what it takes: a
/// bad string is refused whole, never half read. Pure (tier 0).
/// </summary>
public static class HornWire
{
    public const int MaxPlays = 140, MaxChars = 4000;

    public static string Encode(IEnumerable<HornPlay> plays)
    {
        var sb = new StringBuilder();
        foreach (var p in plays.Take(MaxPlays))
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(p.Partial.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(((int)Math.Round(p.On * 1000)).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(((int)Math.Round(p.Off * 1000)).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>The blows in <paramref name="text"/>, or null when it is not a well-formed list within the limits.</summary>
    public static HornPlay[]? Decode(string text)
    {
        if (text.Length > MaxChars) return null;
        if (text.Length == 0) return [];
        var list = new List<HornPlay>();
        foreach (string item in text.Split(';'))
        {
            string[] f = item.Split(',');
            if (list.Count >= MaxPlays || f.Length != 3
                || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int partial)
                || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int on)
                || !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int off)
                || !AlphornRules.IsPartial(partial) || on < 0 || off < on || off > 600_000)
                return null;
            list.Add(new HornPlay(partial, on / 1000.0, off / 1000.0));
        }
        return [.. list];
    }

    /// <summary>The seed of the song of a festival instance's time slot: the same for everyone in it, and another for the next slot.</summary>
    public static int SeedOf(string instance, long slot)
    {
        uint h = 2166136261;
        foreach (char c in instance) h = (h ^ c) * 16777619;
        h = (h ^ (uint)slot) * 16777619;
        h = (h ^ (uint)(slot >> 32)) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }
}
