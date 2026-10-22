using Godot;
using UnitSport.Player;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The Whipple model against the published benchmark (Meijaard et al. 2007, eqs. A.1-A.4 values),
/// and the rider balancing the owner's Addict on it (#1124).
/// </summary>
public class BikeDynamicsTests
{
    private readonly ITestOutputHelper _out;
    public BikeDynamicsTests(ITestOutputHelper output) => _out = output;

    private static readonly WhippleModel Bench = new(WhippleParameters.Benchmark);
    private static readonly WhippleModel Addict = new(WhippleParameters.For(BikeFrame.Default, 120f));

    [Fact]
    public void The_matrices_are_the_papers()
    {
        // M, C1, K0, K2 as the paper prints them for its benchmark bicycle
        Assert.Equal(80.81722f, Bench.Mpp, 2);
        Assert.Equal(2.31941332f, Bench.Mpd, 3);
        Assert.Equal(0.29784188f, Bench.Mdd, 4);
        Assert.Equal(33.86641391f, Bench.C1pd, 3);
        Assert.Equal(-0.85035641f, Bench.C1dp, 4);
        Assert.Equal(1.68540397f, Bench.C1dd, 4);
        Assert.Equal(-80.95f, Bench.K0pp, 2);
        Assert.Equal(-2.59951685f, Bench.K0pd, 3);
        Assert.Equal(-0.80329488f, Bench.K0dd, 4);
        Assert.Equal(76.59734590f, Bench.K2pd, 2);
        Assert.Equal(2.65431524f, Bench.K2dd, 3);
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(6f)]
    [InlineData(10f)]
    [InlineData(16f)]
    [InlineData(22f)]
    public void The_rider_leans_where_asked_and_holds_it(float speed)
    {
        var rider = new RiderBalance(Addict);
        float target = Mathf.DegToRad(25f), max = Mathf.DegToRad(42f);
        float yaw = 0f;
        for (int i = 0; i < 180; i++) yaw = rider.Step(speed, target, max, 1f / 60f);
        _out.WriteLine($"{speed} m/s: roll {Mathf.RadToDeg(rider.Roll):F1}°  steer {Mathf.RadToDeg(rider.Steer):F2}°  yaw {yaw:F3} (g·tanφ/v {WhippleModel.Gravity * Mathf.Tan(rider.Roll) / speed:F3})");
        Assert.InRange(Mathf.RadToDeg(rider.Roll), 23f, 27f);
        // a steady turn's yaw rate is what the lean balances: tan φ = v·ω/g (within the model's linearization)
        Assert.InRange(yaw, 0.85f * WhippleModel.Gravity * Mathf.Tan(rider.Roll) / speed, 1.15f * WhippleModel.Gravity * Mathf.Tan(rider.Roll) / speed);
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(12f)]
    public void To_lean_right_the_rider_first_steers_left(float speed)
    {
        var rider = new RiderBalance(Addict);
        float first = 0f, minRoll = 0f;
        for (int i = 0; i < 12; i++)
        {
            rider.Step(speed, Mathf.DegToRad(20f), Mathf.DegToRad(42f), 1f / 60f);
            if (i == 3) first = rider.Steer;
            minRoll = Mathf.Min(minRoll, rider.Roll);
        }
        Assert.True(first < 0f, $"steer {first} should be left first");
        Assert.True(rider.Roll > 0f, "and the bike falls right");
        Assert.True(minRoll > -0.05f, "without first leaning the wrong way much");
    }

    [Fact]
    public void Straight_and_level_stays_so()
    {
        foreach (float speed in new[] { 2f, 4f, 8f, 15f, 25f })
        {
            var rider = new RiderBalance(Addict);
            rider.Reset(Mathf.DegToRad(3f));   // a nudge
            for (int i = 0; i < 300; i++) rider.Step(speed, 0f, Mathf.DegToRad(42f), 1f / 60f);
            Assert.InRange(Mathf.RadToDeg(rider.Roll), -0.5f, 0.5f);
            Assert.InRange(Mathf.RadToDeg(rider.Steer), -0.5f, 0.5f);
        }
    }

    [Fact]
    public void Steering_pushes_harder_on_the_lean_the_faster_it_goes()
    {
        // what makes a bike steady at speed: a tiny steer angle balances a large lean
        Assert.True(Addict.SteadySteer(15f, 0.3f) < Addict.SteadySteer(5f, 0.3f) / 5f);
    }
}
