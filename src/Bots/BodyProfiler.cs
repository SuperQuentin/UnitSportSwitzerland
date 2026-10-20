using System;
using System.Diagnostics;
using Godot;

namespace UnitSport.Bots;

/// <summary>
/// <c>--botprofile</c> on a server (#842): what each block of a bot body's frame costs, in time and in
/// bytes allocated, summed over every bot and printed every 5 s as per-bot-per-frame figures
/// (<c>[botprofile]</c>). A no-op without the flag; never on a player's body.
/// </summary>
public static class BodyProfiler
{
    public static readonly bool On = Core.CmdArgs.Has("--botprofile");

    public enum Slot { Physics, Deck, Void, Separate, Health, Walk, Ride, Process, Wade, Publish, Pose, Deck2, Brains, Count }

    private static readonly long[] Ticks = new long[(int)Slot.Count];
    private static readonly long[] Bytes = new long[(int)Slot.Count];
    private static long _bodyFrames;
    private static double _reportAt;

    /// <summary>Times the block it is disposed after (<c>using var _ = BodyProfiler.Time(...)</c>): nothing when off or not a bot.</summary>
    public static Timer Time(Slot slot, bool bot) => On && bot ? new Timer(slot, true) : default;

    public readonly ref struct Timer
    {
        private readonly Slot _slot;
        private readonly bool _on;
        private readonly long _t0, _b0;

        public Timer(Slot slot, bool on)
        {
            _slot = slot;
            _on = on;
            _t0 = Stopwatch.GetTimestamp();
            _b0 = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            if (!_on) return;
            Ticks[(int)_slot] += Stopwatch.GetTimestamp() - _t0;
            Bytes[(int)_slot] += GC.GetAllocatedBytesForCurrentThread() - _b0;
        }
    }

    /// <summary>One bot body ran one physics frame (the denominator).</summary>
    public static void CountFrame() { if (On) _bodyFrames++; }

    /// <summary>Every 5 s (the director calls it): the figures, then a fresh count.</summary>
    public static void Report(double now)
    {
        if (!On || now < _reportAt) return;
        _reportAt = now + 5;
        if (_bodyFrames == 0) return;
        var line = new System.Text.StringBuilder("[botprofile] per bot per frame:");
        for (int i = 0; i < (int)Slot.Count; i++)
        {
            if (Ticks[i] == 0 && Bytes[i] == 0) continue;
            double ms = Ticks[i] * 1000.0 / Stopwatch.Frequency / _bodyFrames;
            line.Append(FormattableString.Invariant($" {(Slot)i} {ms:F3}ms {Bytes[i] / (double)_bodyFrames:F0}B"));
            Ticks[i] = 0;
            Bytes[i] = 0;
        }
        line.Append(FormattableString.Invariant($" ({_bodyFrames} body frames)"));
        _bodyFrames = 0;
        GD.Print(line.ToString());
    }
}
