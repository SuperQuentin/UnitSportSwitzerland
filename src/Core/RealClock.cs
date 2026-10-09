using Godot;

namespace UnitSport.Core;

/// <summary>
/// Real time: this process's own monotonic wall clock, in seconds (#579). The third of the three
/// clocks, and the only one nothing can bend.
///
/// <para>
/// <see cref="Engine.TimeScale"/> scales the <c>delta</c> the engine hands to every
/// <c>_Process</c> and <c>_PhysicsProcess</c> in the whole process, and Godot offers no unscaled
/// per-frame delta. So a cadence accumulated from <c>delta</c> stretches by <c>1 / TimeScale</c>:
/// at 0.1x a 2 s period becomes 20 s. That is wrong for anything whose pace belongs to the wall
/// clock rather than to the world — a ping, a LAN broadcast, a poll of a worker thread, a stream
/// scan — and it is worst in <c>Net.ClockSync</c>, whose pings are what the other two clocks are
/// derived from.
/// </para>
///
/// <para>
/// Read it as a <b>deadline</b>, not as a delta: <c>if (RealClock.Now &lt; _nextAt) return;</c>
/// then <c>_nextAt = RealClock.Now + Period;</c>. A deadline is right however often it is polled,
/// where an accumulator silently under-counts the frames on which nobody looked.
/// </para>
///
/// <para>
/// Unlike <see cref="GameClock.Now"/> this does not speed up under <c>--fixed-fps</c> either: a
/// check waiting on a worker thread wants exactly this clock, because the threads do not speed up
/// (<c>docs/notes/general/fast-checks.md</c>). Static, because a process has one wall clock.
/// </para>
/// </summary>
public static class RealClock
{
    /// <summary>Seconds since this process started, never scaled, never going backwards.</summary>
    public static double Now => Time.GetTicksUsec() / 1_000_000.0;
}
