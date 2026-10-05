using UnitSport.Combat;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Server-side PvP hit checks (#468, src/Combat/HitGuard.cs, linked in).</summary>
public class HitGuardTests
{
    private const int Rifle = (int)UnitSport.Items.ItemId.Rifle, Shotgun = (int)UnitSport.Items.ItemId.Shotgun;

    [Fact]
    public void An_honest_rifle_at_its_rate_always_passes()
    {
        var g = new HitGuard();
        for (int i = 0; i < 200; i++)
            Assert.True(g.TryShot(7, Rifle, 0.16f, 1, 9, i * 0.16), $"shot {i}");
    }

    [Fact]
    public void Jitter_bunching_three_shots_together_passes()
    {
        var g = new HitGuard();
        double t = 10;
        Assert.True(g.TryShot(7, Rifle, 0.16f, 1, 9, t));
        // a stall: the next two shots' hits arrive with the third, a few ms apart
        Assert.True(g.TryShot(7, Rifle, 0.16f, 1, 9, t + 0.48));
        Assert.True(g.TryShot(7, Rifle, 0.16f, 1, 9, t + 0.481));
        Assert.True(g.TryShot(7, Rifle, 0.16f, 1, 9, t + 0.482));
    }

    [Fact]
    public void A_rapid_fire_cheat_is_capped_near_the_real_rate()
    {
        var g = new HitGuard();
        int passed = 0;
        // 100 shots a second for 10 s at a rifle that fires 6.25 a second
        for (int i = 0; i < 1000; i++)
            if (g.TryShot(7, Rifle, 0.16f, 1, 9, i * 0.01)) passed++;
        Assert.InRange(passed, 60, 77);   // 10 s × 6.25 × 1/0.85 ≈ 73, plus the burst of 3
    }

    [Fact]
    public void A_shotgun_volley_may_hit_several_players_once_each()
    {
        var g = new HitGuard();
        Assert.True(g.TryShot(7, Shotgun, 1f, 9, 1, 5.0));
        Assert.True(g.TryShot(7, Shotgun, 1f, 9, 2, 5.001));
        Assert.True(g.TryShot(7, Shotgun, 1f, 9, 3, 5.002));
        // the same victim again in the same instant is another shot: the burst pays for a few
        Assert.True(g.TryShot(7, Shotgun, 1f, 9, 1, 5.003));
        Assert.True(g.TryShot(7, Shotgun, 1f, 9, 1, 5.004));
        Assert.False(g.TryShot(7, Shotgun, 1f, 9, 1, 5.005));
    }

    [Fact]
    public void Budgets_are_per_shooter_and_per_weapon()
    {
        var g = new HitGuard();
        for (int i = 0; i < 3; i++) Assert.True(g.TryShot(7, Rifle, 1f, 1, 9, 1.0 + i * 0.001 * 100));
        Assert.False(g.TryShot(7, Rifle, 1f, 1, 9, 1.31));
        Assert.True(g.TryShot(8, Rifle, 1f, 1, 9, 1.31));
        Assert.True(g.TryShot(7, Shotgun, 1f, 9, 9, 1.31));
    }

    // ---- terrain ----

    /// <summary>A 200 m-high ridge 10 m wide at E = 500; flat at 0 elsewhere.</summary>
    private static float? Ridge(double e, double n) => Math.Abs(e - 500) < 30 ? 200f : 0f;

    [Fact]
    public void A_shot_over_flat_ground_is_clear()
        => Assert.True(HitGuard.TerrainClear((0, 0, 1.6), (300, 0, 1.0), (_, _) => 0f));

    [Fact]
    public void A_shot_through_a_ridge_is_refused()
        => Assert.False(HitGuard.TerrainClear((0, 0, 1.6), (1000, 0, 1.0), Ridge));

    [Fact]
    public void A_shot_over_the_ridge_from_high_up_is_clear()
        => Assert.True(HitGuard.TerrainClear((0, 0, 400), (1000, 0, 400), Ridge));

    [Fact]
    public void Players_in_rooms_under_the_ground_still_hit_each_other()
        // an interior lies ~3 km under its building: both ends deep under the surface
        => Assert.True(HitGuard.TerrainClear((0, 0, -2998), (20, 0, -2999), (_, _) => 450f));

    [Fact]
    public void A_valley_floor_the_lattice_fills_in_is_not_a_wall()
    {
        // the coarse surface reads 25 m above two players on a valley floor 300 m apart
        Assert.True(HitGuard.TerrainClear((0, 0, 101.6), (300, 0, 101), (_, _) => 125f));
    }

    [Fact]
    public void Unknown_ground_never_blocks()
        => Assert.True(HitGuard.TerrainClear((0, 0, 1.6), (1000, 0, 1), (_, _) => null));
}
