using UnitSport.Interiors;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Public toilets by the space they have (#1285): which set a strip of a building fits and what it holds.
/// The layouts themselves are checked in the generators' probes.
/// </summary>
public class ToiletSizingTests
{
    [Fact]
    public void A_long_deep_strip_for_many_people_is_a_gendered_set_with_an_accessible_WC()
    {
        var p = ToiletSizing.Choose(12f, 3.4f, 150);
        Assert.Equal(ToiletSet.Gendered, p.Set);
        Assert.True(p.Accessible);
        Assert.True(p.WomenStalls > p.MenStalls, $"{p.WomenStalls} women's stalls, {p.MenStalls} men's");
        Assert.True(p.Urinals >= 1);
        Assert.True(p.Length <= 12f + 1e-3f, $"{p.Length} m");
        // every stall at least a metre wide in the row the depth takes
        Assert.True(ToiletSizing.StallRow(p.WomenStalls, 3.4f).Width >= ToiletSizing.StallMinW - 1e-3f);
    }

    [Fact]
    public void Without_room_for_it_or_people_enough_there_is_no_accessible_WC()
    {
        Assert.False(ToiletSizing.Choose(9.3f, 3.4f, 150).Accessible);
        Assert.False(ToiletSizing.Choose(14f, 3.4f, 40).Accessible);
        Assert.Equal(ToiletSet.Gendered, ToiletSizing.Choose(14f, 3.4f, 40).Set);
    }

    [Fact]
    public void Less_space_is_a_shared_anteroom_and_really_none_a_single_room()
    {
        Assert.Equal(ToiletSet.Shared, ToiletSizing.Choose(6f, 3.0f, 150).Set);
        // long but too shallow for the gendered rooms' turn
        Assert.Equal(ToiletSet.Single, ToiletSizing.Choose(12f, 2.75f, 150).Set);
        Assert.Equal(ToiletSet.Single, ToiletSizing.Choose(6f, 2.6f, 150).Set);
        Assert.Equal(ToiletSet.Single, ToiletSizing.Choose(1.5f, 2.0f, 150).Set);
        Assert.Equal(ToiletSet.None, ToiletSizing.Choose(1.2f, 3f, 150).Set);
        Assert.Equal(ToiletSet.None, ToiletSizing.Choose(4f, 1.5f, 150).Set);
    }

    [Fact]
    public void A_handful_of_people_get_one_room_whatever_the_space()
    {
        Assert.Equal(ToiletSet.Single, ToiletSizing.Choose(14f, 4f, 8).Set);
        Assert.Equal(ToiletSet.Shared, ToiletSizing.Choose(14f, 4f, 20).Set);
    }

    [Fact]
    public void The_length_a_set_takes_is_what_its_rooms_add_up_to()
    {
        var p = ToiletSizing.Choose(30f, 3.4f, 400);
        float want = 2 * ToiletSizing.AnteW + ToiletSizing.WomenWashW + ToiletSizing.MenWashW(p.Urinals) + ToiletSizing.AccessW;
        Assert.Equal(want, p.Length, 3);
        Assert.Equal(8, p.Urinals);
        // the stalls are capped by the row the depth holds: three of a metre or more in 3.4 m
        Assert.Equal(3, p.WomenStalls);
    }

    [Fact]
    public void A_short_row_of_stalls_leaves_a_cleaners_cupboard_or_widens_its_stalls()
    {
        // two men's stalls in 3.4 m: 1.4 m each and a 0.6 m sliver, too little for a cupboard: wider stalls
        Assert.Equal((1.7f, 0f), ToiletSizing.StallRow(2, 3.4f));
        // one in 3.4 m: 1.4 m and a 2 m cupboard
        var (w, left) = ToiletSizing.StallRow(1, 3.4f);
        Assert.Equal(1.4f, w, 3);
        Assert.Equal(2.0f, left, 3);
        Assert.Equal((1.0f, 0f), ToiletSizing.StallRow(3, 3.0f));
    }

    [Fact]
    public void An_anteroom_has_a_basin_per_two_fixtures_as_its_wall_allows()
    {
        Assert.Equal(1, ToiletSizing.Basins(1, 3f));
        Assert.Equal(3, ToiletSizing.Basins(5, 3f));
        Assert.Equal(2, ToiletSizing.Basins(8, 1.4f));
    }
}
