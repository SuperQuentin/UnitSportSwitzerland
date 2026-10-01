using System.Globalization;

namespace UnitSport.Items;

/// <summary>
/// A CD playing in a radio that is not lying in the world: the one in a player's hand. Carried as
/// a string twice — in the radio's <see cref="ItemStack.Data"/> (so throwing it, picking it up or
/// saving the inventory keeps the music going) and in <c>FootPlayer.HeldRadio</c> (replicated, so
/// everyone near hears what the holder plays). No string means silence.
/// </summary>
/// <param name="CdId">Library CD (&gt; 0) or a personal one (&lt; 0, only its owner has the file).</param>
/// <param name="StartedAt">Server clock (<c>Net.ClockSync.ServerNow</c>) at which the CD began.</param>
/// <param name="Length">Seconds the CD lasts; it falls silent after.</param>
public readonly record struct RadioPlay(int CdId, double StartedAt, float Length)
{
    public string Encode() => string.Create(CultureInfo.InvariantCulture, $"{CdId};{StartedAt:R};{Length:R}");

    public static RadioPlay? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split(';');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cd) || cd == 0
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double at)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float length)
            || !double.IsFinite(at) || !float.IsFinite(length))
            return null;
        return new RadioPlay(cd, at, length);
    }
}
