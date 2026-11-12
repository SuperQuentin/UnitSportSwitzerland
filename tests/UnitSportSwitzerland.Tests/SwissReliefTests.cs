using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The game's 500 m Swiss relief as the pyramid builder reads it (#1746, <see cref="SwissRelief"/>): the flat world's fill
/// stands on it, and the earth world's pyramid takes it past the leaves instead of ETOPO's 1.8 km cells.
/// </summary>
public class SwissReliefTests
{
    private static SwissRelief Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return SwissRelief.Load(Path.Combine(dir!.FullName, "src", "Terrain", "swiss_relief.gz"));
    }

    [Fact]
    public void Riddes_valley_and_the_ridge_above_La_Tzoumaz_stand_at_their_heights()
    {
        var relief = Load();
        Assert.Equal(500, relief.Spacing);
        // the Rhone plain at Riddes (~470 m) and the ridge east of La Tzoumaz (Mont Gond side, well over 2,000 m)
        Assert.InRange(relief.Lv95(2583250, 1113000), 440, 520);
        Assert.InRange(relief.Lv95(2588000, 1106500), 2_000, 3_000);
    }

    [Fact]
    public void Heights_come_back_ellipsoidal_at_the_points_place()
    {
        var relief = Load();
        // a point on the plain at Riddes, as latitude and longitude, and its LV95 place back
        var (lat, lon) = SwissGeodesy.ToLatLon(2583250, 1113000, 0);
        Assert.True(relief.TryAt(lat, lon, out double h, out double weight));
        Assert.Equal(1, weight);
        double undulation = SwissSeaLevel.Instance.Undulation(lat, lon);
        Assert.InRange(undulation, 45, 56);   // the geoid over Valais against WGS84's ellipsoid
        Assert.Equal(relief.Lv95(2583250, 1113000) + undulation, h, 0.5);
    }

    [Fact]
    public void Off_the_map_it_says_nothing_and_near_its_edge_it_hands_over()
    {
        var relief = Load();
        Assert.False(relief.TryAt(45.0, -73.0, out _, out _));   // Montreal
        Assert.False(relief.TryAt(52.5, 13.4, out _, out _));    // Berlin
        // 1 km inside the map's west edge: a fifth of the way into the fade, a little weight
        var (lat, lon) = SwissGeodesy.ToLatLon(relief.MinE + 1_000, (relief.MinN + relief.MaxN) / 2, 0);
        Assert.True(relief.TryAt(lat, lon, out _, out double weight));
        Assert.InRange(weight, 0.01, 0.2);
    }
}
