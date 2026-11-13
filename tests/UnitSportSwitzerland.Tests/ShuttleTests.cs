using Godot;
using UnitSport.Player;
using Xunit;
using Xunit.Abstractions;
using static UnitSport.Player.AirlinerFlight;

namespace UnitSportSwitzerland.Tests;

/// <summary>The space shuttle's flight (#1492, src/Player/ShuttleFlight.cs and its AirlinerFlight parts, linked in).</summary>
public class ShuttleTests
{
    private const float Dt = 1f / 60f;
    private static readonly AirlinerSpec Spec = AirlinerCatalog.Shuttle;
    private readonly ITestOutputHelper _out;

    public ShuttleTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TakesOffOnItsRocketsAndClimbsSteep()
    {
        var s = Parked(Spec, 0f);
        var pos = Vector3.Zero;
        float roll = float.NaN, t = 0;
        for (; t < 120 && pos.Y < 10_000; t += Dt)
        {
            bool floor = pos.Y <= 0.001f;
            float pitch = PitchOf(s.Attitude);
            float stick = s.Ias > Spec.StallSpeed(s.Mass, 0) * 1.15f ? Mathf.Clamp((0.87f - pitch) * 3f, -1f, 1f) : 0f;
            Step(Spec, ref s, new Controls(new Vector2(0, stick), LeverUp: 1f), new Env(floor, pos.Y), Dt);
            pos += s.Velocity * Dt;
            if (pos.Y < 0f) { pos.Y = 0f; if (s.Velocity.Y < 0f) s.Velocity = s.Velocity with { Y = 0f }; }
            if (float.IsNaN(roll) && pos.Y > 1f) roll = -pos.Z;
        }
        _out.WriteLine($"lift-off after {roll:F0} m, 10 km in {t:F0} s, pitch {Mathf.RadToDeg(PitchOf(s.Attitude)):F0}°, {s.Velocity.Length():F0} m/s");
        // 2.1 g on the runway: off in well under a kilometre, then up past the airliner's 30° limit
        Assert.InRange(roll, 200f, 1200f);
        Assert.True(pos.Y >= 10_000, "10 km within two minutes");
        Assert.True(PitchOf(s.Attitude) > 0.7f, "climbing steeper than an airliner may");
    }

    [Fact]
    public void ARocketDoesNotLapseWithTheAir()
    {
        var low = Parked(Spec, 0f) with { Lever = 1f, Spool = 1f, OnGround = false, Velocity = new Vector3(0, 0, -300) };
        var high = low;
        Step(Spec, ref low, new Controls(LeverUp: 1f), new Env(false, 1_000f), Dt);
        Step(Spec, ref high, new Controls(LeverUp: 1f), new Env(false, 40_000f), Dt);
        Assert.Equal(low.Thrust, high.Thrust, 1f);
        Assert.Equal(Spec.Engines * Spec.StaticThrust, high.Thrust, 1f);
    }

    [Fact]
    public void TheDriveGoesAtTheLeverTimesTheDistanceOverTau()
    {
        Assert.Equal(40_000f, ShuttleFlight.DriveSpeed(1f, 400_000), 1f);
        Assert.Equal(3_000f, ShuttleFlight.DriveSpeed(0.5f, 60_000), 1f);
        Assert.Equal(0f, ShuttleFlight.DriveSpeed(0f, 400_000));
        // switched on in orbit it carries on at the speed it had
        Assert.Equal(7_669f, ShuttleFlight.DriveSpeed(ShuttleFlight.DriveLever(7_669f, 400_000), 400_000), 1f);
        Assert.Equal(1f, ShuttleFlight.DriveLever(1e6f, 60_000));
    }

    [Fact]
    public void TheJetsHaveItInVacuumAndTheAirInThickAir()
    {
        Assert.Equal(1f, ShuttleFlight.JetShare(0f));
        Assert.Equal(1f, ShuttleFlight.JetShare(ShuttleFlight.JetQ * 0.5f));
        Assert.Equal(0f, ShuttleFlight.JetShare(ShuttleFlight.JetQ));
        Assert.Equal(0f, ShuttleFlight.JetShare(30_000f));
    }

