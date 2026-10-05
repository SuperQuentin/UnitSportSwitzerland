using Godot;
using UnitSport.Player;
using UnitSport.World;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The boat model (src/Player/BoatModel.cs) and the two boats (src/Player/BoatCatalog.cs), linked in
/// (#302): floating, stability, the hump and the plane, top speed, steering, the waves, beaching.
/// </summary>
public class BoatTests
{
    private readonly ITestOutputHelper _out;
    public BoatTests(ITestOutputHelper output) => _out = output;

    /// <summary>A lake: level 0, the bed <see cref="Depth"/> down, or a beach rising from <see cref="ShoreX"/>; the real waves at a sea state.</summary>
    private sealed class Lake : IBoatWater
    {
        public float Depth = 20f;
        public float ShoreX = float.PositiveInfinity;
        public float Slope = 0.08f;
        public double T;
        private readonly float[] _amp = new float[WaveSpectrum.Count];
        private readonly float _sea;

        public Lake(float seaState = 0f)
        {
            _sea = seaState;
            WaveSpectrum.Amplitudes(seaState, _amp);
        }

        public bool Surface(float x, float z, out float level, out Vector3 flow)
        {
            level = (float)WaveSpectrum.HeightAt(5000 + x, 5000 + z, T, 1f, _amp);
            WaveSpectrum.Velocity(5000 + x, 5000 + z, T, 1f, _amp, out double vx, out double vy, out double vz);
            flow = new Vector3((float)vx, (float)vy, (float)vz);
            return Bed(x, z) < level;
        }

        public float Bed(float x, float z) => x > ShoreX ? -Depth + (x - ShoreX) * Slope : -Depth;

        public Vector3 Wind => Vector3.Zero;
    }

    private const float Dt = 1f / 60f;

    /// <summary>A boat floating level at rest on <paramref name="water"/>, its keel at the surface.</summary>
    private static BoatState Afloat(BoatSpec s, float yaw = 0f) =>
        BoatState.At(new Vector3(0, s.CentreHeight - 0.2f, 0), yaw, Vector3.Zero);

    private static void Run(BoatSpec s, ref BoatState b, Lake water, BoatControls c, float seconds, Action<float>? each = null)
    {
        for (float t = 0; t < seconds; t += Dt)
        {
            water.T += Dt;
            BoatDynamics.Step(s, ref b, c, water, Dt);
            each?.Invoke(t);
        }
    }

    private static float Deg(float rad) => rad * 180f / Mathf.Pi;

    /// <summary>The planing boats (#302); the steamer (#303) has its own tests (SteamerTests).</summary>
    public static IEnumerable<object[]> Boats => BoatCatalog.All.Where(s => s.LiftShare > 0f).Select(s => new object[] { s.Name });
    private static BoatSpec Spec(string name) => BoatCatalog.All.First(s => s.Name == name);

    [Theory]
    [MemberData(nameof(Boats))]
    public void Floats_level_at_its_draft_in_calm_water(string name)
    {
        var s = Spec(name);
        var b = Afloat(s);
        var lake = new Lake();
        Run(s, ref b, lake, default, 20f);
        float draft = -(b.Position.Y - s.CentreHeight);
        _out.WriteLine($"{name}: draft {draft:F3} m, immersion {b.Immersion:F3}, pitch {Deg(b.Pitch):F2}°, roll {Deg(b.Roll):F2}°, v {b.Velocity.Length():F3}");
        Assert.InRange(b.Immersion, 0.95f, 1.05f);
        Assert.InRange(draft, 0.12f, 0.5f);
        Assert.True(Mathf.Abs(Deg(b.Pitch)) < 3f && Mathf.Abs(Deg(b.Roll)) < 1.5f, $"level: pitch {Deg(b.Pitch):F1}°, roll {Deg(b.Roll):F1}°");
        Assert.True(b.Velocity.Length() < 0.05f);
    }

