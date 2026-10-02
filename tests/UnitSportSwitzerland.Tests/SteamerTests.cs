using Godot;
using UnitSport.Player;
using UnitSport.World;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The CGN paddle steamer's model (#303, src/Player/BoatCatalog.cs <c>Steamer</c>, the paddle drive
/// in src/Player/BoatModel.cs): its draught, its slow way on and its long stop, its turning circle,
/// and that a gamey lake only rocks it.
/// </summary>
public class SteamerTests
{
    private readonly ITestOutputHelper _out;
    public SteamerTests(ITestOutputHelper output) => _out = output;

    private sealed class Lake : IBoatWater
    {
        public double T;
        private readonly float[] _amp = new float[WaveSpectrum.Count];
        public Lake(float seaState = 0f) => WaveSpectrum.Amplitudes(seaState, _amp);

        public bool Surface(float x, float z, out float level, out Vector3 flow)
        {
            level = (float)WaveSpectrum.HeightAt(5000 + x, 5000 + z, T, 1f, _amp);
            WaveSpectrum.Velocity(5000 + x, 5000 + z, T, 1f, _amp, out double vx, out double vy, out double vz);
            flow = new Vector3((float)vx, (float)vy, (float)vz);
            return true;
        }

        public float Bed(float x, float z) => -40f;
        public Vector3 Wind => Vector3.Zero;
    }

    private const float Dt = 1f / 60f;
    private static readonly BoatSpec S = BoatCatalog.Steamer;

    private static BoatState Afloat() => BoatState.At(new Vector3(0, S.CentreHeight - SteamerLines.Draught, 0), 0f, Vector3.Zero);

    private static void Run(ref BoatState b, Lake water, int order, float steer, float seconds, Action<float>? each = null)
    {
        var c = new BoatControls(Mathf.Max(0f, Telegraph.Lever(order)), Mathf.Max(0f, -Telegraph.Lever(order)), steer);
        for (float t = 0; t < seconds; t += Dt)
        {
            water.T += Dt;
            BoatDynamics.Step(S, ref b, c, water, Dt);
            each?.Invoke(t);
        }
    }

    private static float Deg(float rad) => rad * 180f / Mathf.Pi;
    private static float Speed(in BoatState b) => new Vector2(b.Velocity.X, b.Velocity.Z).Length();

    [Fact]
    public void Floats_level_at_its_published_draught()
    {
        var b = Afloat();
        var lake = new Lake();
        Run(ref b, lake, 0, 0f, 40f);
        float draught = -(b.Position.Y - S.CentreHeight);
        _out.WriteLine($"steamer: draught {draught:F2} m (La Suisse 1.68), pitch {Deg(b.Pitch):F2}°, roll {Deg(b.Roll):F2}°, immersion {b.Immersion:F3}");
        Assert.InRange(draught, 1.45f, 1.9f);
        Assert.True(Mathf.Abs(Deg(b.Pitch)) < 0.6f && Mathf.Abs(Deg(b.Roll)) < 0.3f, "level");
        Assert.True(b.Velocity.Length() < 0.02f);
    }

    [Fact]
    public void Gathers_way_slowly_to_its_trial_speed_and_stops_long()
    {
        var b = Afloat();
        var lake = new Lake();
        Run(ref b, lake, 0, 0f, 5f);
        float at10 = 0f, to90 = float.NaN;
        Run(ref b, lake, Telegraph.Max, 0f, 240f, t =>
        {
            if (t < 10f) at10 = Speed(b);
            if (float.IsNaN(to90) && Speed(b) > S.TopSpeed * 0.9f) to90 = t;
        });
        float top = Speed(b);
        _out.WriteLine($"steamer: {at10 * 3.6f:F1} km/h after 10 s, 90 % in {to90:F0} s, top {top * 3.6f:F1} km/h (trials 29.1)");
        Assert.InRange(top, S.TopSpeed * 0.92f, S.TopSpeed * 1.08f);
        Assert.True(at10 < 3f, $"slow to gather way ({at10:F1} m/s after 10 s)");
        Assert.True(to90 > 40f, $"90 % of its speed only after {to90:F0} s");

        // full astern from full ahead: the engine goes through stop, the ship runs on a long way
        float stopped = float.NaN, run = 0f;
        var astern = b;
        var from = b.Position;
        Run(ref astern, lake, -Telegraph.Max, 0f, 120f, t =>
        {
            if (!float.IsNaN(stopped) || astern.WaterSpeed >= 0.05f) return;
            stopped = t;
            run = new Vector2(astern.Position.X - from.X, astern.Position.Z - from.Z).Length();
        });
        // let go (stop): it coasts
        var coast = b;
        float coastFor = float.NaN;
        Run(ref coast, lake, 0, 0f, 400f, t => { if (float.IsNaN(coastFor) && Speed(coast) < 0.5f) coastFor = t; });
        _out.WriteLine($"steamer: full astern from full ahead stops in {stopped:F0} s and {run:F0} m; at STOP it coasts {coastFor:F0} s to 0.5 m/s");
        Assert.InRange(stopped, 20f, 90f);
        Assert.True(run > 80f, $"a long stop ({run:F0} m)");
        Assert.True(coastFor > stopped, "coasting takes longer than going astern");
    }

