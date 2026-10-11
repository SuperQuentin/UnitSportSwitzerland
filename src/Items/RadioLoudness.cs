using System;

namespace UnitSport.Items;

// Plain C#, no Godot: linked into the unit tests (tests/UnitSportSwitzerland.Tests, RadioLoudnessTests).

/// <summary>
/// A radio's own volume (#734), 0..1, the same for everyone: how loud it plays and how far it
/// reaches. Its speaker's gain and range, and the radius things react to its beat in
/// (<see cref="BeatField"/>), all come from here, so what you hear and what moves agree.
/// The player's Settings Music volume stays their own level on top (the Music bus).
/// </summary>
public static class RadioLoudness
{
    /// <summary>A radio's volume until someone turns it: the old fixed loudness and reach.</summary>
    public const float Default = 0.6f;

    /// <summary>Reach at volume 0 and at 1, m: a whisper at the foot of the radio, a block party.</summary>
    public const float MinRadius = 12f, MaxRadius = 60f;

    public static float Clamp(float volume) => float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : Default;

    /// <summary>How far it is heard and things react, m: <see cref="Default"/> gives about 41 m.</summary>
    public static float Radius(float volume) => MinRadius + (MaxRadius - MinRadius) * Clamp(volume);

    /// <summary>The speaker's gain over its base, dB: 0 at <see cref="Default"/>, about −20 near silent, +4.4 at full.</summary>
    public static float Db(float volume) => 20f * MathF.Log10(Math.Max(Clamp(volume), 0.06f) / Default);

    /// <summary>
    /// How strongly the music moves something <paramref name="distance"/> m from a radio of
    /// <paramref name="volume"/>, 0..1: full in the near third of its reach, fading to nothing at the edge.
    /// </summary>
    public static float Reach(float distance, float volume)
    {
        float r = Radius(volume);
        if (distance >= r) return 0f;
        float x = Math.Clamp((distance - r * 0.33f) / (r * 0.67f), 0f, 1f);
        return (1f - x) * (1f - x);
    }
}
