using System.Globalization;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.World;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The wave field's maths (src/World/WaveSpectrum.cs), the water layer (src/Terrain/WaterLayer.cs)
/// and <c>/seastate</c> parsing (src/World/SeaStateCommand.cs), linked in (#299).
/// </summary>
public class WaterTests
{
    private static float[] Amp(float seaState)
    {
        var a = new float[WaveSpectrum.Count];
        WaveSpectrum.Amplitudes(seaState, a);
        return a;
    }

    [Fact]
    public void Waves_repeat_over_the_pattern_period_so_the_floating_origin_cannot_shift_them()
    {
        var amp = Amp(1f);
        foreach (var (x, z) in new[] { (12.3, 45.6), (5000.0, 8000.0), (9599.0, 1.0) })
        {
            WaveSpectrum.Displace(x, z, 300, 1f, amp, out var dx, out var dy, out var dz);
            WaveSpectrum.Displace(x + WaveSpectrum.PeriodM, z - WaveSpectrum.PeriodM, 300, 1f, amp, out var ex, out var ey, out var ez);
            Assert.Equal(dx, ex, 6);
            Assert.Equal(dy, ey, 6);
            Assert.Equal(dz, ez, 6);
        }
    }

    [Fact]
    public void Pattern_coordinates_are_lv95_modulo_the_period()
    {
        Assert.Equal(2_600_000 % 9600.0, WaveSpectrum.PatternX(2_600_000), 9);
        double z = WaveSpectrum.PatternZ(1_200_000);
        Assert.InRange(z, 0, WaveSpectrum.PeriodM);
        Assert.Equal(0, (z + 1_200_000) % WaveSpectrum.PeriodM, 6);
    }

    [Fact]
    public void Waves_repeat_over_the_time_loop_so_the_clock_can_wrap()
    {
        var amp = Amp(1f);
        WaveSpectrum.Displace(100, 200, 17.25, 1f, amp, out var dx, out var dy, out var dz);
        WaveSpectrum.Displace(100, 200, 17.25 + WaveSpectrum.LoopS, 1f, amp, out var ex, out var ey, out var ez);
        Assert.Equal(dy, ey, 6);
        Assert.Equal(dx, ex, 6);
        Assert.Equal(dz, ez, 6);
        Assert.Equal(17.25, WaveSpectrum.WaveTime(17.25 + 3 * WaveSpectrum.LoopS), 6);
        Assert.InRange(WaveSpectrum.WaveTime(-1), 0, WaveSpectrum.LoopS);
    }

    [Fact]
    public void Every_wave_keeps_deep_water_dispersion_within_a_percent()
    {
        foreach (var w in WaveSpectrum.Waves)
        {
            double ideal = Math.Sqrt(WaveSpectrum.Gravity * w.K);
            Assert.InRange(w.Omega / ideal, 0.99, 1.01);
        }
    }

    [Fact]
    public void Gamey_is_a_big_swell_and_calm_a_ripple()
    {
        float gamey = Amp(1f).Sum(), calm = Amp(0f).Sum(), chop = Amp(SeaStateCommand.Chop).Sum();
        Assert.InRange(gamey, 1.0f, 1.4f);         // 1.5-2 m crest to trough, typically
        Assert.InRange(calm, 0.01f, 0.06f);        // a few centimetres
        Assert.InRange(chop, calm, gamey * 0.4f);
        Assert.Equal(0f, Amp(0f)[0]);              // no long swell on a calm day
        // steepness: far from the Gerstner loops (sum k A Q = 1)
        double steep = 0;
        var a = Amp(1f);
        for (int i = 0; i < WaveSpectrum.Count; i++) steep += WaveSpectrum.Waves[i].K * a[i] * WaveSpectrum.Chop;
        Assert.InRange(steep, 0.1, 0.5);
    }

    [Fact]
    public void Height_inverts_the_horizontal_motion()
    {
        var amp = Amp(1f);
        for (double x0 = 0; x0 < 200; x0 += 13.7)
        {
            double z0 = x0 * 0.6 + 40;
            WaveSpectrum.Displace(x0, z0, 55.5, 0.9f, amp, out var dx, out var dy, out var dz);
            double h = WaveSpectrum.HeightAt(x0 + dx, z0 + dz, 55.5, 0.9f, amp);
            Assert.Equal(dy, h, 2);   // within 5 mm
        }
    }

