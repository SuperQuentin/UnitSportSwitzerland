using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

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
            Replace(PoiCatalogue.Fixture(e, n), "fixture");
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
            Callable.From(() => Replace(catalogue, from)).CallDeferred();
        });
    }

    /// <summary>Leaving to the title (<c>WorldStatics</c>): no places, no listeners of the old world.</summary>
    public static void Reset()
    {
        Current = PoiCatalogue.Empty;
        Changed = null;
        Loads = 0;
    }

    /// <summary>Swaps a catalogue in (main thread): what a load ends with, and a probe's own file.</summary>
    public static void Replace(PoiCatalogue catalogue, string from)
    {
        Current = catalogue;
        Loads++;
        GD.Print($"[pois] {catalogue.Count} points of interest ({from})");
        Changed?.Invoke();
    }
}
