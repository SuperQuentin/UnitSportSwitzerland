using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// The fixture course <c>airport</c> (#422): a small airport on flat ground for the stand checks,
/// mapped the way OpenStreetMap maps a real one and planned by the same <see cref="AirportPlanner"/>.
/// Metres from the start (the spawn, on the apron's west edge), X east, Y north:
/// <list type="bullet">
/// <item>runway 09/27, 2 700 m by 45 m along Y = 300 (X -300..2400): long enough for airliners;</item>
/// <item>a parallel taxiway along Y = 150;</item>
/// <item>the terminal apron, X 0..380 and Y -250..130: six stands every 50 m from X 30, their lead-in
/// lines from the taxiway's edge (Y 120) south to the nose's stop (Y 40): A320s face south;</item>
/// <item>the cargo apron, X 400..700: two stands (C1 at X 470, C2 at X 610), nose stop at Y 0;</item>
/// <item>a landside road along Y -300 (every course has a road; the ground follows it, flat).</item>
/// </list>
/// </summary>
public static class FixtureAirport
{
    public const double RunwayY = 300, RunwayX0 = -300, RunwayX1 = 2400, RunwayWidth = 45;
    public const double TaxiwayY = 150;
    public const double ApronX0 = 0, ApronX1 = 700, ApronY0 = -250, ApronY1 = 130, CargoX0 = 400;

    public static FixtureCourse Create() => new FixtureCourse
    {
        Name = "airport",
        Extent = (RunwayX0 - 200, ApronY0 - 200, RunwayX1 + 200, RunwayY + 300),
        Cover = (x, y) => Paved(x, y) ? CoverClass.PavedArea : CoverClass.Open,
        Airports = Plan,
    }.Road(RoadClass.Road, new FixtureCourse.Pen(-200, ApronY0 - 50, FixtureCourse.FlatHeight, 0, 0).Straight(1000).Points);   // the landside road, east

    /// <summary>Runway, taxiway or apron.</summary>
    public static bool Paved(double x, double y) =>
        (x >= RunwayX0 && x <= RunwayX1 && Math.Abs(y - RunwayY) <= RunwayWidth / 2)
        || (x >= RunwayX0 && x <= RunwayX1 && Math.Abs(y - TaxiwayY) <= 20)
        || (x >= RunwayX0 && x <= RunwayX0 + 30 && y >= TaxiwayY && y <= RunwayY)    // the link at the west end
        || (x >= ApronX0 && x <= ApronX1 && y >= ApronY0 && y <= ApronY1);

    /// <summary>The stands as mapped: ref, lead-in line (from the taxiway to the nose's stop), apron name.</summary>
    public static IEnumerable<(string Ref, (double X, double Y)[] Line, string Apron)> Stands()
    {
        for (int i = 0; i < 6; i++)
            yield return ($"{i + 1}", new[] { (30.0 + 50 * i, 120.0), (30.0 + 50 * i, 40.0) }, "Terminal apron");
        yield return ("C1", new[] { (470.0, 120.0), (470.0, 0.0) }, "Cargo apron");
        yield return ("C2", new[] { (610.0, 120.0), (610.0, 0.0) }, "Cargo apron");
    }

    /// <summary>The fixture airport planned at a start in LV95, as the preprocessor plans a real one.</summary>
    public static AirportIndex Plan(double startE, double startN)
    {
        var taxiway = new List<(double E, double N)> { (startE + RunwayX0, startN + TaxiwayY), (startE + RunwayX1, startN + TaxiwayY) };
        var taxiways = new List<IReadOnlyList<(double E, double N)>> { taxiway };
        var candidates = new List<AirportPlanner.Candidate>();
        foreach (var (reference, line, apron) in Stands())
        {
            var pts = line.Select(p => (startE + p.X, startN + p.Y)).ToList();
            if (AirportPlanner.FromLine(pts, taxiways) is { } s)
                candidates.Add(new(reference, s.E, s.N, s.Heading, apron, apron.StartsWith("Cargo", StringComparison.Ordinal)));
        }
        double Ground(double e, double n) => FixtureCourse.FlatHeight;
        var airport = new Airport { Name = "Fixture", Code = "LSXX", E = startE + 1000, N = startN + 100 };
        airport.Runways.Add(AirportPlanner.Profile("09/27", startE + RunwayX0, startN + RunwayY, startE + RunwayX1, startN + RunwayY, RunwayWidth, Ground));
        airport.Stands = AirportPlanner.Choose(airport.Code, candidates, (e, n) => Paved(e - startE, n - startN), Ground);
        return new AirportIndex { Airports = { airport } };
    }
}
