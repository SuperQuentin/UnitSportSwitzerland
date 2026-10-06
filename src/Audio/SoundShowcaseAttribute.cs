using System;

namespace UnitSport.Audio;

/// <summary>
/// Puts a set of sounds in the sound player (<c>--sounds</c>, <c>src/Audio/SoundPlayer.cs</c>), which
/// finds these by reflection: tag the method, never edit a list.
///
/// <para>
/// On a static method with no parameters returning an
/// <c>IEnumerable&lt;(string Category, string Name, Func&lt;float[]&gt; Make)&gt;</c>: mono samples at
/// <see cref="Dsp.Rate"/>, made when the sound is first selected (an empty category falls back to
/// <see cref="Category"/>). Static <see cref="SfxBank"/> and <c>AudioStreamWav</c> members need no
/// tag: the player lists them by itself. A sound with arguments (a surface, an engine voice) gets a
/// set over its registry or enum, so a new value shows by itself.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SoundShowcaseAttribute : Attribute
{
    public SoundShowcaseAttribute(string category) => Category = category;

    /// <summary>The player's category for tuples that name none.</summary>
    public string Category { get; }
}
