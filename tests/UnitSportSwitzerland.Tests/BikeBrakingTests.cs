using Godot;
using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Addict's hydraulic discs under a 120 kg rider (#1124): the load moving forward, the rear locking, the pitch-over point.</summary>
public class BikeBrakingTests
{
    private const float Dry = BikePhysics.TyreGrip, Gravel = 0.65f, Grass = 0.5f;
    private static readonly (float ToFront, float ToRear, float Height) Owner = BikeFrame.Default.Balance(120f);
    private static readonly float Disc = BikePhysics.LeverStrength(BikeBrakes.Disc);

    private static BikePhysics.BrakeResult Brake(float lever, float grip = Dry, float? strength = null) =>
        BikePhysics.Brake(lever, grip, strength ?? Disc, Owner.ToFront, Owner.ToRear, Owner.Height);

    [Fact]
    public void A_seated_rider_puts_most_weight_on_the_back_wheel()
    {
        float front = Owner.ToRear / (Owner.ToFront + Owner.ToRear);
        Assert.InRange(front, 0.33f, 0.45f);        // road bikes on the hoods weigh 35-45% front
        Assert.InRange(Owner.Height, 0.95f, 1.15f);
    }

    [Fact]
    public void A_full_pull_stops_at_the_pitch_over_point_with_the_rear_sliding()
    {
        var full = Brake(1f);
        Assert.InRange(full.Decel / BikePhysics.Gravity, 0.50f, 0.62f);   // ~0.6 g: no bike stops harder upright
        Assert.True(full.RearLocked);
        Assert.True(full.PitchLimited);
        // 50 km/h to a stop: 16-20 m, before the rider's reaction
        float v = 50f / 3.6f;
        Assert.InRange(v * v / (2f * full.Decel), 16f, 20f);
    }

    [Fact]
    public void A_gentle_pull_slows_without_locking()
    {
        var soft = Brake(0.2f);
        Assert.False(soft.RearLocked);
        Assert.False(soft.PitchLimited);
        Assert.Equal(0.2f * Disc * BikePhysics.Gravity, soft.Decel, 2);
    }

    [Fact]
    public void Gravel_still_stops_at_the_pitch_point_but_grass_runs_out_of_grip()
    {
        // gravel's 0.65 is more than the ~0.57 g a bike can stop at upright: as hard a stop as tarmac,
        // but the rear goes earlier
        Assert.Equal(Brake(1f).Decel, Brake(1f, Gravel).Decel, 2);
        Assert.True(Brake(0.5f, Gravel).RearLocked);
        // grass's 0.5 is less: the tyres give out first
        var grass = Brake(1f, Grass);
        Assert.False(grass.PitchLimited);
        Assert.True(grass.Decel < Brake(1f).Decel - 0.4f);
    }

    [Fact]
    public void Discs_outbrake_rims_short_of_the_limit()
    {
        float rim = BikePhysics.LeverStrength(BikeBrakes.Rim);
        Assert.True(Brake(0.4f).Decel > Brake(0.4f, strength: rim).Decel);
    }

    [Theory]
    [InlineData(Dry, 0f, 40f, 43f)]       // dry tarmac: ~42°
    [InlineData(Dry, 4.9f, 34f, 39f)]     // braking at half a g stands it up
    [InlineData(Gravel, 0f, 31f, 35f)]    // gravel: ~33°
    public void The_friction_circle_sets_the_lean(float grip, float accel, float min, float max) =>
        Assert.InRange(Mathf.RadToDeg(BikePhysics.GripLean(grip, accel)), min, max);
}
