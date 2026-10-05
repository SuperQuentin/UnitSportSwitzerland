using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The per-vehicle mini tutorials (#517, src/Core/VehicleIntros.cs, linked in).</summary>
public class VehicleIntrosTests
{
    [Fact]
    public void Every_kind_has_one_short_intro()
    {
        foreach (VehicleIntroKind kind in Enum.GetValues<VehicleIntroKind>())
        {
            var intro = VehicleIntros.For(kind);
            Assert.InRange(intro.Rows.Length, 3, 4);
            Assert.False(string.IsNullOrWhiteSpace(intro.Title));
        }
        Assert.Equal(Enum.GetValues<VehicleIntroKind>().Length, VehicleIntros.Every.Select(i => i.Kind).Distinct().Count());
    }

    [Fact]
    public void Every_row_can_be_ticked_and_names_its_controls()
    {
        foreach (var row in VehicleIntros.Every.SelectMany(i => i.Rows))
        {
            Assert.True(row.Actions.Length > 0 || row.While != null, row.Keys);
            // never a key typed into the text: keys come from InputHints placeholders
            Assert.Contains("{", row.Keys);
            if (row.Pad != null) Assert.Contains("{", row.Pad);
        }
    }

    [Fact]
    public void A_pad_gets_its_own_words_where_a_stick_or_trigger_replaces_keys()
    {
        var steer = VehicleIntros.For(VehicleIntroKind.Car).Rows[2];
        Assert.Contains("{move_left}", VehicleIntros.Text(steer, pad: false));
        Assert.Equal(steer.Pad, VehicleIntros.Text(steer, pad: true));
        var gas = VehicleIntros.For(VehicleIntroKind.Car).Rows[0];
        Assert.Equal(gas.Keys, VehicleIntros.Text(gas, pad: true));
    }

    [Fact]
    public void The_trailer_is_met_on_foot_and_done_from_a_truck()
    {
        var trailer = VehicleIntros.For(VehicleIntroKind.Trailer);
        Assert.True(trailer.Near);
        Assert.All(trailer.Rows, r => Assert.Equal(VehicleIntroKind.Truck, r.While));
        Assert.Contains(trailer.Rows, r => r.Actions.Contains("couple"));
        Assert.All(VehicleIntros.Every.Where(i => i.Kind != VehicleIntroKind.Trailer), i => Assert.False(i.Near));
    }

    [Fact]
    public void Seen_matches_saved_names_any_case()
    {
        var saved = new[] { "car", "Helicopter", "Hovercraft" };
        Assert.True(VehicleIntros.Seen(saved, VehicleIntroKind.Car));
        Assert.True(VehicleIntros.Seen(saved, VehicleIntroKind.Helicopter));
        Assert.False(VehicleIntros.Seen(saved, VehicleIntroKind.Plane));
    }
}
