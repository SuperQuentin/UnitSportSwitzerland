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
    public static readonly bool Fixed = Array.IndexOf(ProcessArgs(), "--fixed-fps") >= 0;

    /// <summary>
    /// The process's own command line, engine arguments included. On macOS the CLR hosted by Godot
    /// sees none (<c>Environment.GetCommandLineArgs</c> is empty), so <see cref="Fixed"/> was always
    /// false there and every <c>--fixed-fps</c> run (the quick tier, the trailer's renders) kept
    /// <c>Net.ClockSync</c> on the wall clock: the herds, the sightings and the signals followed how
    /// fast the machine drew (#924). There it is read from the kernel (<c>KERN_PROCARGS2</c>).
    /// </summary>
    private static string[] ProcessArgs()
    {
        var args = System.Environment.GetCommandLineArgs();
        if (args.Length > 1 || !OperatingSystem.IsMacOS()) return args;
        try { return MacArgs(System.Environment.ProcessId) ?? args; }
        catch (Exception) { return args; }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int sysctl(int[] name, uint namelen, byte[]? oldp, ref nint oldlenp, IntPtr newp, nint newlen);

    /// <summary>macOS: argv of process <paramref name="pid"/> (argc, the exec path, padding, then argv, NUL-separated).</summary>
    private static string[]? MacArgs(int pid)
    {
        int[] mib = [1 /* CTL_KERN */, 49 /* KERN_PROCARGS2 */, pid];
        nint size = 0;
        if (sysctl(mib, 3, null, ref size, IntPtr.Zero, 0) != 0 || size <= 4) return null;
        var buf = new byte[size];
        if (sysctl(mib, 3, buf, ref size, IntPtr.Zero, 0) != 0) return null;
        int argc = BitConverter.ToInt32(buf, 0), at = 4;
        while (at < size && buf[at] != 0) at++;            // the exec path
        while (at < size && buf[at] == 0) at++;            // its padding
        var list = new System.Collections.Generic.List<string>(argc);
        while (list.Count < argc && at < size)
        {
            int end = at;
            while (end < size && buf[end] != 0) end++;
            list.Add(System.Text.Encoding.UTF8.GetString(buf, at, end - at));
            at = end + 1;
        }
        return list.ToArray();
    }

    public static double Now => _base + (Engine.GetPhysicsFrames() - _baseFrames) / (double)Engine.PhysicsTicksPerSecond;

    // the time and tick count when the tick rate last changed: game time carries on from there
    private static double _base;
    private static ulong _baseFrames;

    /// <summary>
    /// Physics at <paramref name="hz"/> ticks a second from now on, game time running on without a
    /// jump (VR ticks at the headset's refresh rate, <c>XR.XrSession.ApplyRefresh</c>, #1036).
    /// </summary>
    public static void SetTickRate(int hz)
    {
        if (hz <= 0 || hz == Engine.PhysicsTicksPerSecond) return;
        _base = Now;
        _baseFrames = Engine.GetPhysicsFrames();
        Engine.PhysicsTicksPerSecond = hz;
    }

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
