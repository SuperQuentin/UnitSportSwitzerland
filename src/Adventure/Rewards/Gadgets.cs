using System;
using System.Collections.Generic;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>The <c>gadgets</c> section (#1227): the adventure gadgets ever crafted or granted (a lost detector can be re-crafted cheaply).</summary>
public sealed class GadgetsProgress
{
    public static readonly SectionKey<GadgetsProgress> Section = new("gadgets", Version: 1);

    /// <summary>By <c>ItemId</c> name: how many ever had.</summary>
    public Dictionary<string, int> Had { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The treasure detector's maths (#1227, rewards plan 8): a beep that quickens as the nearest reward nears.</summary>
public static class Detector
{
    public const float RangeM = 250, PingSeconds = 3, Cooldown = 10;

    /// <summary>Seconds between beeps at a distance: 1.6 s at the edge of range, 0.12 s at the spot; null out of range.</summary>
    public static float? Interval(float distanceM) =>
        distanceM > RangeM ? null : 0.12f + 1.48f * Math.Clamp(distanceM / RangeM, 0f, 1f);
}
