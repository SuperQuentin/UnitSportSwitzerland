using System.Linq;
using UnitSport.Trailer;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The real-terrain check of the films (src/Trailer/FilmCoverage.cs, linked in, #882).</summary>
public class FilmCoverageTests
{
    [Fact]
    public void Tiles_near_a_point_are_those_whose_square_comes_within_the_radius()
    {
        // the middle of tile 2600-1200: 5 km reaches 5 tiles out on each side, not the far corners
        var tiles = FilmCoverage.TilesNear(2_600_500, 1_200_500, 5000).ToHashSet();
        Assert.Contains((2600, 1200), tiles);
        Assert.Contains((2595, 1200), tiles);
        Assert.Contains((2605, 1200), tiles);
        Assert.DoesNotContain((2606, 1200), tiles);
        Assert.DoesNotContain((2595, 1195), tiles);   // its nearest corner is 6.4 km away
        Assert.Contains((2597, 1197), tiles);         // 2.5 km, 2.5 km: 3.5 km
        Assert.Equal(101, tiles.Count);
    }

    [Fact]
    public void A_high_eye_needs_eight_kilometres_and_a_low_one_or_an_actor_five()
    {
        var high = new FilmCoverage.Need(2_600_500, 1_200_500, 3100, Eye: true);
        var low = high with { Alt = 1500 };
        var actorHigh = high with { Eye = false };
        Assert.Equal(FilmCoverage.HighRadius, FilmCoverage.RadiusFor(high));
        Assert.Equal(FilmCoverage.Radius, FilmCoverage.RadiusFor(low));
        Assert.Equal(FilmCoverage.Radius, FilmCoverage.RadiusFor(actorHigh));
    }

    [Fact]
    public void Missing_lists_each_tile_once_sorted_as_the_tiles_file_wants()
    {
        // only tile 2600-1200 and its east neighbour are real
        var points = new[]
        {
            new FilmCoverage.Need(2_600_500, 1_200_500, double.NaN, Eye: true),
            new FilmCoverage.Need(2_600_600, 1_200_500, double.NaN, Eye: false),
        };
        var missing = FilmCoverage.Missing(points, (e, n) => (e, n) is (2600, 1200) or (2601, 1200));
        Assert.DoesNotContain((2600, 1200), missing);
        Assert.Contains((2599, 1200), missing);
        Assert.Equal(missing.Distinct().Count(), missing.Count);
        Assert.Equal(missing.OrderBy(t => t.E).ThenBy(t => t.N).ToList(), missing);
        Assert.StartsWith("2595-1198\n", FilmCoverage.TilesFile(missing));
    }

    [Fact]
    public void Tiles_are_every_tile_round_the_points_once_and_the_missing_ones_among_them()
    {
        var points = new[]
        {
            new FilmCoverage.Need(2_600_500, 1_200_500, double.NaN, Eye: true),
            new FilmCoverage.Need(2_600_600, 1_200_500, double.NaN, Eye: false),
        };
        var all = FilmCoverage.Tiles(points);
        Assert.Equal(FilmCoverage.TilesNear(2_600_500, 1_200_500, FilmCoverage.Radius).Union(
            FilmCoverage.TilesNear(2_600_600, 1_200_500, FilmCoverage.Radius)).Count(), all.Count);
        var missing = FilmCoverage.Missing(points, (e, n) => (e, n) is (2600, 1200));
        Assert.Equal(all.Count - 1, missing.Count);
        Assert.All(missing, t => Assert.Contains(t, all));
    }

    [Fact]
    public void A_tile_abroad_is_allowed_and_counted_not_missing()
    {
        // a point by the border: tiles west of 2600 are "abroad" (no Swiss survey), none is built
        var points = new[] { new FilmCoverage.Need(2_600_500, 1_200_500, double.NaN, Eye: true) };
        var missing = FilmCoverage.Missing(points, (_, _) => false, (e, _) => e >= 2600, out var abroad);
        Assert.All(missing, t => Assert.True(t.E >= 2600));
        Assert.All(abroad, t => Assert.True(t.E < 2600));
        Assert.Contains((2599, 1200), abroad);
        Assert.Contains((2600, 1200), missing);
        // without the country map every unbuilt tile is missing, as before
        Assert.Equal(missing.Count + abroad.Count, FilmCoverage.Missing(points, (_, _) => false).Count);
    }
}
