using System;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// Game time: physics ticks since boot, in seconds. The test runner starts headless checks with
/// <c>--fixed-fps 60</c>, which drops real-time sync: the simulation then runs as fast as the CPU
/// allows, several times faster than the wall clock (<c>docs/notes/general/fast-checks.md</c>), except while the
/// world loads (<see cref="Pace"/>).
/// A check timing a wait or a duration reads this, never <see cref="Time.GetTicksMsec"/>, and so
/// does <c>Net.ClockSync</c> under <see cref="Fixed"/> (waves, signals, radios keep pace with the
/// physics). Ignores <see cref="Engine.TimeScale"/>.
/// </summary>
public static class GameClock
{
    /// <summary>Started with <c>--fixed-fps</c>: one process, game time decoupled from the wall clock. The process's own
    /// argv: Godot drops the engine args it consumed from <c>OS.GetCmdlineArgs</c>.</summary>
    public static readonly bool Fixed = Array.IndexOf(System.Environment.GetCommandLineArgs(), "--fixed-fps") >= 0;

    public static double Now => Engine.GetPhysicsFrames() / (double)Engine.PhysicsTicksPerSecond;

    private static ulong _lastUsec;

    /// <summary>
    /// Under <see cref="Fixed"/>, called once a frame: while <paramref name="busy"/> (the world loading,
    /// tiles building on threads) the frame is held to one physics tick of wall time, so game time
    /// cannot outrun the threads and a probe's "wait 2 s for the ground" still sees the ground.
    /// Idle, the simulation runs flat out.
    /// </summary>
    public static void Pace(bool busy)
    {
        ulong now = Time.GetTicksUsec();
        if (_lastUsec == 0) GD.Print("[clock] --fixed-fps: game time, held to real time while the world loads");
        if (busy)
        {
            ulong tick = 1_000_000UL / (ulong)Engine.PhysicsTicksPerSecond;
            if (now - _lastUsec < tick) OS.DelayUsec((int)(tick - (now - _lastUsec)));
            now = Time.GetTicksUsec();
        }
        _lastUsec = now;
    }
}
