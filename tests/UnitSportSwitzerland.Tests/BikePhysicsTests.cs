using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The road bike's power equation against published numbers (#1124): the standard atmosphere, the
/// drafting literature, and steady speeds the usual cycling power calculators (Martin 1998's model)
/// give for the same inputs.
/// </summary>
public class BikePhysicsTests
{
    private const float Tarmac = 0.0045f, Gravel = 0.012f;
    private static readonly float RefMass = BikePhysics.ReferenceRiderMass + BicycleCatalog.AddictRc15.Mass + 1.5f;
    private static readonly float OwnerMass = 120f + BicycleCatalog.AddictRc15.Mass + 1.5f;
    private static float Kmh(float ms) => ms * 3.6f;

    [Theory]
    [InlineData(0f, 1.225f)]       // ISA sea level
    [InlineData(500f, 1.167f)]     // a Swiss valley floor
    [InlineData(2164f, 0.990f)]    // the Grimsel pass
    public void Air_density_follows_the_standard_atmosphere(float altitude, float expected) =>
        Assert.Equal(expected, BikePhysics.AirDensity(altitude), 2);

    [Fact]
    public void Drag_area_scales_with_the_rider_and_the_position()
    {
        Assert.Equal(0.32f, BikePhysics.DragArea(72.5f, RidingPosition.Hoods), 3);
        Assert.InRange(BikePhysics.DragArea(120f, RidingPosition.Hoods), 0.39f, 0.40f);
        Assert.True(BikePhysics.DragArea(120f, RidingPosition.Tuck) < BikePhysics.DragArea(120f, RidingPosition.Drops));
    }

    [Theory]
    [InlineData(0.5f, 0f, 0.38f, 0.42f)]   // half a wheel back: ~40%
    [InlineData(1.0f, 0f, 0.33f, 0.37f)]
    [InlineData(3.0f, 0f, 0.19f, 0.23f)]
    [InlineData(10f, 0f, 0.02f, 0.05f)]
    [InlineData(20f, 0f, 0f, 0f)]          // gone
    [InlineData(1.0f, 0.7f, 0f, 0f)]       // out of the wake beside the wheel
    public void A_wheel_ahead_shelters_by_the_gap(float gap, float lateral, float min, float max) =>
        Assert.InRange(BikePhysics.WheelDraft(gap, lateral), min, max);

    [Fact]
    public void The_reference_rider_keeps_the_old_flat_speed()
    {
        // the model the game shipped with gave 32.7 km/h at 180 W; the new terms move it by tenths
        float v = BikePhysics.SteadySpeed(180f, RefMass, 0.32f, Tarmac, BikePhysics.AirDensity(500f));
        Assert.InRange(Kmh(v), 32.4f, 33.4f);
    }

    [Fact]
    public void The_owner_at_120_kg()
    {
        float drag = BikePhysics.DragArea(120f, RidingPosition.Hoods);
        float valley = BikePhysics.AirDensity(500f);
        Assert.InRange(Kmh(BikePhysics.SteadySpeed(180f, OwnerMass, drag, Tarmac, valley)), 29.1f, 30.1f);
        Assert.InRange(Kmh(BikePhysics.SteadySpeed(250f, OwnerMass, drag, Tarmac, valley, grade: 0.08f)), 7.8f, 8.4f);
        // the thin air on the pass is worth ~1.5 km/h; a gravel lane costs five
        Assert.InRange(Kmh(BikePhysics.SteadySpeed(180f, OwnerMass, drag, Tarmac, BikePhysics.AirDensity(2164f))), 30.6f, 31.6f);
        Assert.InRange(Kmh(BikePhysics.SteadySpeed(180f, OwnerMass, drag, Gravel, valley)), 23.9f, 24.9f);
        // a metre behind a wheel: four km/h for nothing
        float draft = BikePhysics.WheelDraft(1f, 0f);
        Assert.InRange(Kmh(BikePhysics.SteadySpeed(180f, OwnerMass, drag, Tarmac, valley, draft: draft)), 33.1f, 34.1f);
    }

    [Fact]
    public void A_tucked_descent_settles_where_the_hill_meets_the_air()
    {
        float tuck = BikePhysics.DragArea(72.5f, RidingPosition.Tuck);
        float v = BikePhysics.SteadySpeed(0f, RefMass, tuck, Tarmac, BikePhysics.AirDensity(500f), grade: -0.08f);
        Assert.InRange(Kmh(v), 73f, 77f);
    }
}
