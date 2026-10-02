using Godot;
using UnitSport.Core;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The shared maths helpers (src/Core/MathX.cs, linked in): each must equal the inline formula it replaced, bit for bit.</summary>
public class MathXTests
{
    [Fact]
    public void Flat_drops_y()
    {
        Assert.Equal(new Vector3(3, 0, -4), MathX.Flat(new Vector3(3, 7, -4)));
        Assert.Equal(5f, MathX.FlatLength(new Vector3(3, 100, -4)));
        Assert.Equal(5f, MathX.FlatDistance(new Vector3(4, 1, -2), new Vector3(1, -9, 2)));
    }

    [Theory]
    [InlineData(6f, 1f / 60f)]
    [InlineData(0.05f, 0.1f)]
    [InlineData(14f, 0.016666668f)]
    public void Damp_is_the_inline_formula(float rate, float dt)
    {
        Assert.Equal(1f - Mathf.Exp(-rate * dt), MathX.Damp(rate, dt));
        Assert.Equal(1f - Mathf.Exp(-dt * rate), MathX.Damp(rate, dt));   // a*b == b*a in IEEE: callers wrote both
        Assert.InRange(MathX.Damp(rate, dt), 0f, 1f);
    }

    [Fact]
    public void Damp_is_frame_rate_independent()
    {
        float once = MathX.Damp(5f, 0.1f);
        float twice = 1f - (1f - MathX.Damp(5f, 0.05f)) * (1f - MathX.Damp(5f, 0.05f));
        Assert.Equal(once, twice, 5);
        Assert.Equal(0f, MathX.Damp(5f, 0f));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(4f, 4f - Mathf.Tau)]
    [InlineData(-4f, -4f + Mathf.Tau)]
    [InlineData(10f, 10f - 2 * Mathf.Tau)]
    public void WrapAngle_into_pm_pi(float a, float want)
    {
        Assert.Equal(want, MathX.WrapAngle(a), 5);
        Assert.Equal(Mathf.Wrap(a, -Mathf.Pi, Mathf.Pi), MathX.WrapAngle(a));
        Assert.InRange(MathX.WrapAngle(a), -Mathf.Pi, Mathf.Pi);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(0.77f)]
    [InlineData(1f)]
    public void SmoothStep_equals_the_hand_written_cubic_inside_0_1(float t) =>
        Assert.Equal(t * t * (3f - 2f * t), Mathf.SmoothStep(0f, 1f, t));
}