    [Fact]
    public void The_shaft_reverses_through_stop_at_the_engine_s_pace()
    {
        var b = Afloat();
        var lake = new Lake();
        Run(ref b, lake, Telegraph.Max, 0f, 20f);
        Assert.True(b.Shaft > 0.99f);
        float through = float.NaN;
        Run(ref b, lake, -Telegraph.Max, 0f, 30f, t => { if (float.IsNaN(through) && b.Shaft < 0f) through = t; });
        _out.WriteLine($"steamer: shaft through stop {through:F1} s after full astern was rung, {b.Shaft:F2} now");
        Assert.InRange(through, 8f, 16f);
        Assert.True(b.Shaft < -0.99f);
        Assert.Equal(-1, b.Gear);
        Assert.Equal(-1f, Telegraph.Lever(-Telegraph.Max));
        Assert.Equal(0f, Telegraph.Lever(0));
        Assert.Equal("HALF AHEAD", Telegraph.Name(3));
    }

    [Fact]
    public void Turns_wide_at_full_speed_and_barely_heels()
    {
        var b = Afloat();
        var lake = new Lake();
        Run(ref b, lake, Telegraph.Max, 0f, 150f);
        float minX = 1e9f, maxX = -1e9f, minZ = 1e9f, maxZ = -1e9f, worst = 0f, speed = 0f;
        Run(ref b, lake, Telegraph.Max, 1f, 240f, t =>
        {
            worst = Mathf.Max(worst, Mathf.Abs(Deg(b.Roll)));
            if (t < 60f) return;
            minX = Mathf.Min(minX, b.Position.X); maxX = Mathf.Max(maxX, b.Position.X);
            minZ = Mathf.Min(minZ, b.Position.Z); maxZ = Mathf.Max(maxZ, b.Position.Z);
            speed = Speed(b);
        });
        float circle = 0.5f * (maxX - minX + maxZ - minZ);
        _out.WriteLine($"steamer: turning circle {circle:F0} m at {speed * 3.6f:F0} km/h, worst heel {worst:F1}°");
        Assert.InRange(circle, 120f, 700f);
        Assert.True(worst < 6f, $"heels {worst:F1}°");
    }

    [Fact]
    public void A_gamey_lake_rocks_it_gently()
    {
        foreach (int order in new[] { 0, Telegraph.Max })
        {
            var b = Afloat();
            var lake = new Lake(1f);
            float pMin = 0f, pMax = 0f, rMin = 0f, rMax = 0f, hi = -9f, lo = 9f;
            Run(ref b, lake, order, 0f, 120f, t =>
            {
                if (t < 30f) return;
                pMin = Mathf.Min(pMin, Deg(b.Pitch)); pMax = Mathf.Max(pMax, Deg(b.Pitch));
                rMin = Mathf.Min(rMin, Deg(b.Roll)); rMax = Mathf.Max(rMax, Deg(b.Roll));
                hi = Mathf.Max(hi, b.Position.Y); lo = Mathf.Min(lo, b.Position.Y);
            });
            _out.WriteLine($"steamer, gamey, {Telegraph.Name(order)}: pitch {pMin:F2}..{pMax:F2}°, roll {rMin:F2}..{rMax:F2}°, heave {hi - lo:F2} m, {Speed(b) * 3.6f:F0} km/h");
            Assert.True(pMax - pMin > 0.2f || rMax - rMin > 0.4f, "the swell moves it");
            Assert.True(pMax - pMin < 5f, "gently in pitch");
            Assert.True(rMax - rMin < 10f, "gently in roll");
        }
    }
}
