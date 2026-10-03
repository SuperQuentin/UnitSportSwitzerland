using UnitSport.Vehicles;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>What fits in a hold and when a vehicle is in one (#418, src/Vehicles/CargoFit.cs).</summary>
public class CargoFitTests
{
    // holds (across, height, length): the AN-124's and a Transall-sized military freighter's
    private static readonly (float W, float H, float L) An124 = (6.4f, 4.4f, 36f), Freighter = (3.15f, 2.98f, 13.5f);
    // hulls: a Citaro city bus (12 m, 3.07 m high), a hatchback, a motorbike
    private static readonly (float W, float H, float L) Bus = (2.55f, 3.07f, 12.13f), Car = (1.7f, 1.4f, 4.0f), Bike = (0.8f, 1.2f, 2.1f);

    private static bool Fits((float W, float H, float L) v, (float W, float H, float L) bay) => CargoFit.Fits(v.W, v.H, v.L, bay.W, bay.H, bay.L);

    [Fact]
    public void A_bus_fits_the_an124_not_the_freighter()
    {
        Assert.True(Fits(Bus, An124));
        Assert.False(Fits(Bus, Freighter));
    }

    [Fact]
    public void Cars_and_bikes_fit_both()
    {
        foreach (var v in new[] { Car, Bike })
        {
            Assert.True(Fits(v, An124));
            Assert.True(Fits(v, Freighter));
        }
    }

    [Fact]
    public void Fits_needs_clearance_on_every_side()
    {
        Assert.False(CargoFit.Fits(3f, 1f, 4f, 3f, 2f, 5f));
        Assert.True(CargoFit.Fits(3f - CargoFit.Clearance, 1f, 4f, 3f, 2f, 5f));
        Assert.False(CargoFit.Fits(1f, 2f, 4f, 3f, 2f, 5f));
        Assert.False(CargoFit.Fits(1f, 1f, 5f, 3f, 2f, 5f));
    }

    [Fact]
    public void Inside_is_over_the_floor_and_under_the_roof()
    {
        // a 3 x 2 x 10 hold: floor at y -1
        Assert.True(CargoFit.Inside(0f, -1f, 0f, 3f, 2f, 10f, 0f));
        Assert.True(CargoFit.Inside(1.4f, -1f, 4.9f, 3f, 2f, 10f, 0f));
        Assert.False(CargoFit.Inside(0f, -1f, 5.2f, 3f, 2f, 10f, 0f));
        Assert.True(CargoFit.Inside(0f, -1f, 5.2f, 3f, 2f, 10f, 0.3f));   // the grown box keeps one in
        Assert.True(CargoFit.Inside(0f, -1.4f, 0f, 3f, 2f, 10f, 0f));     // on a ramp's top, just under the floor
        Assert.False(CargoFit.Inside(0f, -2f, 0f, 3f, 2f, 10f, 0f));      // on the ground under it
        Assert.False(CargoFit.Inside(0f, 1.2f, 0f, 3f, 2f, 10f, 0f));     // on its roof
    }
}
