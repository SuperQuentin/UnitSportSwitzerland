using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

/// <summary>
/// <c>--poischeck --world fixture --pois fixture</c> (#1224, quick tier): the world boots, the
/// <see cref="PoiCatalogueHost"/> loads the fixture POIs through the boot path, the queries answer
/// round the start, and a <c>pois.bin</c> written to <c>test_output/poischeck</c> loads back through a
/// <see cref="LocalChunkSource"/> (the offline and cache path) into the host with the same ids.
/// Prints <c>[poischeck] RESULT: ok|FAILED</c> and quits.
/// </summary>
public partial class PoiCheck : Node
{
    public static bool Requested => CmdArgs.Has("--poischeck");

    private const double TimeoutMs = 60_000;
    private readonly (double E, double N) _start;
    private readonly List<string> _failures = new();
    private ulong _began;
    private int _stage;
    private int _loadsBefore;
    private int _expected;
    private string[] _ids = Array.Empty<string>();

    public PoiCheck((double E, double N)? start) =>
        _start = start ?? (PoiCatalogue.FixtureE, PoiCatalogue.FixtureN);

    public override void _Ready() => _began = Time.GetTicksMsec();

    private void Expect(bool ok, string what)
    {
        GD.Print($"[poischeck] {(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failures.Add(what);
    }

    public override void _Process(double delta)
    {
        if (Time.GetTicksMsec() - _began > TimeoutMs)
        {
            Expect(false, $"timed out at stage {_stage} (loads {PoiCatalogueHost.Loads})");
            Finish();
            return;
        }
        switch (_stage)
        {
            case 0 when PoiCatalogueHost.Loads >= 1:
                CheckQueries(PoiCatalogueHost.Current);
                WriteAndReload();
                _stage = 1;
                break;
            case 1 when PoiCatalogueHost.Loads > _loadsBefore:
                var reloaded = PoiCatalogueHost.Current;
                Expect(reloaded.Count == _expected, $"pois.bin from the chunk dir: {reloaded.Count} of {_expected} POIs");
                Expect(_ids.All(id => reloaded.Resolve(id)?.Id == id), "every id resolves after the file round trip");
                Finish();
                break;
        }
    }

    private void CheckQueries(PoiCatalogue c)
    {
        _expected = PoiCatalogue.FixtureData(_start.E, _start.N).Pois.Count;
        Expect(PoiCatalogueHost.FixtureRequested, "--pois fixture given");
        Expect(c.Count == _expected, $"the fixture catalogue loaded at boot: {c.Count} POIs");
        var peak = c.OfKind(PoiKind.Peak).FirstOrDefault();
        Expect(peak != null && peak.Is(PoiFlags.Cross) && peak.Is(PoiFlags.Viewpoint), "the fixture peak has a cross and is a viewpoint");
        if (peak == null) return;
        string renamed = peak.Id.Replace("fixture-peak", "renamed-peak", StringComparison.Ordinal);
        Expect(c.Resolve(peak.Id) == peak && c.Resolve(renamed) == peak, $"{peak.Id} resolves, and with another slug");
        var near = new List<Poi>(16);
        c.Near(_start.E, _start.N, 200, near);
        var kinds = near.Select(p => p.KindTag).Order(StringComparer.Ordinal).ToList();
        Expect(kinds.SequenceEqual(new[] { "busstop", "chapel", "lake", "peak" }), $"within 200 m of the start: {string.Join(", ", kinds)}");
        c.Near(_start.E, _start.N, 5000, near, PoiKind.Lake);
        Expect(near.Count == 1 && PoiCatalogue.InLake(near[0], _start.E + 1340, _start.N, 0)
                                && !PoiCatalogue.InLake(near[0], _start.E, _start.N, 30)
                                && PoiCatalogue.InLake(near[0], _start.E, _start.N, 60),
            "the lake: its middle inside, the start 40 m from its shore");
        Expect(c.InTile(TileId.FromLv95(peak.E, peak.N)).ToArray().Contains(peak), "InTile holds the peak");
        Expect(c.Totals("VS").Tier1 == 1, "canton totals from the file");
        _ids = c.All.Select(p => p.Id).ToArray();
    }

    private void WriteAndReload()
    {
        string dir = Path.Combine(ProjectSettings.GlobalizePath("res://"), "test_output", "poischeck");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, PoiFile.FileName), PoiFile.Encode(PoiCatalogue.FixtureData(_start.E, _start.N)));
        _loadsBefore = PoiCatalogueHost.Loads;
        // the file path, not the fixture: Load reads the source unless --pois fixture, so read it as the host does
        var source = new LocalChunkSource(dir);
        System.Threading.Tasks.Task.Run(async () =>
        {
            var bytes = await source.LoadPoisAsync();
            Callable.From(() =>
            {
                if (bytes == null) Expect(false, "LocalChunkSource read no pois.bin");
                PoiCatalogueHost.Replace(bytes == null ? PoiCatalogue.Empty : PoiCatalogue.Load(bytes), "poischeck");
            }).CallDeferred();
        });
    }

    private void Finish()
    {
        GD.Print(_failures.Count == 0 ? "[poischeck] RESULT: ok" : $"[poischeck] RESULT: FAILED {string.Join("; ", _failures)}");
        SetProcess(false);
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
}
