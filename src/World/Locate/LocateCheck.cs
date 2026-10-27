using Godot;
using UnitSport.Core;

namespace UnitSport.World.Locate;

/// <summary>
/// <c>--locatecheck [q1|q2|...]</c> (#1277): offline, the world's <see cref="LocateService"/> builds its building
/// index, answers each query from the start point (<c>--at</c>, the fixture's start), and the index writes and
/// reads back through the cache format; every answer is printed. On the garage course (quick tier,
/// <c>--chunks fixture:garage --pois fixture</c>) it expects the fixture's castle and lake, its blocks and their garage doors; on a real region (<c>--chunks</c>) it is the probe to read the answers off.
/// Prints <c>[locatecheck] RESULT: ok|FAILED</c> and quits.
/// </summary>
public partial class LocateCheck : Node
{
    public static bool Requested => CmdArgs.Has("--locatecheck");

    private const double TimeoutMs = 600_000;
    private static readonly string[] Default =
    [
        "castle", "a lake", "harbour", "cinema", "ikea", "steamer", "apartment building", "construction zone",
        "church + spire", "apartment building + parking entrance", "tall building", "old house + 3 floors", "spaceship",
    ];

    private readonly (double E, double N) _from;
    private readonly List<string> _failures = new();
    private ulong _began;
    private bool _running;

    public LocateCheck((double E, double N) from) => _from = from;

    public override void _Ready() => _began = Time.GetTicksMsec();

    private void Expect(bool ok, string what)
    {
        GD.Print($"[locatecheck] {(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failures.Add(what);
    }

    public override void _Process(double delta)
    {
        if (Time.GetTicksMsec() - _began > TimeoutMs)
        {
            Expect(false, "timed out waiting for the building index");
            Finish();
            return;
        }
        if (_running || LocateService.Instance is not { } locate) return;
        locate.EnsureIndexing();
        if (locate.Buildings == null) return;
        _running = true;
        _ = Run(locate);
    }

    private async Task Run(LocateService locate)
    {
        GD.Print($"[locatecheck] index: {locate.Buildings!.Count} buildings, {(Time.GetTicksMsec() - _began)} ms after boot");
        string? asked = CmdArgs.Value("--locatecheck", notFlag: true);
        var queries = asked != null ? asked.Split('|', StringSplitOptions.RemoveEmptyEntries) : Default;
        var from = new GlobalPos(_from.E, _from.N, 0);
        foreach (string q in queries)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var (lines, failed) = await locate.Answer(q, from);
            GD.Print($"[locatecheck] /locate {q}  ({started.ElapsedMilliseconds} ms{(failed ? ", refused" : "")})");
            foreach (string line in lines) GD.Print($"[locatecheck]   {line}");
            if (asked == null) Expect(failed == (q == "spaceship"), $"'{q}' {(failed ? "refused" : "answered")}");
        }

        // the cache format, on this world's own index
        using (var ms = new MemoryStream())
        {
            locate.Buildings.Write(ms);
            ms.Position = 0;
            Expect(BuildingIndex.Read(ms)?.Count == locate.Buildings.Count, "the index reads back from its cache format");
        }

        // the chat's completion offers the nouns, then the selectors
        var completer = new ChatCompleter();
        Expect(completer.Complete("/locate chu").Any(s => s.Label == "church"), "Tab completes a noun");
        Expect(completer.Complete("/locate church ").Any(s => s.Label == "+ spire"), "Tab offers the selectors after a noun");
        Expect(completer.Complete("/locate church + par").Any(s => s.Label == "parking entrance"), "Tab completes a selector");

        if (Systems.FixtureCourse != null && asked == null)
        {
            var castle = await locate.FindAsync("castle", _from.E, _from.N, 1);
            Expect(castle.Count == 1, $"the fixture's castle is found ({castle.Count})");
            var lake = await locate.FindAsync("lake", _from.E, _from.N, 1);
            Expect(lake.Count == 1, $"the fixture's lake is found ({lake.Count})");
            Expect(locate.Buildings.Count > 0, $"the fixture's buildings are indexed ({locate.Buildings.Count})");
            // fixture:garage: blocks of flats whose garage doors always roll, checked on the real footprint
            var garages = await locate.FindAsync("apartment building + parking entrance", _from.E, _from.N, 3);
            Expect(garages.Count > 0 && garages.All(g => g.Detail.Contains("garage door")), $"the blocks' garage doors are found ({garages.Count})");
        }
        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures.Count == 0 ? "[locatecheck] RESULT: ok" : $"[locatecheck] RESULT: FAILED ({string.Join("; ", _failures)})");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
}
