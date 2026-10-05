using UnitSport.Farming;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The farm machines' tanks, codes, seed and draft (#494, src/Farming/MachineLoad.cs).</summary>
public class MachineLoadTests
{
    [Fact]
    public void Fractions_add_up_to_whole_items()
    {
        var t = default(Tank);
        for (int i = 0; i < 10; i++) t = MachineLoad.Add(t, CropKind.Wheat, 0.25f, 140, out _);
        Assert.Equal(CropKind.Wheat, t.Crop);
        Assert.Equal(2, t.Items);
        Assert.Equal(0.5f, t.Partial, 3);
    }

    [Fact]
    public void One_crop_at_a_time_and_capacity_holds()
    {
        var t = MachineLoad.Add(default, CropKind.Wheat, 3f, 140, out _);
        var same = MachineLoad.Add(t, CropKind.Barley, 2f, 140, out var why);
        Assert.Equal(TankRefusal.OtherCrop, why);
        Assert.Equal(t, same);
        var full = MachineLoad.Add(t, CropKind.Wheat, 500f, 140, out why);
        Assert.Equal(TankRefusal.Full, why);
        Assert.Equal(140, full.Items);
        // emptied, it takes another crop
        var emptied = MachineLoad.Take(full, 1000, out int taken);
        Assert.Equal(140, taken);
        Assert.True(emptied.Empty);
        MachineLoad.Add(emptied, CropKind.Barley, 1f, 140, out why);
        Assert.Equal(TankRefusal.None, why);
    }

    [Fact]
    public void Unloading_moves_what_fits_of_the_same_crop()
    {
        var combine = new Tank(CropKind.Wheat, 140);
        var (from, to, moved) = MachineLoad.Transfer(combine, new Tank(CropKind.Wheat, 100), 200);
        Assert.Equal(100, moved);
        Assert.Equal(40, from.Items);
        Assert.Equal(200, to.Items);
        Assert.Equal(0, MachineLoad.Transfer(combine, new Tank(CropKind.Maize, 1), 200).Moved);
        Assert.Equal(140, MachineLoad.Transfer(combine, default, 200).Moved);
    }

    [Fact]
    public void Codes_and_flags_round_trip()
    {
        var t = new Tank(CropKind.Potato, 187);
        int code = 7 | 93 << 8 | MachineLoad.FarmBits(t);
        Assert.Equal(7, code & 0xFF);
        Assert.Equal(93, (code >> 8) & 0x7F);
        Assert.Equal(t, MachineLoad.TankOf(code));
        Assert.True(code > 0);
        int flags = 1 | 4 | 5 << 16 | MachineLoad.TankFlags(new Tank(CropKind.Wheat, 140));
        Assert.Equal(new Tank(CropKind.Wheat, 140), MachineLoad.TankFromFlags(flags));
        Assert.Equal(5, (flags >> 16) & 7);
        Assert.True(flags < 1 << 24);   // pose W is a float: exact only below 2^24
        Assert.Equal(default, MachineLoad.TankOf(7 | 50 << 8));
    }

    [Fact]
    public void Seed_is_taken_by_the_whole_item()
    {
        float owed = 0f;
        int taken = 0;
        for (int i = 0; i < 25; i++) taken += MachineLoad.SeedDue(ref owed, 0.1f);
        Assert.Equal(2, taken);
        Assert.Equal(0.5f, owed, 3);
    }

    [Fact]
    public void A_plough_pulls_far_harder_than_a_drill()
    {
        float plough = MachineLoad.PloughDraft(7f, 1.8f);
        Assert.InRange(plough, 25000f, 32000f);
        Assert.True(MachineLoad.PloughDraft(12f, 1.8f) > plough * 1.3f);
        float drill = MachineLoad.DrillDraft(3f);
        Assert.InRange(drill, 8000f, 11000f);
        Assert.True(plough > 2.5f * drill);
        Assert.InRange(MachineLoad.MowerDraft(12f, 3f), 3000f, 6000f);
    }

    [Fact]
    public void The_combine_tank_keeps_to_its_flag_bits()
    {
        var t = new Tank(CropKind.Barley, 140);
        int flags = MachineLoad.TankFlags(t);
        Assert.Equal(flags, flags & MachineLoad.FlagMask);
        // the bus bits under it (brake, lamps, reverse, kneel, doors) and the throttle eighths are not touched
        int other = 1 | 2 | 4 | 8 | 15 << 4 | 7 << 16;
        Assert.Equal(0, other & MachineLoad.FlagMask);
        Assert.Equal(t, MachineLoad.TankFromFlags(flags | other));
        // a float carries the whole pose word exactly (Truck.WritePose)
        Assert.Equal(flags | other, (int)(float)(flags | other));
    }
}