    [Fact]
    public void Normal_and_velocity_match_finite_differences()
    {
        var amp = Amp(1f);
        const double x0 = 321.0, z0 = 654.0, t = 99.0, e = 1e-3;
        WaveSpectrum.Normal(x0, z0, t, 1f, amp, out var nx, out var ny, out var nz);
        Assert.Equal(1.0, Math.Sqrt(nx * nx + ny * ny + nz * nz), 6);
        Assert.True(ny > 0.5);

        // tangents of the displaced surface
        static (double, double, double) P(double x, double z, double t, float[] amp)
        {
            WaveSpectrum.Displace(x, z, t, 1f, amp, out var dx, out var dy, out var dz);
            return (x + dx, dy, z + dz);
        }
        var (ax, ay, az) = P(x0 + e, z0, t, amp);
        var (bx, by, bz) = P(x0 - e, z0, t, amp);
        var (cx, cy, cz) = P(x0, z0 + e, t, amp);
        var (fx, fy, fz) = P(x0, z0 - e, t, amp);
        double tx0 = ax - bx, tx1 = ay - by, tx2 = az - bz;
        double tz0 = cx - fx, tz1 = cy - fy, tz2 = cz - fz;
        double mx = tz1 * tx2 - tz2 * tx1, my = tz2 * tx0 - tz0 * tx2, mz = tz0 * tx1 - tz1 * tx0;
        double len = Math.Sqrt(mx * mx + my * my + mz * mz);
        Assert.Equal(mx / len, nx, 4);
        Assert.Equal(my / len, ny, 4);
        Assert.Equal(mz / len, nz, 4);

        WaveSpectrum.Velocity(x0, z0, t, 1f, amp, out var vx, out var vy, out var vz);
        WaveSpectrum.Displace(x0, z0, t + e, 1f, amp, out var px, out var py, out var pz);
        WaveSpectrum.Displace(x0, z0, t - e, 1f, amp, out var qx, out var qy, out var qz);
        Assert.Equal((px - qx) / (2 * e), vx, 4);
        Assert.Equal((py - qy) / (2 * e), vy, 4);
        Assert.Equal((pz - qz) / (2 * e), vz, 4);
    }

    [Fact]
    public void No_scale_no_waves()
    {
        WaveSpectrum.Displace(10, 10, 10, 0f, Amp(1f), out var dx, out var dy, out var dz);
        Assert.Equal((0.0, 0.0, 0.0), (dx, dy, dz));
    }

    // ---- the water layer -------------------------------------------------------------------

    private static readonly TileId Tile = new(2600, 1200);

