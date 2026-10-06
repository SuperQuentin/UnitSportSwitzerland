using UnitSport.Interiors;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Loading bays on an industrial site's front wall (#528, #496 phase 2): the arithmetic that says
/// how many, how wide and where. Pure, so it is pinned here rather than eyeballed in a town — and
/// the placement rule in particular, because its first two versions both looked right and both
/// lost most of the bays.
/// </summary>
public class LoadingBayTests
{
    private const float Eave = 9f;

    [Fact]
    public void A_showroom_has_no_bays()
    {
        // a dealership's front is glazed and its forecourt is walked across, not reversed into
        Assert.Null(DoorBudget.Bays(BuildingType.Dealership, 40f, Eave));
        // and nor has anything that is not a site at all
        Assert.Null(DoorBudget.Bays(BuildingType.None, 40f, Eave));
        Assert.Null(DoorBudget.Bays(BuildingType.Church, 40f, Eave));
        Assert.Null(DoorBudget.Bays(BuildingType.Bank, 40f, Eave));
    }

    [Fact]
    public void A_hauliers_bays_are_the_widest_and_tallest()
    {
        var depot = DoorBudget.Bays(BuildingType.Depot, 60f, Eave)!.Value;
        var works = DoorBudget.Bays(BuildingType.Factory, 60f, Eave)!.Value;
        var shop = DoorBudget.Bays(BuildingType.Mechanic, 60f, Eave)!.Value;
        Assert.True(depot.Width > works.Width && works.Width > shop.Width);
        Assert.True(depot.Height >= works.Height && works.Height > shop.Height);
        // a 4 m box body has to clear the lintel
        Assert.True(depot.Height >= 4.4f, $"a depot's bay is {depot.Height} m");
    }

    [Fact]
    public void A_bay_is_never_taller_than_the_wall_it_is_cut_in()
    {
        for (float eave = 2.5f; eave < 12f; eave += 0.25f)
            if (DoorBudget.Bays(BuildingType.Warehouse, 60f, eave) is { } run)
                Assert.True(run.Height < eave, $"a {run.Height} m bay in a {eave} m wall");
    }

    [Fact]
    public void A_wall_too_low_for_a_vehicle_door_gets_no_bays_rather_than_a_squashed_one()
    {
        Assert.Null(DoorBudget.Bays(BuildingType.Warehouse, 60f, 2.8f));
        Assert.Null(DoorBudget.Bays(BuildingType.Depot, 60f, 3.0f));
        Assert.NotNull(DoorBudget.Bays(BuildingType.Warehouse, 60f, 4.0f));
    }

    [Fact]
    public void A_short_wall_gets_fewer_bays_and_a_tiny_one_none()
    {
        int Count(float run) => DoorBudget.Bays(BuildingType.Warehouse, run, Eave)?.Count ?? 0;
        Assert.Equal(0, Count(3f));
        Assert.True(Count(12f) < Count(30f), "a 12 m wall takes fewer bays than a 30 m one");
        Assert.True(Count(30f) <= Count(90f));
        // and a wall of any length stops at the type's own maximum
        Assert.Equal(DoorBudget.Bays(BuildingType.Warehouse, 400f, Eave)!.Value.Count, Count(400f));
        Assert.True(Count(400f) <= 6);
    }

    [Fact]
    public void The_pier_between_two_bays_is_wall_and_not_a_pedestrian_doors_elbow_room()
    {
        var run = DoorBudget.Bays(BuildingType.Warehouse, 60f, Eave)!.Value;
        Assert.True(run.Pier > 0, "two bays cannot share a jamb");
        // the whole point: MinGap would reject every bay after the first
        Assert.True(run.Pier < DoorBudget.MinGap, $"pier {run.Pier} vs MinGap {DoorBudget.MinGap}");
        // and the pier must be small enough that bays at these centres actually pass TooClose
        Assert.True(run.Width + run.Pier <= run.Spacing + 1e-4f,
            $"{run.Width} + {run.Pier} must fit in {run.Spacing}");
    }

    [Fact]
    public void No_bay_lands_on_the_main_door()
    {
        var run = DoorBudget.Bays(BuildingType.Warehouse, 80f, Eave)!.Value;
        const float mainHalf = 1.4f;
        foreach (float off in DoorBudget.BayOffsets(80f, mainHalf, run))
            Assert.True(Math.Abs(off) >= mainHalf + DoorBudget.BayToDoorGap + run.Width / 2 - 1e-4f,
                $"a bay at {off} overlaps the main door");
    }

    [Fact]
    public void Bays_fill_both_sides_of_the_main_door()
    {
        // taking only the longer side cost a 26 x 18 m depot half the bays its wall had room for
        var run = DoorBudget.Bays(BuildingType.Warehouse, 80f, Eave)!.Value;
        var offsets = DoorBudget.BayOffsets(80f, 1.4f, run);
        Assert.Contains(offsets, o => o < 0);
        Assert.Contains(offsets, o => o > 0);
    }

    [Fact]
    public void Every_bay_stands_on_the_wall_with_room_for_its_jambs()
    {
        var run = DoorBudget.Bays(BuildingType.Depot, 70f, Eave)!.Value;
        foreach (float off in DoorBudget.BayOffsets(70f, 1.4f, run))
        {
            Assert.True(off - run.Width / 2 >= -35f + DoorBudget.EndMargin - 1e-4f, $"bay at {off} runs off the near end");
            Assert.True(off + run.Width / 2 <= 35f - DoorBudget.EndMargin + 1e-4f, $"bay at {off} runs off the far end");
        }
    }

    [Fact]
    public void Bays_on_one_side_are_a_spacing_apart()
    {
        var run = DoorBudget.Bays(BuildingType.Warehouse, 80f, Eave)!.Value;
        var offsets = DoorBudget.BayOffsets(80f, 1.4f, run);
        foreach (var side in new[] { offsets.Where(o => o > 0).OrderBy(o => o).ToArray(),
                                     offsets.Where(o => o < 0).OrderByDescending(o => o).ToArray() })
            for (int i = 1; i < side.Length; i++)
                Assert.Equal(run.Spacing, Math.Abs(side[i] - side[i - 1]), 3);
    }

    [Fact]
    public void A_wall_with_no_room_beside_the_door_gets_no_bays()
    {
        var run = DoorBudget.Bays(BuildingType.Warehouse, 60f, Eave)!.Value;
        // a 9 m run is all main door and elbow room
        Assert.Empty(DoorBudget.BayOffsets(9f, 1.4f, run));
        Assert.Empty(DoorBudget.BayOffsets(0f, 1.4f, run));
    }

    [Fact]
    public void The_offsets_never_outnumber_the_run()
    {
        foreach (var site in new[] { BuildingType.Warehouse, BuildingType.Factory, BuildingType.Depot, BuildingType.Mechanic })
            for (float wall = 8f; wall < 140f; wall += 3.5f)
            {
                if (DoorBudget.Bays(site, wall, Eave) is not { } run) continue;
                var offsets = DoorBudget.BayOffsets(wall, 1.4f, run);
                Assert.True(offsets.Length <= run.Count, $"{site} on a {wall} m wall: {offsets.Length} > {run.Count}");
                Assert.Equal(offsets.Length, offsets.Distinct().Count());
            }
    }
}
