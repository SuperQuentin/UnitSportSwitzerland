using UnitSport.Player;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The speed regulator (src/Player/CruiseControl.cs, linked in) on a point mass with drag, a hill and a load.</summary>
public class CruiseControlTests
{
    /// <summary>A vehicle of <paramref name="mass"/> kg and <paramref name="force"/> N at full throttle, driven <paramref name="seconds"/> s.</summary>
    private static float Drive(CruiseControl cruise, float u, float seconds, float mass, float force, float grade = 0f, float load = 0f)
    {
        const float dt = 1f / 60f;
        for (float t = 0; t < seconds; t += dt)
        {
            var (throttle, brake) = cruise.Apply(0f, 0f, false, u, dt);
            float a = (throttle * force - brake * 0.6f * mass * 9.81f * MathF.Sign(u) - 0.4f * u * u - 200f - load - mass * 9.81f * grade) / mass;
            u += a * dt;
        }
        return u;
    }

    [Fact]
    public void Sets_the_speed_to_a_whole_kmh_and_no_lower_than_the_least()
    {
        var cruise = new CruiseControl();
        cruise.Engage(10.4f / 3.6f);
        Assert.Equal(10f, cruise.SetSpeed * 3.6f, 3);
        cruise.Engage(0f);
        Assert.Equal(CruiseControl.MinSpeed, cruise.SetSpeed, 4);
    }

    [Theory]
    [InlineData(1500f, 6000f, 0f, 0f)]      // a car on the flat
    [InlineData(1500f, 6000f, 0.06f, 0f)]   // a car up a 6 % road
    [InlineData(9000f, 40000f, 0f, 15000f)] // a tractor pulling a plough
    [InlineData(9000f, 40000f, -0.08f, 0f)] // a tractor down an 8 % lane: it brakes
    public void Holds_the_set_speed(float mass, float force, float grade, float load)
    {
        var cruise = new CruiseControl();
        float set = 10f / 3.6f;
        cruise.Engage(set);
        float u = Drive(cruise, set, 40f, mass, force, grade, load);
        Assert.True(cruise.On);
        Assert.InRange(u * 3.6f, 9f, 11f);
    }

    [Fact]
    public void The_brake_or_handbrake_or_reversing_switches_it_off()
    {
        var cruise = new CruiseControl();
        cruise.Engage(5f);
        Assert.Equal((0f, 0.5f), cruise.Apply(0f, 0.5f, false, 5f, 0.1f));
        Assert.False(cruise.On);
        cruise.Engage(5f);
        cruise.Apply(0f, 0f, true, 5f, 0.1f);
        Assert.False(cruise.On);
        cruise.Engage(5f);
        cruise.Apply(0f, 0f, false, -1f, 0.1f);
        Assert.False(cruise.On);
    }

    [Fact]
    public void The_drivers_throttle_wins_when_it_asks_for_more()
    {
        var cruise = new CruiseControl();
        cruise.Engage(10f);
        var (throttle, brake) = cruise.Apply(1f, 0f, false, 12f, 0.1f);
        Assert.Equal(1f, throttle);
        Assert.Equal(0f, brake);
        Assert.True(cruise.On);
    }

    [Fact]
    public void Pressed_again_near_the_set_speed_it_switches_off_well_off_it_sets_the_new_one()
    {
        var cruise = new CruiseControl();
        Assert.True(cruise.Press(20f / 3.6f));
        Assert.False(cruise.Press(21f / 3.6f));
        Assert.True(cruise.Press(20f / 3.6f));
        Assert.True(cruise.Press(30f / 3.6f));
        Assert.Equal(30f, cruise.SetSpeed * 3.6f, 3);
    }
}
