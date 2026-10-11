using System;

namespace UnitSport.Items;

// Plain C#, no Godot: linked into the unit tests (tests/UnitSportSwitzerland.Tests, RadioFocusTests).

/// <summary>
/// Several radios in earshot (#813): the one loudest at the ears plays as it is, every other one
/// ducks under it, so two songs never fight at the same level. Which one is "loudest" holds until
/// another beats it by <see cref="SwitchMarginDb"/>, so standing halfway between two radios does not
/// flip between them; the duck fades over about a second. Purely this machine's ears: nothing on
/// the network, every song stays on the shared clock.
/// </summary>
public static class RadioFocus
{
    /// <summary>How far under the focused radio the others play, dB.</summary>
    public const float DuckDb = -12f;

    /// <summary>A challenger must be this much louder at the ears to take the focus, dB.</summary>
    public const float SwitchMarginDb = 3f;

    /// <summary>How fast a radio ducks or comes back, dB/s: the whole duck in a second.</summary>
    public const float FadeDbPerSecond = 12f;

    /// <summary>The level of a source the ears are part of (one's own radio, the cabin's stereo): always the focus.</summary>
    public const float CloseDb = 1000f;

    /// <summary>
    /// The level at the ears, dB, of a source playing at <paramref name="sourceDb"/> heard from
    /// <paramref name="distance"/> m, on the speakers' inverse-distance law (full within
    /// <paramref name="unitSize"/>); minus infinity past <paramref name="maxDistance"/>, where it is not heard.
    /// </summary>
    public static float LevelAt(float sourceDb, float distance, float unitSize, float maxDistance) =>
        !(distance < maxDistance) ? float.NegativeInfinity
        : sourceDb + 20f * MathF.Log10(unitSize / MathF.Max(distance, unitSize));

    /// <summary>
    /// Which of <paramref name="levels"/> (dB at the ears; minus infinity = not heard) is the focus,
    /// given the one that was (<paramref name="current"/>, -1 for none): the loudest, unless the
    /// current one is still heard and within <see cref="SwitchMarginDb"/> of it. -1 when none is heard.
    /// </summary>
    public static int Pick(ReadOnlySpan<float> levels, int current)
    {
        int best = -1;
        float loudest = float.NegativeInfinity;
        for (int i = 0; i < levels.Length; i++)
            if (levels[i] > loudest) { loudest = levels[i]; best = i; }
        if (best < 0) return -1;
        if ((uint)current < (uint)levels.Length && levels[current] > float.NegativeInfinity
            && loudest - levels[current] < SwitchMarginDb)
            return current;
        return best;
    }

    /// <summary>One frame of a radio's duck, dB: towards 0 when it is the focus (or none is), <see cref="DuckDb"/> otherwise.</summary>
    public static float Step(float duck, bool full, float dt)
    {
        float want = full ? 0f : DuckDb, step = FadeDbPerSecond * dt;
        return Math.Abs(want - duck) <= step ? want : duck + Math.Sign(want - duck) * step;
    }
}
