using System.Diagnostics;
using System.Globalization;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Always-on stage timer (#1539). Prints one greppable line per stage when it ends:
/// <c>[stage] name=cover wall=12.30 cpu=45.60 cores=3.71 gc_pause=0.80 peak_ws_mb=4021</c>
/// (seconds; cores = cpu / wall; peak_ws_mb is the process peak so far). <see cref="Mark"/> starts a
/// top-level stage and ends the previous one (the pipeline's own stage names); <see cref="Sub"/> times
/// phases inside it, one at a time, named "stage/phase". A sub-stage can repeat (one per batch):
/// <c>tools/builder-bench.sh</c> sums the lines of a name. Only prints; no output file changes.
/// </summary>
internal static class StageClock
{
    private sealed class Slot
    {
        public string Name = "";
        public readonly Stopwatch Wall = Stopwatch.StartNew();
        public TimeSpan Cpu0, Gc0;
    }

    private static readonly object Gate = new();
    private static Slot? _top, _sub;

    /// <summary>Ends the running stage (and its sub-stage) and starts <paramref name="name"/>.</summary>
    public static void Mark(string name)
    {
        lock (Gate)
        {
            Close(ref _sub);
            Close(ref _top);
            _top = Open(name);
        }
    }

    /// <summary>Ends the running sub-stage and starts <paramref name="name"/> (null: just ends it).</summary>
    public static void Sub(string? name)
    {
        lock (Gate)
        {
            Close(ref _sub);
            if (name != null) _sub = Open(name);
        }
    }

    /// <summary>Ends everything that is running.</summary>
    public static void End() => Mark("");

    private static Slot? Open(string name)
    {
        if (name.Length == 0) return null;
        using var p = Process.GetCurrentProcess();
        return new Slot { Name = name, Cpu0 = p.TotalProcessorTime, Gc0 = GC.GetTotalPauseDuration() };
    }

    private static void Close(ref Slot? s)
    {
        if (s == null) return;
        double wall = s.Wall.Elapsed.TotalSeconds;
        using var p = Process.GetCurrentProcess();
        double cpu = (p.TotalProcessorTime - s.Cpu0).TotalSeconds;
        double gc = (GC.GetTotalPauseDuration() - s.Gc0).TotalSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[stage] name={s.Name} wall={wall:F2} cpu={cpu:F2} cores={cpu / Math.Max(wall, 1e-9):F2} gc_pause={gc:F2} peak_ws_mb={p.PeakWorkingSet64 / 1048576}"));
        s = null;
    }
}