    [Theory]
    [MemberData(nameof(Boats))]
    public void Rights_itself_from_a_heel(string name)
    {
        var s = Spec(name);
        var b = Afloat(s);
        var lake = new Lake();
        Run(s, ref b, lake, default, 5f);
        b.Attitude = b.Attitude * new Quaternion(Vector3.Back, 0.45f);   // 26° over
        Run(s, ref b, lake, default, 8f);
        _out.WriteLine($"{name}: roll after 8 s {Deg(b.Roll):F2}°");
        Assert.True(Mathf.Abs(Deg(b.Roll)) < 3f);
    }

    [Theory]
    [MemberData(nameof(Boats))]
    public void Climbs_the_hump_onto_the_plane_and_reaches_its_top_speed(string name)
    {
        var s = Spec(name);
        var b = Afloat(s);
        var lake = new Lake();
        Run(s, ref b, lake, default, 3f);
        float plane = float.NaN, maxTrim = 0f, hump = 0f;
        float risen = float.NaN, restY = b.Position.Y;
        Run(s, ref b, lake, new BoatControls(1f, 0f, 0f), 50f, t =>
        {
            if (float.IsNaN(plane) && s.Planing(b.WaterSpeed) > 0.95f) plane = t;
            if (t < 6f) { maxTrim = Mathf.Max(maxTrim, Deg(b.Pitch)); if (b.WaterSpeed < s.HumpSpeed * 1.3f) hump = t; }
            if (float.IsNaN(risen) && b.Position.Y - restY > 0.06f) risen = t;
        });
        float top = b.WaterSpeed;
        _out.WriteLine($"{name}: on the plane in {plane:F1} s, top {top * 3.6f:F1} km/h ({top / 0.5144f:F1} kn) of {s.TopSpeed * 3.6f:F0}, " +
            $"bow up {maxTrim:F1}° over the hump, immersion {b.Immersion:F2}, running trim {Deg(b.Pitch):F1}°, risen {b.Position.Y - restY:F2} m");
        Assert.InRange(top, s.TopSpeed * 0.9f, s.TopSpeed * 1.1f);
        Assert.True(plane < 8f, $"planes in {plane:F1} s");
        Assert.True(b.Immersion < 0.6f, $"rides high on the plane ({b.Immersion:F2})");
        Assert.True(maxTrim > 1.5f, $"bow up over the hump ({maxTrim:F1}°)");
        Assert.InRange(Deg(b.Pitch), -1f, 8f);
    }

    [Theory]
    [MemberData(nameof(Boats))]
    public void Turns_hard_at_speed_without_capsizing_in_calm_water(string name)
    {
        var s = Spec(name);
        var b = Afloat(s);
        var lake = new Lake();
        Run(s, ref b, lake, new BoatControls(1f, 0f, 0f), 25f);
        float worstRoll = 0f;
        float minX = 1e9f, maxX = -1e9f, minZ = 1e9f, maxZ = -1e9f;
        float speed = 0f, rollSum = 0f;
        int rolls = 0;
        Run(s, ref b, lake, new BoatControls(1f, 0f, 1f), 30f, t =>
        {
            worstRoll = Mathf.Max(worstRoll, Mathf.Abs(Deg(b.Roll)));
            if (t > 10f)
            {
                minX = Mathf.Min(minX, b.Position.X); maxX = Mathf.Max(maxX, b.Position.X);
                minZ = Mathf.Min(minZ, b.Position.Z); maxZ = Mathf.Max(maxZ, b.Position.Z);
                speed = new Vector2(b.Velocity.X, b.Velocity.Z).Length();
                rollSum += Deg(b.Roll);
                rolls++;
            }
        });
        float circle = 0.5f * (maxX - minX + maxZ - minZ);
        _out.WriteLine($"{name}: turning circle {circle:F1} m at {speed * 3.6f:F0} km/h, roll {rollSum / rolls:F1}° on average (worst {worstRoll:F1}°)");
        Assert.True(worstRoll < 50f, $"stays upright ({worstRoll:F0}°)");
        Assert.InRange(circle, 6f, 90f);
        Assert.True(rollSum / rolls > 1f, "leans into the turn (starboard down)");
    }

