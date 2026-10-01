using UnitSport.Terrain.Format;

namespace UnitSport.BattleRoyale;

/// <summary>A match's square of Switzerland: centre in LV95, side in metres, named after its biggest town.</summary>
public sealed record BrArea(double E, double N, float Side, string Name)
{
    public bool Contains(double e, double n) => Math.Abs(e - E) <= Side * 0.5 && Math.Abs(n - N) <= Side * 0.5;
}

/// <summary>
/// Picks a match region (docs/notes/br/region.md). Pure: everything comes in as arguments, so the
/// <c>--brcheck</c> self-test runs it over hundreds of seeds without a world.
/// <para>
/// A candidate is a square around a town. It is rejected when a tile is missing, when it holds
/// fewer than <see cref="MinBuildings"/> buildings in its towns, when more than 30 % of its tiles
/// reach above 2,400 m, or when it lies within <see cref="RepeatKm"/> km of a recent region. The
/// rest are scored: buildings (a city counts no more than a large town), how many villages share
/// them, how much the ground rises and falls. The best of <see cref="Tries"/> draws wins.
/// </para>
/// </summary>
public static class BrRegion
{
    public const int MinBuildings = 120;
    public const double RepeatKm = 8;
    public const int Tries = 60;
    private const float HighMetres = 2400f;

    /// <summary>The side for a field of <paramref name="players"/>: 5 km under 10, 6 up to 20, 7 beyond.</summary>
    public static float SideFor(int players) => players < 10 ? 5000f : players <= 20 ? 6000f : 7000f;

    /// <param name="tiles">The world's tiles (1 km, by LV95 km); empty for a world that is all generated.</param>
    /// <param name="recent">Centres of the last rounds.</param>
    /// <param name="fallback">The centre to use when there is no town to go by (a generated world).</param>
    public static BrArea Pick(int seed, float side, IReadOnlyList<Place> places, IReadOnlyList<ManifestTile> tiles,
        IReadOnlyList<(double E, double N)> recent, (double E, double N) fallback)
    {
        var rng = new Random(seed);
        var towns = places.Where(p => p.Kind == PlaceKind.Town && p.Buildings >= 15).ToList();
        if (towns.Count == 0 || tiles.Count == 0)
        {
            // generated ground: around the default spawn, whose valley the generated network serves
            double e = fallback.E + (rng.NextDouble() * 2 - 1) * 1500, n = fallback.N + (rng.NextDouble() * 2 - 1) * 1500;
            return new BrArea(Math.Round(e / 50) * 50, Math.Round(n / 50) * 50, side, "Generated valley");
        }

        var byTile = new Dictionary<(int E, int N), ManifestTile>();
        foreach (var t in tiles) byTile[(t.E, t.N)] = t;

        BrArea? best = null;
        double bestScore = double.MinValue;
        int valid = 0;
        for (int i = 0; i < Tries * 4 && i < towns.Count * 4; i++)
        {
            var town = towns[rng.Next(towns.Count)];
            // not dead centre on the town: the square is shifted up to a quarter of its side
            double e = town.E + (rng.NextDouble() - 0.5) * side * 0.5, n = town.N + (rng.NextDouble() - 0.5) * side * 0.5;
            e = Math.Round(e / 50) * 50;
            n = Math.Round(n / 50) * 50;
            var area = new BrArea(e, n, side, town.Name);
            if (Score(area, towns, byTile, recent) is not { } s) continue;
            if (s > bestScore)
            {
                bestScore = s;
                // named after the biggest town inside
                var biggest = towns.Where(t => area.Contains(t.E, t.N)).MaxBy(t => t.Buildings);
                best = area with { Name = biggest?.Name ?? town.Name };
            }
            if (++valid >= Tries) break;
        }
        return best ?? new BrArea(towns[0].E, towns[0].N, side, towns[0].Name);
    }

    /// <summary>The candidate's score, or null when a hard rule rejects it.</summary>
    public static double? Score(BrArea area, IReadOnlyList<Place> towns, IReadOnlyDictionary<(int E, int N), ManifestTile> tiles,
        IReadOnlyList<(double E, double N)> recent)
    {
        foreach (var (re, rn) in recent)
            if (Math.Sqrt((re - area.E) * (re - area.E) + (rn - area.N) * (rn - area.N)) < RepeatKm * 1000) return null;

        double half = area.Side * 0.5;
        int e0 = (int)Math.Floor((area.E - half) / 1000), e1 = (int)Math.Floor((area.E + half - 1) / 1000);
        int n0 = (int)Math.Floor((area.N - half) / 1000), n1 = (int)Math.Floor((area.N + half - 1) / 1000);
        int count = 0, high = 0;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int e = e0; e <= e1; e++)
            for (int n = n0; n <= n1; n++)
            {
                // a tile at sea level is off the edge of the data (beyond the border): not part of the country
                if (!tiles.TryGetValue((e, n), out var t) || t.Min <= 1f) return null;
                count++;
                if (t.Max > HighMetres) high++;
                lo = Math.Min(lo, (float)t.Max);
                hi = Math.Max(hi, (float)t.Max);
            }
        if (high > count * 0.3) return null;

        int buildings = 0, villages = 0;
        foreach (var t in towns)
            if (area.Contains(t.E, t.N))
            {
                buildings += t.Buildings;
                villages++;
            }
        if (buildings < MinBuildings) return null;

        // a city is a maze, not a better arena: count up to 3,000 buildings
        return Math.Log(Math.Min(buildings, 3000)) + 0.6 * Math.Min(villages, 6) + Math.Min(hi - lo, 1500f) / 600.0;
    }
}
