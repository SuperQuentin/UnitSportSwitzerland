using System.Globalization;

namespace UnitSport.Items;

/// <summary>
/// A CD playing in a radio that is not lying in the world: the one in a player's hand, or a car's
/// (<c>FootPlayer.CarCd</c>, #211). Carried as a string — in the radio's <see cref="ItemStack.Data"/>
/// (so throwing it, picking it up or saving the inventory keeps the music going) and in a
/// replicated <c>FootPlayer</c> property (so everyone near hears what the holder or driver plays).
/// No string means silence.
/// </summary>
/// <param name="CdId">Library CD (&gt; 0) or a personal one (&lt; 0, only its owner has the file).</param>
/// <param name="StartedAt">Server clock (<c>Net.ClockSync.ServerNow</c>) at which the CD began.</param>
/// <param name="Length">Seconds the CD lasts; it falls silent after, unless <paramref name="Mode"/> carries on.</param>
/// <param name="Mode">What the owner does when the CD ends (<see cref="RadioQueue"/>).</param>
public readonly record struct RadioPlay(int CdId, double StartedAt, float Length, RadioMode Mode = RadioMode.Once)
{
    /// <summary>"cd;at;len", plus ";mode" when it is not <see cref="RadioMode.Once"/> (older strings stay valid).</summary>
    public string Encode() => Mode == RadioMode.Once
        ? string.Create(CultureInfo.InvariantCulture, $"{CdId};{StartedAt:R};{Length:R}")
        : string.Create(CultureInfo.InvariantCulture, $"{CdId};{StartedAt:R};{Length:R};{(int)Mode}");

    /// <summary>Still within the CD at <paramref name="serverNow"/>.</summary>
    public bool Sounding(double serverNow) => serverNow - StartedAt < Length;

    public static RadioPlay? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split(';');
        if (parts.Length is < 3 or > 4
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cd) || cd == 0
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double at)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float length)
            || !double.IsFinite(at) || !float.IsFinite(length))
            return null;
        var mode = RadioMode.Once;
        if (parts.Length == 4 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m))
            mode = RadioQueue.Clamp(m);
        return new RadioPlay(cd, at, length, mode);
    }
}
