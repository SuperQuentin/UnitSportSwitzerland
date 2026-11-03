using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Zibelemaerit;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Zibelemärit (#1373): the festival's dates and site, the stalls, wares and prices, the stall-hop and the confetti fight's pay. Pure.</summary>
public class ZibeleTests
{
    private static ResolvedSite Site() => new("the test field", 2600000, 1200000, 100, false, null, "Zibelemärit at the test field");

    [Fact]
    public void The_festival_is_in_the_catalogue_and_sound()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.ZibelemaeritId);
        Assert.NotNull(def);
        Assert.Empty(def!.Problems());
        Assert.Equal("zibelemaerit", def.Id);
        Assert.Equal(1, def.Length);
        foreach (string g in new[] { ZibeleRules.StallGame, ZibeleRules.ConfettiGame }) Assert.True(def.IsOpen(g, 1));
        Assert.Equal(ZibeleRules.DayMax + ZibeleRules.ConfettiDayCap, def.TokenCap);
    }

    [Theory]
    [InlineData(2026, 11, 23)]
    [InlineData(2027, 11, 22)]
    [InlineData(2028, 11, 27)]
    public void It_is_the_fourth_monday_of_november_for_one_day(int year, int month, int day)
    {
        var def = FestivalCatalog.Get(FestivalCatalog.ZibelemaeritId)!;
        var (from, to) = def.Schedule[0];
        var span = FestivalCalendar.SpanOf(from, to, year);
        Assert.NotNull(span);
        Assert.Equal(new DateOnly(year, month, day), span!.Value.Start);
        Assert.Equal(DayOfWeek.Monday, span.Value.Start.DayOfWeek);
        Assert.Equal(span.Value.Start, span.Value.End);
    }

    [Fact]
    public void Bern_holds_it_at_the_bundesplatz()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.ZibelemaeritId)!;
        Assert.Contains("Bern", def.Sites[0].Name);
    }

    [Fact]
    public void Four_stalls_in_a_row_each_with_its_own_ware()
    {
        var layout = ZibeleRules.Layout(Site());
        Assert.Equal(ZibeleRules.Stalls, layout.Length);
        Assert.Equal(4, layout.Select(s => s.Kind).Distinct().Count());
        for (int i = 1; i < layout.Length; i++) Assert.True(ZibeleRules.Dist(layout[i].E, layout[i].N, layout[i - 1].E, layout[i - 1].N) > 3.4);   // a booth is 3.3 m wide
        // standing in front of a stall finds that stall, standing far off finds none
        var s0 = layout[0];
        Assert.Equal(0, ZibeleRules.NearestStall(Site(), s0.E, s0.N - 2));
        Assert.Equal(-1, ZibeleRules.NearestStall(Site(), s0.E + 100, s0.N + 100));
    }

    [Fact]
    public void Wares_and_prices_are_a_real_markets()
    {
        Assert.Equal(ItemId.Zwiebelzopf, ZibeleRules.ItemOf(ZibeleStallKind.Zopf));
        Assert.Equal(ItemId.Konfettiei, ZibeleRules.ItemOf(ZibeleStallKind.Konfetti));
        Assert.Equal(6, ZibeleRules.CountOf(ZibeleStallKind.Konfetti));
        Assert.Equal(1, ZibeleRules.CountOf(ZibeleStallKind.Chueche));
        foreach (var k in Enum.GetValues<ZibeleStallKind>())
        {
            Assert.InRange(ZibeleRules.PriceOf(k), 1, 10);
            Assert.NotEmpty(ZibeleRules.StallName(k));
            Assert.NotEmpty(ZibeleRules.WareName(k));
        }
    }

    [Fact]
    public void Items_are_in_the_festivals_block_525_to_529()
    {
        foreach (var id in new[] { ItemId.Zwiebelzopf, ItemId.Zibelechueche, ItemId.Mehlsuppe, ItemId.Konfettiei })
            Assert.InRange((int)id, 525, 529);
        Assert.Equal(4, new[] { ItemId.Zwiebelzopf, ItemId.Zibelechueche, ItemId.Mehlsuppe, ItemId.Konfettiei }.Distinct().Count());
    }

    [Fact]
    public void The_stall_hop_stamps_once_and_pays_its_day_cap()
    {
        int mask = 0;
        for (int i = 0; i < ZibeleRules.Stalls; i++)
        {
            Assert.False(ZibeleRules.Stamped(mask, i));
            mask = ZibeleRules.Stamp(mask, i);
            Assert.True(ZibeleRules.Stamped(mask, i));
            Assert.Equal(i + 1, ZibeleRules.Count(mask));
        }
        Assert.Equal(ZibeleRules.AllStamps, mask);
        Assert.Equal(mask, ZibeleRules.Stamp(mask, 2));
        Assert.Equal(mask, ZibeleRules.Stamp(mask, 9));
        Assert.Equal(ZibeleRules.Stalls * ZibeleRules.StampBatzen + ZibeleRules.MarktgangBatzen, ZibeleRules.DayMax);
    }

    [Fact]
    public void A_whole_round_of_wares_costs_less_than_the_day_pays_and_the_cap_is_the_games()
    {
        int round = Enum.GetValues<ZibeleStallKind>().Sum(ZibeleRules.PriceOf);
        Assert.True(round < ZibeleRules.DayMax);
        Assert.Equal(ZibeleRules.DayMax + ZibeleRules.ConfettiDayCap, ZibeleRules.TokenCap);
        // six eggs are one play and a bit: buying eggs to farm throws never pays more than the day cap
        Assert.Equal(0, ZibeleRules.CountOf(ZibeleStallKind.Konfetti) / ZibeleRules.ThrowsPerPlay - 1);
        Assert.True(ZibeleRules.ConfettiDayCap % ZibeleRules.ThrowBatzen == 0);
    }

    [Fact]
    public void The_square_is_where_throws_count()
    {
        var site = Site();
        Assert.True(ZibeleRules.NearSite(site, site.E + 100, site.N));
        Assert.False(ZibeleRules.NearSite(site, site.E + 150, site.N));
        Assert.True(ZibeleRules.NearSite(site, site.E + 150, site.N, ZibeleRules.ServerSlack * 6));
    }
}