    [Fact]
    public void A_jet_does_not_steer_off_the_throttle()
    {
        var s = BoatCatalog.Jetski;
        // the same run each time (same waves): 8 s flat out, then 1.5 s with these controls
        float Turned(BoatControls then)
        {
            var b = Afloat(s);
            var lake = new Lake();
            Run(s, ref b, lake, new BoatControls(1f, 0f, 0f), 8f);
            float yaw0 = b.Yaw(0f);
            Run(s, ref b, lake, then, 1.5f);
            return Mathf.Abs(Mathf.AngleDifference(yaw0, b.Yaw(0f)));
        }
        float drift = Turned(new BoatControls(0f, 0f, 0f));
        float coasting = Mathf.Abs(Turned(new BoatControls(0f, 0f, 1f)) - drift);
        float powered = Turned(new BoatControls(1f, 0f, 1f));
        _out.WriteLine($"jetski: heading change in 1.5 s at full helm, off the throttle {Deg(coasting):F1}° (beyond the {Deg(drift):F1}° it wanders anyway), on it {Deg(powered):F1}°");
        Assert.True(coasting < Mathf.DegToRad(2f));
        Assert.True(powered > Mathf.DegToRad(45f));
    }

    [Theory]
    [MemberData(nameof(Boats))]
    public void Rides_a_gamey_sea_flat_out_and_stays_afloat(string name)
    {
        var s = Spec(name);
        var b = Afloat(s);
        var lake = new Lake(1f);
        float air = 0f, worstRoll = 0f, lowest = 0f, pitchMin = 0f, pitchMax = 0f;
        bool thrown = false;
        for (float t = 0; t < 60f; t += Dt)
        {
            lake.T += Dt;
            var steer = t > 30f ? Mathf.Sin(t * 0.4f) * 0.4f : 0f;
            if (BoatDynamics.Step(s, ref b, new BoatControls(1f, 0f, steer), lake, Dt) == BoatEvent.Thrown) thrown = true;
            air = Mathf.Max(air, b.Airborne);
            worstRoll = Mathf.Max(worstRoll, Mathf.Abs(Deg(b.Roll)));
            lowest = Mathf.Min(lowest, b.Position.Y);
            pitchMin = Mathf.Min(pitchMin, Deg(b.Pitch));
            pitchMax = Mathf.Max(pitchMax, Deg(b.Pitch));
        }
        _out.WriteLine($"{name}, gamey: longest airborne {air:F2} s, pitch {pitchMin:F0}..{pitchMax:F0}°, worst roll {worstRoll:F0}°, lowest {lowest:F2} m, " +
            $"speed {b.WaterSpeed * 3.6f:F0} km/h, thrown {thrown}");
        Assert.True(lowest > -2f, "never goes under");
        // Rolling over is the spec's own FlipAngle, the angle BoatModel counts as capsized, not a
        // literal: the jetski's is 1.6 rad = 92°, and its harsh gamey ride is deliberate (#376,
        // docs/notes/player/boats.md). The rider is still thrown by the landing rule well before.
        Assert.True(worstRoll < Deg(s.FlipAngle), $"never rolls over ({worstRoll:F0}° of {Deg(s.FlipAngle):F0}°)");
        Assert.True(pitchMax - pitchMin > 2f, "the waves pitch it");
    }

    [Theory]
    [MemberData(nameof(Boats))]
    public void Drifts_with_the_waves_parked_in_a_gamey_sea(string name)
    {
        var s = Spec(name);
        var b = Afloat(s);
        var lake = new Lake(1f);
        float hi = -9f, lo = 9f, worstRoll = 0f;
        Run(s, ref b, lake, default, 60f, t =>
        {
            if (t < 10f) return;
            hi = Mathf.Max(hi, b.Position.Y);
            lo = Mathf.Min(lo, b.Position.Y);
            worstRoll = Mathf.Max(worstRoll, Mathf.Abs(Deg(b.Roll)));
        });
        _out.WriteLine($"{name}, parked, gamey: heave {lo:F2}..{hi:F2} m, worst roll {worstRoll:F0}°, immersion {b.Immersion:F2}");
        Assert.True(hi - lo > 0.3f, "the swell lifts it");
        Assert.True(worstRoll < 45f);
    }

