using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The record of which facade doors have no doorway inside (#1766): what a tile build leaves out, kept per tile on
/// disk and judged against the building's print, so a changed building is planned again.
/// </summary>
public class DoorLocksTests
{
    private static readonly BuildingKey House = new(2501, 1117, 78);

    [Fact]
    public void AnUnknownBuildingAndAMainDoorAreNeverLocked()
    {
        var locks = new DoorLocks(null);
        Assert.False(locks.IsLocked(new DoorKey(House, 2), 7));
        locks.Record(House, 7, [0, 1, 2]);
        // slot 0 always has its doorway: never recorded, never left out
        Assert.False(locks.IsLocked(new DoorKey(House, 0), 7));
        Assert.True(locks.IsLocked(new DoorKey(House, 1), 7));
        Assert.True(locks.IsLocked(new DoorKey(House, 2), 7));
        Assert.False(locks.IsLocked(new DoorKey(House, 3), 7));
    }

    [Fact]
    public void AChangedBuildingIsUnknownAgain()
    {
        var locks = new DoorLocks(null);
        locks.Record(House, 7, [2]);
        Assert.True(locks.Known(House, 7));
        Assert.False(locks.Known(House, 8));
        Assert.False(locks.IsLocked(new DoorKey(House, 2), 8));
        // a check asks what is on record, whatever the print
        Assert.True(locks.Recorded(new DoorKey(House, 2)));
        Assert.False(locks.Recorded(new DoorKey(House, 1)));
    }

    [Fact]
    public void ChangedIsRaisedOnlyWhenTheLockedDoorsChange()
    {
        var locks = new DoorLocks(null);
        var raised = new List<TileId>();
        locks.Changed += raised.Add;
        locks.Record(House, 7, []);
        Assert.Empty(raised);
        Assert.False(locks.AnyLocked(House.Tile));
        locks.Record(House, 7, [2, 1, 2]);
        Assert.Equal([House.Tile], raised);
        locks.Record(House, 7, [1, 2]);
        Assert.Single(raised);
        Assert.True(locks.AnyLocked(House.Tile));
    }

    [Fact]
    public void RecordsSurviveOnDiskPerTile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "doorlocks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new DoorLocks(dir);
            first.Record(House, 4_000_000_000u, [3, 1]);
            first.Record(House with { Index = 5 }, 12, []);
            Assert.True(File.Exists(Path.Combine(dir, "2501_1117.txt")));
            var again = new DoorLocks(dir);
            Assert.True(again.IsLocked(new DoorKey(House, 1), 4_000_000_000u));
            Assert.True(again.IsLocked(new DoorKey(House, 3), 4_000_000_000u));
            Assert.False(again.IsLocked(new DoorKey(House, 2), 4_000_000_000u));
            Assert.True(again.Known(House with { Index = 5 }, 12));
            Assert.False(again.AnyLocked(new TileId(2500, 1117)));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ALineItCannotReadIsPlannedAgain()
    {
        string dir = Path.Combine(Path.GetTempPath(), "doorlocks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "2501_1117.txt"), "78 7 2\nnonsense\n79 x 1\n80 9 1 y\n");
            var locks = new DoorLocks(dir);
            Assert.True(locks.IsLocked(new DoorKey(House, 2), 7));
            Assert.False(locks.Known(House with { Index = 79 }, 0));
            Assert.False(locks.Known(House with { Index = 80 }, 9));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ThePrintIsStableAndCultureFree()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-CH");
            uint fr = DoorLocks.Print(120, 372.5f, 390.25f, "", "");
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(fr, DoorLocks.Print(120, 372.5f, 390.25f, "", ""));
            Assert.NotEqual(fr, DoorLocks.Print(121, 372.5f, 390.25f, "", ""));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
}
