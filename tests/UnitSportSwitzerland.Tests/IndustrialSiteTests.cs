using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// <see cref="BuildingTypes.SiteFor"/> (#497) invents which industry an industrial building is in,
/// because the cadastre only says "industrial". Every peer has to invent the same one from the same
/// bytes, with nothing sent, so what is tested here is that it is a <b>pure function of its inputs</b>
/// and that the mix it produces is actually varied.
/// </summary>
public class IndustrialSiteTests
{
    private static BuildingType Site(string key, BuildingKind kind, float w, float d, float h) =>
        BuildingTypes.SiteFor(key, kind, w, d, h);

    [Fact]
    public void SameBuildingAlwaysGivesTheSameSite()
    {
        for (int i = 0; i < 200; i++)
        {
            string key = $"2593_1120_{i}";
            var first = Site(key, BuildingKind.Industrial, 28f, 16f, 7f);
            for (int again = 0; again < 3; again++)
                Assert.Equal(first, Site(key, BuildingKind.Industrial, 28f, 16f, 7f));
        }
    }

    [Fact]
    public void OnlyIndustrialAndTradeGaragesAreSites()
    {
        foreach (var kind in new[]
        {
            BuildingKind.House, BuildingKind.Apartment, BuildingKind.Commercial, BuildingKind.Agricultural,
            BuildingKind.Sacral, BuildingKind.Civic, BuildingKind.Annex, BuildingKind.Other,
            BuildingKind.UnderConstruction,
        })
            Assert.Equal(BuildingType.None, Site("2593_1120_1", kind, 40f, 25f, 8f));
    }

    [Fact]
    public void ASmallIndustrialSolidIsNoSite()
    {
        // under SiteMinArea there is no hall worth walking into: it stays a plain Workshop
        Assert.Equal(BuildingType.None, Site("2593_1120_2", BuildingKind.Industrial, 7f, 6f, 4f));
    }

    [Fact]
    public void AOneCarGarageIsNoSite()
    {
        // 3.4 x 6.2 m is what the generated world puts beside a house: somebody's garage, not a trade one
        Assert.Equal(BuildingType.None, Site("2593_1120_3", BuildingKind.Garage, 3.4f, 6.2f, 2.8f));
    }

    [Fact]
    public void ASiloIsNoSite()
    {
        // tall and slender: a silo or a tank has no floor to walk, whatever its cadastre class
        Assert.Equal(BuildingType.None, Site("2593_1120_4", BuildingKind.Industrial, 9f, 9f, 22f));
        Assert.Equal(BuildingType.None, Site("2593_1120_5", BuildingKind.Industrial, 12f, 11f, 20f));
    }

    [Fact]
    public void ATallHallIsAlwaysAWorks()
    {
        // that height is a crane, a silo run or a press — never pallet racking
        for (int i = 0; i < 60; i++)
            Assert.Equal(BuildingType.Factory, Site($"2593_1120_{i}", BuildingKind.Industrial, 44f, 26f, 13f));
    }

    [Fact]
    public void BigLowHallsAreWarehousesOrWorks()
    {
        var seen = new HashSet<BuildingType>();
        for (int i = 0; i < 300; i++)
            seen.Add(Site($"2593_1120_{i}", BuildingKind.Industrial, 60f, 30f, 7.5f));
        Assert.Equal(new[] { BuildingType.Factory, BuildingType.Warehouse }, seen.OrderBy(t => t.ToString()).ToArray());
    }

    [Fact]
    public void MediumHallsAreDepotsOrBodyShops()
    {
        var seen = new HashSet<BuildingType>();
        for (int i = 0; i < 300; i++)
            seen.Add(Site($"2593_1120_{i}", BuildingKind.Industrial, 14f, 10f, 6f));
        Assert.Equal(new[] { BuildingType.Depot, BuildingType.Mechanic }, seen.OrderBy(t => t.ToString()).ToArray());
    }

    [Fact]
    public void ABigTradeGarageCanBeADealership()
    {
        var seen = new HashSet<BuildingType>();
        for (int i = 0; i < 300; i++)
            seen.Add(Site($"2593_1120_{i}", BuildingKind.Garage, 22f, 14f, 6f));
        Assert.Contains(BuildingType.Dealership, seen);
        Assert.Contains(BuildingType.Depot, seen);
        Assert.Contains(BuildingType.Mechanic, seen);
        Assert.DoesNotContain(BuildingType.None, seen);
    }

    [Fact]
    public void ADealershipNeedsFrontage()
    {
        // below DealerArea a trade garage is a workshop or a small haulier, never a showroom
        for (int i = 0; i < 300; i++)
            Assert.NotEqual(BuildingType.Dealership,
                Site($"2593_1120_{i}", BuildingKind.Garage, 13f, 11f, 5f));
    }

    [Fact]
    public void NoOneTypeTakesOverTheMediumStock()
    {
        // the point of the hash is variety: neither type may run away with the whole stock
        int depots = 0, shops = 0;
        for (int i = 0; i < 2000; i++)
            if (Site($"2593_1120_{i}", BuildingKind.Industrial, 16f, 11f, 6f) == BuildingType.Depot) depots++;
            else shops++;
        Assert.InRange(depots / 2000.0, 0.4, 0.6);
        Assert.InRange(shops / 2000.0, 0.4, 0.6);
    }

    [Fact]
    public void EveryIndustrialSiteIsDrivenIntoButTheShowroom()
    {
        Assert.True(BuildingTypes.DrivenInto(BuildingType.Warehouse));
        Assert.True(BuildingTypes.DrivenInto(BuildingType.Factory));
        Assert.True(BuildingTypes.DrivenInto(BuildingType.Depot));
        Assert.True(BuildingTypes.DrivenInto(BuildingType.Mechanic));
        Assert.False(BuildingTypes.DrivenInto(BuildingType.Dealership));
        // a church and a bank are not sites at all
        Assert.False(BuildingTypes.IsSite(BuildingType.Church));
        Assert.False(BuildingTypes.IsSite(BuildingType.Bank));
        Assert.False(BuildingTypes.IsSite(BuildingType.None));
    }

    [Fact]
    public void SiteValuesKeepTheirNumbers()
    {
        // stored plans hold BuildingType as a number: renumbering one would re-label every saved
        // interior (InteriorLayout.Type)
        Assert.Equal(0, (int)BuildingType.None);
        Assert.Equal(1, (int)BuildingType.Church);
        Assert.Equal(2, (int)BuildingType.Bank);
        Assert.Equal(3, (int)BuildingType.Warehouse);
        Assert.Equal(4, (int)BuildingType.Factory);
        Assert.Equal(5, (int)BuildingType.Depot);
        Assert.Equal(6, (int)BuildingType.Mechanic);
        Assert.Equal(7, (int)BuildingType.Dealership);
    }
}
