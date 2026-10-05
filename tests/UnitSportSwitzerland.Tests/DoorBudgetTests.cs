using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// How many front doors a building gets and where they go along its walls (#498): the rules a
/// 100 m block needs so it does not stand there with one door.
/// </summary>
public class DoorBudgetTests
{
    [Fact]
    public void AHouseKeepsItsOneFrontDoor()
    {
        Assert.Equal(1, DoorBudget.Total(BuildingKind.House, 10, 8));
        Assert.Equal(1, DoorBudget.Total(BuildingKind.House, 16, 11));
        Assert.Equal(1, DoorBudget.Total(BuildingKind.Annex, 5, 4));
        // and a shed with no size at all is still one door, never none
        Assert.Equal(1, DoorBudget.Total(BuildingKind.Other, 0, 0));
    }

    [Fact]
    public void ABarnAndAGarageAlwaysGetAPedestrianDoorBesideTheirVehicleOne()
    {
        Assert.Equal(2, DoorBudget.Total(BuildingKind.Agricultural, 14, 9));
        Assert.Equal(2, DoorBudget.Total(BuildingKind.Garage, 6, 6));
    }

    [Fact]
    public void ALongBlockGetsSeveralDoors()
    {
        // the complaint this exists for: 100 m of facade behind one door
        Assert.True(DoorBudget.Total(BuildingKind.Commercial, 100, 20) >= 5);
        // and a block of flats one entrance per stairwell's worth of frontage
        Assert.Equal(3, DoorBudget.Total(BuildingKind.Apartment, 30, 15));
    }

    [Fact]
    public void NoBuildingIsAllDoors()
    {
        Assert.Equal(DoorBudget.MaxPerBuilding, DoorBudget.Total(BuildingKind.Industrial, 400, 300));
    }

    [Fact]
    public void OneDoorOnAShortRunAndItIsWhereTheMainDoorAlreadyStood()
    {
        var offsets = DoorBudget.AlongRun(8f, 1f, DoorBudget.MaxPerWall);
        Assert.Equal(new[] { 0f }, offsets);
    }

    [Fact]
    public void ALongRunGetsADoorEveryTwentyOddMetres()
    {
        var offsets = DoorBudget.AlongRun(100f, 1f, DoorBudget.MaxPerWall);
        Assert.Equal(5, offsets.Length);
        Assert.Equal(0f, offsets[0]);                      // the main door keeps the middle
        Assert.Equal(new[] { -44f, -22f, 0f, 22f, 44f }, offsets.OrderBy(x => x));
    }

    [Fact]
    public void DoorsStayOnTheWallAndOffItsEnds()
    {
        foreach (float run in new[] { 3f, 9f, 23f, 45f, 46f, 70f, 140f, 300f })
            foreach (float width in new[] { 1.0f, 1.8f, 4.0f })
            {
                var offsets = DoorBudget.AlongRun(run, width, DoorBudget.MaxPerWall);
                Assert.NotEmpty(offsets);
                Assert.True(offsets.Length <= DoorBudget.MaxPerWall);
                foreach (float o in offsets)
                    Assert.True(Math.Abs(o) + width / 2 + DoorBudget.EndMargin <= run / 2 + 1e-4f || o == 0f,
                        $"run {run} width {width}: a door at {o} hangs off the end");
                // and never two doors in the same piece of wall
                foreach (float a in offsets)
                    foreach (float b in offsets)
                        Assert.True(a == b || Math.Abs(a - b) >= DoorBudget.MinGap + width);
            }
    }

    [Fact]
    public void TheMiddleComesFirstSoABudgetDropsTheOutermost()
    {
        var all = DoorBudget.AlongRun(100f, 1f, DoorBudget.MaxPerWall);
        for (int limit = 1; limit <= all.Length; limit++)
            Assert.Equal(all.Take(limit), DoorBudget.AlongRun(100f, 1f, limit));
    }
}

/// <summary>
/// Door names (#498). A building's main door must keep the name it had when a building had one
/// door, because <c>LootService</c> files, stored plans and the door RPCs all carry that text.
/// </summary>
public class DoorKeyTests
{
    [Fact]
    public void TheMainDoorIsNamedExactlyAsItsBuilding()
    {
        var building = new BuildingKey(2583, 1113, 42);
        Assert.Equal(building.ToString(), new DoorKey(building).ToString());
        Assert.Equal("2583_1113_42", new DoorKey(2583, 1113, 42).ToString());
    }

    [Fact]
    public void AnExtraDoorIsItsBuildingPlusItsSlot()
    {
        Assert.Equal("2583_1113_42_3", new DoorKey(2583, 1113, 42, 3).ToString());
        Assert.Equal(new BuildingKey(2583, 1113, 42), new DoorKey(2583, 1113, 42, 3).Building);
    }

    [Theory]
    [InlineData("2583_1113_42", 2583, 1113, 42, 0)]
    [InlineData("2583_1113_42_1", 2583, 1113, 42, 1)]
    [InlineData("-5_0_0_7", -5, 0, 0, 7)]
    public void EveryNameRoundTrips(string text, int e, int n, int index, int slot)
    {
        Assert.True(DoorKey.TryParse(text, out var key));
        Assert.Equal(new DoorKey(e, n, index, slot), key);
        Assert.Equal(text, key.ToString());
        // and a building key still parses out of a main door's name
        if (slot == 0) Assert.True(BuildingKey.TryParse(text, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2583_1113")]
    [InlineData("2583_1113_42_1_9")]
    [InlineData("2583_1113_x")]
    [InlineData("2583_1113_42_x")]
    public void NonsenseIsRefused(string text)
    {
        Assert.False(DoorKey.TryParse(text, out _));
    }
}