    /// <summary>A full grid from a height function of (col, row) in metres.</summary>
    private static ChunkGrid Grid(Func<int, int, double> height)
    {
        int n = ChunkFormat.GridSize;
        var q = new ushort[n * n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                q[r * n + c] = ChunkFormat.Quantize(height(c, r));
        return new ChunkGrid(Tile, q, (float)ChunkFormat.Dequantize(q.Min()), (float)ChunkFormat.Dequantize(q.Max()));
    }

    [Fact]
    public void Legacy_layer_sits_on_the_terrain_and_has_no_waves()
    {
        var grid = Grid((c, r) => 372.0);
        var cover = new byte[ChunkFormat.GridSize * ChunkFormat.GridSize];
        // a 100 m wide lake strip in the west of the tile
        for (int r = 0; r < ChunkFormat.GridSize; r++)
            for (int c = 0; c <= 100; c++)
                cover[r * ChunkFormat.GridSize + c] = (byte)CoverClass.Water;
        var layer = WaterLayer.FromCover(grid, cover)!;
        Assert.NotNull(layer);
        Assert.True(layer.TrySample(40, 500, out float level, out float scale));
        Assert.Equal(372f + WaterLayer.LegacyLift, level, 2);
        Assert.True(scale < 0.01f, $"legacy water is 0.12 m deep: no waves, got {scale}");
        Assert.False(layer.TrySample(300, 500, out _, out _));

        Assert.Null(WaterLayer.FromCover(grid, new byte[cover.Length]));
    }

    [Fact]
    public void Deep_open_water_takes_the_sea_state_and_the_shore_does_not()
    {
        // a lake east of x = 500, its bed sloping from the waterline to 40 m deep
        var grid = Grid((c, r) => c < 500 ? 501.0 : 500.0 - (c - 500) * 0.1);
        var level = new float[WaterLayer.Size * WaterLayer.Size];
        for (int r = 0; r < WaterLayer.Size; r++)
            for (int c = 0; c < WaterLayer.Size; c++)
                level[r * WaterLayer.Size + c] = c * WaterLayer.Stride >= 500 ? 500f : float.NaN;
        var fetch = new float[level.Length];
        Array.Fill(fetch, 3000f);
        var layer = WaterLayer.Create(new WaterTile { Level = level, FetchM = fetch }, grid)!;
        Assert.True(layer.TrySample(900, 500, out float l, out float deep));
        Assert.Equal(500f, l, 3);
        Assert.Equal(WaterLayer.FetchFactor(3000f), deep, 2);
        // right at the bank the bed is the bank: depth negative, no waves
        Assert.True(layer.TrySample(500, 500, out _, out float shore));
        Assert.True(shore < deep);
    }

    [Fact]
    public void Sampling_a_bank_uses_only_the_wet_corners()
    {
        var grid = Grid((c, r) => 400.0);
        var level = new float[WaterLayer.Size * WaterLayer.Size];
        Array.Fill(level, float.NaN);
        // a sloping river two samples wide along column 10..11, level falling southwards
        for (int r = 0; r < WaterLayer.Size; r++)
        {
            level[r * WaterLayer.Size + 10] = 410f - r * 0.01f;
            level[r * WaterLayer.Size + 11] = 410f - r * 0.01f;
        }
        var layer = WaterLayer.Create(new WaterTile { Level = level }, grid)!;
        // between column 11 (wet) and 12 (dry), nearer 11: the dry corner does not drag it down
        Assert.True(layer.TrySample(11 * 2 + 0.6, 100, out float l, out _));
        Assert.Equal(410f - 50 * 0.01f, l, 3);
        Assert.False(layer.TrySample(12 * 2 + 0.5, 100, out _, out _));
    }

    [Fact]
    public void Shore_fetch_is_a_river_width_and_open_water_is_capped()
    {
        var level = new float[WaterLayer.Size * WaterLayer.Size];
        Array.Fill(level, float.NaN);
        for (int r = 0; r < WaterLayer.Size; r++)
            for (int c = 100; c < 110; c++)   // 20 m wide
                level[r * WaterLayer.Size + c] = 400f;
        var f = WaterLayer.ShoreFetch(level);
        Assert.InRange(f[250 * WaterLayer.Size + 105], 8f, 24f);
        Assert.Equal(0f, f[250 * WaterLayer.Size + 50]);

        Array.Fill(level, 400f);
        Assert.Equal(WaterLayer.OpenFetchM, WaterLayer.ShoreFetch(level)[250 * WaterLayer.Size + 250]);
        Assert.True(WaterLayer.FetchFactor(20f) < 0.02f);
        Assert.True(WaterLayer.FetchFactor(5000f) > 0.95f);
    }

    // ---- /seastate -------------------------------------------------------------------------

    [Theory]
    [InlineData("calm", 0f)]
    [InlineData(" Gamey ", 1f)]
    [InlineData("chop", SeaStateCommand.Chop)]
    [InlineData("storm", SeaStateCommand.Storm)]
    [InlineData("0.5", 0.5f)]
    [InlineData("0,25", 0.25f)]
    [InlineData("1", 1f)]
    public void Sea_state_parses(string text, float value)
    {
        Assert.True(SeaStateCommand.TryParse(text, out float v, out _));
        Assert.Equal(value, v, 4);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("tsunami")]
    [InlineData("NaN")]
    [InlineData("")]
    public void Sea_state_rejects(string text)
    {
        Assert.False(SeaStateCommand.TryParse(text, out _, out string error));
        Assert.Contains("seastate", error);
    }

    [Fact]
    public void Sea_state_parses_the_same_on_a_French_locale()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert.True(SeaStateCommand.TryParse("0.75", out float v, out _));
            Assert.Equal(0.75f, v, 4);
            Assert.Equal("0.50 (between chop and storm)", SeaStateCommand.Describe(0.5f));
            Assert.Equal("chop (0.35)", SeaStateCommand.Describe(0.35f));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

public class SeaStateArgsTests
{
    [Fact]
    public void Sea_state_flag()
    {
        Assert.Equal(1f, SeaStateCommand.FromArgs(["--server", "--sea-state", "gamey"], out _));
        Assert.Null(SeaStateCommand.FromArgs(["--server"], out string none));
        Assert.Equal(string.Empty, none);
        Assert.Null(SeaStateCommand.FromArgs(["--sea-state"], out string missing));
        Assert.NotEmpty(missing);
        Assert.Null(SeaStateCommand.FromArgs(["--sea-state", "2"], out string bad));
        Assert.NotEmpty(bad);
    }
}
