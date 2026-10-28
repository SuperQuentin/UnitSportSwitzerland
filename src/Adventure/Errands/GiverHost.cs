using UnitSport.Adventure.Places;
using UnitSport.Core;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// Holds the session's <see cref="GiverCatalogue"/> (#1267) on the server and every client: the huts, stations and landings of
/// the POI catalogue (it follows <see cref="PoiCatalogueHost"/> as the places arrive), or the fixture's eight under
/// <c>--givers fixture</c>. <see cref="Current"/> is never null and only replaced whole, on the main thread.
/// </summary>
public static class GiverHost
{
    public static GiverCatalogue Current { get; private set; } = GiverCatalogue.Empty;

    public static bool FixtureRequested => CmdArgs.Value("--givers") == "fixture";

    private static (double E, double N) _start = (PoiCatalogue.FixtureE, PoiCatalogue.FixtureN);
    private static bool _hooked;

    public static event Action? Changed;

    /// <summary>Starts following the POI catalogue; <paramref name="fixtureStart"/> is where the fixture givers stand round.</summary>
    public static void Configure((double E, double N) fixtureStart)
    {
        _start = fixtureStart;
        if (!_hooked)
        {
            _hooked = true;
            PoiCatalogueHost.Changed += Rebuild;
        }
        Rebuild();
    }

    private static void Rebuild()
    {
        Current = FixtureRequested ? GiverCatalogue.Fixture(_start.E, _start.N) : new GiverCatalogue(GiverCatalogue.FromPois(PoiCatalogueHost.Current));
        Changed?.Invoke();
    }
}
