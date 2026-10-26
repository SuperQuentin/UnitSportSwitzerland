using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

/// <summary>
/// Holds the session's <see cref="PoiCatalogue"/> (#1224) on the server and every client, until the
/// places service (plan section 3) takes it over: loaded from the chunk source at boot (offline, the
/// chunk dir's <c>pois.bin</c>; a client again once <c>ClientTerrainSync.PoisReceived</c> brought the
/// server's), <see cref="PoiCatalogue.Fixture"/> under <c>--pois fixture</c>, empty on the generated
/// world or without the file. <see cref="Current"/> is never null and only ever replaced whole, on the
/// main thread.
/// </summary>
public static class PoiCatalogueHost
{
    public static PoiCatalogue Current { get; private set; } = PoiCatalogue.Empty;

    /// <summary>The curated Atlas cards that resolve in <see cref="Current"/>, by POI (<c>res://assets/adventure/atlas_cards.json</c>).</summary>
    public static IReadOnlyDictionary<Poi, AtlasCard> Cards { get; private set; } = new Dictionary<Poi, AtlasCard>();
    private static Dictionary<Poi, AtlasCard> _nextCards = new();

    public const string CardsPath = "res://assets/adventure/atlas_cards.json";

    /// <summary>
    /// Reads the curated cards into a catalogue before it is shared: their aliases resolve, their POIs are
    /// landmarks. Any thread (FileAccess on <c>res://</c>).
    /// </summary>
    public static Dictionary<Poi, AtlasCard> ApplyCards(PoiCatalogue catalogue)
    {
        var byPoi = new Dictionary<Poi, AtlasCard>();
        if (catalogue.Count == 0) return byPoi;
        try
        {
            string json = Godot.FileAccess.GetFileAsString(CardsPath);
            if (string.IsNullOrEmpty(json)) return byPoi;
            var cards = AtlasFacts.ParseCards(json);
            catalogue.AddAliases(cards.Select(c => (c.Alias, c.Id)));
            foreach (var c in cards)
                if (catalogue.Resolve(c.Id) is { } poi) byPoi[poi] = c;
        }
        catch (Exception e) { GD.PushWarning($"[pois] {CardsPath}: {e.Message}"); }
        return byPoi;
    }

    /// <summary>Raised on the main thread after <see cref="Current"/> was replaced.</summary>
    public static event Action? Changed;

    /// <summary>How many loads finished (a probe waits for the first).</summary>
    public static int Loads { get; private set; }

    /// <summary><c>--pois fixture</c>: the fixture POIs instead of the file.</summary>
    public static bool FixtureRequested => CmdArgs.Value("--pois") == "fixture";

    /// <summary>
    /// Loads the catalogue from <paramref name="source"/> on a worker and swaps it in on the main
    /// thread; under <c>--pois fixture</c> builds the fixture set around <paramref name="fixtureStart"/>.
    /// </summary>
    public static void Load(IChunkSource? source, (double E, double N)? fixtureStart = null)
    {
        if (FixtureRequested)
        {
            var (e, n) = fixtureStart ?? (PoiCatalogue.FixtureE, PoiCatalogue.FixtureN);
            var fixture = PoiCatalogue.Fixture(e, n);
            // the fixture's own alias, as a curated card would give one
            fixture.AddAliases(new[] { ("peak:fixture-peak", fixture.OfKind(PoiKind.Peak)[0].Id) });
            _nextCards = new Dictionary<Poi, AtlasCard>
            {
                [fixture.OfKind(PoiKind.Peak)[0]] = new("peak:fixture-peak", fixture.OfKind(PoiKind.Peak)[0].Id, "Fixture Peak",
                    "The fixture world's own summit, with a cross on top. A check climbs it every run."),
            };
            Replace(fixture, "fixture");
            return;
        }
        // --pois <file>: a pois.bin of one's own (a region built with --pois-file), in place of the source's
        if (CmdArgs.Value("--pois") is { Length: > 0 } file && file != "fixture")
        {
            string path = ProjectSettings.GlobalizePath(file);
            Task.Run(() =>
            {
                PoiCatalogue c = PoiCatalogue.Empty;
                try { c = PoiCatalogue.Load(System.IO.File.ReadAllBytes(path)); }
                catch (Exception e) { GD.PushWarning($"[pois] {path}: {e.Message}"); }
                var cards = ApplyCards(c);
                Callable.From(() => { _nextCards = cards; Replace(c, path); }).CallDeferred();
            });
            return;
        }
        if (source == null)
        {
            Replace(PoiCatalogue.Empty, "no source");
            return;
        }
        Task.Run(async () =>
        {
            PoiCatalogue catalogue = PoiCatalogue.Empty;
            string from = "no pois.bin";
            try
            {
                if (await source.LoadPoisAsync().ConfigureAwait(false) is { } bytes)
                {
                    catalogue = PoiCatalogue.Load(bytes);
                    from = $"pois.bin, {bytes.Length / 1024} KB";
                }
            }
            catch (Exception e)
            {
                GD.PushWarning($"[pois] pois.bin does not load: {e.Message}");
                from = "a pois.bin that does not load";
            }
            var cards = ApplyCards(catalogue);
            Callable.From(() => { _nextCards = cards; Replace(catalogue, from); }).CallDeferred();
        });
    }

    /// <summary>Leaving to the title (<c>WorldStatics</c>): no places, no listeners of the old world.</summary>
    public static void Reset()
    {
        Current = PoiCatalogue.Empty;
        Cards = new Dictionary<Poi, AtlasCard>();
        Changed = null;
        Loads = 0;
    }

    /// <summary>Swaps a catalogue in (main thread): what a load ends with, and a probe's own file.</summary>
    public static void Replace(PoiCatalogue catalogue, string from)
    {
        Current = catalogue;
        Cards = _nextCards;
        _nextCards = new();
        Loads++;
        GD.Print($"[pois] {catalogue.Count} points of interest ({from})");
        Changed?.Invoke();
    }
}
