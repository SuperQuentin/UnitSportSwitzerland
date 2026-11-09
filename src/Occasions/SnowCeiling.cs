using System;

namespace UnitSport.Occasions;

/// <summary>
/// How much of the falling snow is drawn at a height (#1637): none above the cloud layer, so a plane,
/// the spectator high up, the UFO and the shuttle in space see no flakes. The game draws no clouds,
/// so their base is taken at <see cref="CloudBaseAboveGroundM"/> over the local ground (a winter
/// stratus deck) and the snow fades across <see cref="FadeM"/> round it rather than popping.
/// Under it the snow thins in the low valleys as before. Pure.
/// </summary>
public static class SnowCeiling
{
    /// <summary>The cloud base over the local ground, m: snow falls below it, none above.</summary>
    public const double CloudBaseAboveGroundM = 2_000;

    /// <summary>The fade's width, centred on the cloud base, m: all of it under 1,800 m, none over 2,200 m.</summary>
    public const double FadeM = 400;

    /// <summary>Altitudes (m above sea) between which the snow thickens from 45 % to all of it: valleys get less.</summary>
    public const double ThinBelowM = 700, ThickAboveM = 1_800;

    /// <summary>The share of the snow drawn, 0..1, for a camera at this altitude over ground at this altitude.</summary>
    public static float Share(double altitude, double groundAltitude)
    {
        double above = altitude - groundAltitude;
        double ceiling = 1 - Smooth(CloudBaseAboveGroundM - FadeM / 2, CloudBaseAboveGroundM + FadeM / 2, above);
        double thin = 0.45 + 0.55 * Smooth(ThinBelowM, ThickAboveM, altitude);
        return (float)(ceiling * thin);
    }

    /// <summary>The cloud deck drawn while it snows (#1643), m over the same ground: the snow fades out in its lower part.</summary>
    public const double DeckBaseAboveGroundM = 1_900, DeckTopAboveGroundM = 2_400;

    /// <summary>Camera altitudes (m) across which the deck fades out: space keeps its view of the Earth.</summary>
    public const double DeckGoneFromM = 12_000, DeckGoneAboveM = 20_000;

    /// <summary>The snowfall that makes a full overcast; lighter snow, a thinner deck.</summary>
    public const float FullDeckSnowfall = 0.3f;

    /// <summary>How much of the cloud deck is drawn, 0..1, for this snowfall and a camera at this altitude.</summary>
    public static float DeckStrength(float snowfall, double altitude) =>
        (float)(Math.Clamp(snowfall / FullDeckSnowfall, 0, 1) * (1 - Smooth(DeckGoneFromM, DeckGoneAboveM, altitude)));

    private static double Smooth(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