    [Fact]
    public void HandsOffTheJetsHoldTheAttitudeAndTheStickTurnsIt()
    {
        var s = new State { Attitude = new Basis(Vector3.Right, 0.3f) };
        for (int i = 0; i < 600; i++) ShuttleFlight.Jets(ref s, Vector2.Zero, drive: false, Dt);
        Assert.Equal(0.3f, PitchOf(s.Attitude), 3);
        // a full stick back for 4 s: about the jets' rate, less the spin-up
        for (int i = 0; i < 240; i++) ShuttleFlight.Jets(ref s, new Vector2(0, 1), drive: false, Dt);
        float pitched = PitchOf(s.Attitude) - 0.3f;
        _out.WriteLine($"pitched {Mathf.RadToDeg(pitched):F1}° in 4 s");
        Assert.InRange(pitched, ShuttleFlight.JetPitchRate * 3f, ShuttleFlight.JetPitchRate * 4f);
    }

    [Fact]
    public void InTheDriveABankTurnsTheNoseLikeAPlane()
    {
        var s = new State { Attitude = new Basis(Vector3.Back, -0.5f) };   // right wing down
        Assert.True(BankOf(s.Attitude) > 0.4f);
        float yaw0 = s.Yaw;
        for (int i = 0; i < 120; i++) ShuttleFlight.Jets(ref s, Vector2.Zero, drive: true, Dt);
        // a right turn: the heading (minus the yaw) grows
        Assert.True(s.Yaw < yaw0 - 0.2f, $"yaw {s.Yaw}");
        Assert.InRange(PitchOf(s.Attitude), -0.01f, 0.01f);
    }

    [Fact]
    public void ReleveledWithTheEarthsUp()
    {
        var level = new Basis(Vector3.Up, 0.4f);
        var to = new Vector3(0, Mathf.Cos(0.1f), -Mathf.Sin(0.1f));
        var turned = ShuttleFlight.Relevel(level, Vector3.Up, to);
        // still level over the new up: the nose square to it, the wings too
        Assert.Equal(0f, (-turned.Z).Dot(to), 4);
        Assert.Equal(0f, turned.X.Dot(to), 4);
        Assert.Equal(level, ShuttleFlight.Relevel(level, Vector3.Up, Vector3.Up));
    }

    [Fact]
    public void ReEntryGlowsWhereThePeakHeatingIs()
    {
        Assert.Equal(0f, ShuttleFlight.Heat(Density(0f), 300f));
        Assert.Equal(0f, ShuttleFlight.Heat(Density(200_000f), 7_800f));
        // the orbiter's peak heating, 7 km/s at 70 km: white hot
        float peak = ShuttleFlight.Heat(Density(70_000f), 7_000f);
        _out.WriteLine($"heat at 70 km, 7 km/s: {peak:F2}; at 80 km, 7.6 km/s: {ShuttleFlight.Heat(Density(80_000f), 7_600f):F2}");
        Assert.InRange(peak, 0.9f, 1f);
        Assert.True(ShuttleFlight.Heat(Density(80_000f), 7_600f) > 0.3f);
    }

    [Fact]
    public void InThinAirHandsOffItHoldsItsAttitudeNotThePath()
    {
        // 70 km up at 2 km/s, nose 20° up, the path level: no air to speak of, so the jets hold the nose
        var s = Parked(Spec, 0f) with
        {
            OnGround = false, GearDown = false, Gear = 0f,
            Attitude = new Basis(Vector3.Right, 0.35f),
            Velocity = new Vector3(0, 0, -2_000),
        };
        for (int i = 0; i < 600; i++) Step(Spec, ref s, new Controls(), new Env(false, 70_000f), Dt);
        _out.WriteLine($"pitch {Mathf.RadToDeg(PitchOf(s.Attitude)):F1}° after 10 s, path {Mathf.RadToDeg(Mathf.Asin(s.Velocity.Y / s.Velocity.Length())):F1}°");
        Assert.Equal(0.35f, PitchOf(s.Attitude), 2);
    }
}
