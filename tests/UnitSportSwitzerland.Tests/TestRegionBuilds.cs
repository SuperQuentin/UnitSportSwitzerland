using UnitSport.Tools.RoadGen.TestRegion;

namespace UnitSport.Tests;

/// <summary>
/// The signal test region (#386) built once per test run for each set of flags, shared by every class that reads it (#1716).
/// Its fixtures were <c>IClassFixture</c>s, which xUnit builds once per class: seven classes built the same region side by
/// side. The tests only read the built tiles, so one build serves them all; it is deleted when the test process exits.
/// A build takes ~30 s since RoadGen is optimized in Debug too (175 s unoptimized, which made it the unit stage's critical
/// path); a disk cache between runs was tried and measured no faster on a loaded machine, so there is none.
/// </summary>
public static class TestRegionBuilds
{
    public sealed record Build(string Dir, int Code, IReadOnlyList<string> Log);

    // the planner writing its junctions (the game's --test-region), and the planner reporting over the old layout
    private static readonly Lazy<Build> Written = new(() => Run("unitsport-386-", newJunctions: true, writeJunctions: true));
    private static readonly Lazy<Build> Planned = new(() => Run("unitsport-818-", newJunctions: true, writeJunctions: false));

    public static Build Get(bool writeJunctions) => writeJunctions ? Written.Value : Planned.Value;

    private static Build Run(string prefix, bool newJunctions, bool writeJunctions)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(dir);
        var log = new List<string>();
        int code = SignalTestRegion.Run(dir, null, log.Add, newJunctions, writeJunctions);
        return new Build(dir, code, log);
    }

    private static void Delete(string dir)
    {
        foreach (string d in new[] { dir, dir + "_temp" })
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }
}