    /// <summary>The swell's drift for these tests: the Stokes drift of the gamey waves, a little more.</summary>
    private sealed class DriftingLake : IBoatWater
    {
        private readonly Lake _lake = new(1f);
        public Vector3 Current = new(0.15f, 0f, 0.05f);
        public double T { get => _lake.T; set => _lake.T = value; }

        public bool Surface(float x, float z, out float level, out Vector3 flow)
        {
            bool wet = _lake.Surface(x, z, out level, out flow);
            flow += Current;
            return wet;
        }

        public float Bed(float x, float z) => _lake.Bed(x, z);
        public Vector3 Wind => Vector3.Zero;
    }

    [Theory]
    [MemberData(nameof(Boats))]
    [InlineData("Paddle steamer")]
    public void Moored_it_stays_on_its_spot_in_a_gamey_sea(string name)
    {
        var s = BoatCatalog.All.First(x => x.Name == name);
        float yaw = 0.7f;
        var b = Afloat(s, yaw);
        var spot = b.Position;
        var lake = new DriftingLake();
        var free = b;
        var freeLake = new DriftingLake();
        float worst = 0f, worstTurn = 0f, hi = -9f, lo = 9f;
        for (float t = 0; t < 60f; t += Dt)
        {
            lake.T += Dt;
            BoatDynamics.Step(s, ref b, default, lake, Dt);
            BoatDynamics.Moor(ref b, spot, yaw, Dt);
            freeLake.T += Dt;
            BoatDynamics.Step(s, ref free, default, freeLake, Dt);
            worst = Mathf.Max(worst, new Vector2(b.Position.X - spot.X, b.Position.Z - spot.Z).Length());
            worstTurn = Mathf.Max(worstTurn, Mathf.Abs(Deg(Mathf.AngleDifference(b.Yaw(yaw), yaw))));
            if (t > 10f) { hi = Mathf.Max(hi, b.Position.Y); lo = Mathf.Min(lo, b.Position.Y); }
        }
        float drifted = new Vector2(free.Position.X - spot.X, free.Position.Z - spot.Z).Length();
        _out.WriteLine($"{name}, moored, gamey, 0.16 m/s of drift: off its spot {worst:F2} m at worst (free: {drifted:F1} m in 60 s), heading {worstTurn:F1}° off, heave {hi - lo:F2} m");
        Assert.True(worst < 1f, $"stays within a metre of its spot ({worst:F2} m)");
        Assert.True(worstTurn < 10f, $"and near its heading ({worstTurn:F1}°)");
        Assert.True(drifted > 3f * worst, "a free one drifts off");
        Assert.True(hi - lo > 0.1f, "it still rides the swell");
    }

    [Theory]
    [MemberData(nameof(Boats))]
    public void Runs_aground_on_a_beach_and_stops_bow_up(string name)
    {
        var s = Spec(name);
        var b = Afloat(s, yaw: -Mathf.Pi / 2);   // bow to +X
        var lake = new Lake { ShoreX = 20f, Depth = 3f, Slope = 0.1f };
        Run(s, ref b, lake, new BoatControls(0.35f, 0f, 0f), 30f);
        _out.WriteLine($"{name}: aground at x {b.Position.X:F1}, speed {b.Velocity.Length():F2} m/s, pitch {Deg(b.Pitch):F1}°, grounded {b.Grounded}");
        Assert.True(b.Grounded);
        Assert.True(b.Velocity.Length() < 1f, "the beach stops it");
        Assert.True(b.Position.X < 80f, "not driven far up the land");
    }
}
