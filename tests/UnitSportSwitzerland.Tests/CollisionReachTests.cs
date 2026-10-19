using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Which tiles a server-run bot gets collision on (#837, src/Terrain/CollisionReach.cs, linked in).</summary>
public class CollisionReachTests
{
    private static readonly TileId Here = new(2600, 1200);   // E [2600000, 2601000) x N [1200000, 1201000)

    [Fact]
    public void A_bot_mid_tile_wants_only_its_own_tile()
    {
        double e = 2600500, n = 1200500;
        Assert.True(CollisionReach.Wants(Here, e, n, e, n, airborne: false));
        Assert.False(CollisionReach.Wants(new TileId(2601, 1200), e, n, e, n, airborne: false));
        Assert.False(CollisionReach.Wants(new TileId(2599, 1201), e, n, e, n, airborne: false));
    }

    [Fact]
    public void Near_a_corner_it_wants_the_four_tiles_round_it()
    {
        double e = 2600900, n = 1200900;   // 100 m from the NE corner
        int wanted = 0;
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
                if (CollisionReach.Wants(new TileId(2600 + de, 1200 + dn), e, n, e, n, airborne: false)) wanted++;
        Assert.Equal(4, wanted);
    }

    [Fact]
    public void A_tile_is_held_to_the_release_radius_then_freed()
    {
        var east = new TileId(2601, 1200);
        double n = 1200500;
        Assert.False(CollisionReach.Wants(east, 2600650, n, 2600650, n, false));                  // 350 m: not wanted
        Assert.True(CollisionReach.Wants(east, 2600650, n, 2600650, n, false, holding: true));    // but held
        Assert.False(CollisionReach.Wants(east, 2600550, n, 2600550, n, false, holding: true));   // 450 m: freed
    }

    [Fact]
    public void A_driving_bot_wants_the_ground_ahead_clamped()
    {
        // 30 m/s east from mid-tile: 300 m ahead in 10 s -> the next tile's edge is 200 m from there
        var ahead = CollisionReach.Ahead(2600500, 1200500, 600, 30, 0, 0);
        Assert.Equal(2600800, ahead.E, 3);
        Assert.True(CollisionReach.Wants(new TileId(2601, 1200), 2600500, 1200500, ahead.E, ahead.N, false));
        // 100 m/s: clamped to 600 m
        var far = CollisionReach.Ahead(0, 0, 0, 100, 0, -10);
        Assert.Equal(CollisionReach.LookAheadMaxM, far.E, 3);
        Assert.Equal(-60, far.Alt, 3);
    }

    [Fact]
    public void At_speed_4_the_look_ahead_covers_four_times_the_ground()
    {
        // a canopy sinking 5.9 m/s, 300 m over the ground (#931): 10 real s are 59 m at x1, 236 m at x4, so at x4
        // its look-ahead is already near the ground (the build runs in real time) while at x1 it is still in the air
        var x1 = CollisionReach.Ahead(2600500, 1200500, 300, 0, 0, -5.9);
        var x4 = CollisionReach.Ahead(2600500, 1200500, 300, 0, 0, -5.9, scale: 4);
        Assert.Equal(241, x1.Alt, 3);
        Assert.Equal(64, x4.Alt, 3);
        Assert.True(x1.Alt > CollisionReach.AirGapM);
        Assert.True(x4.Alt < CollisionReach.AirGapM);
        // the horizontal clamp holds at any scale
        Assert.Equal(CollisionReach.LookAheadMaxM, CollisionReach.Ahead(0, 0, 0, 30, 0, 0, scale: 8).E, 3);
    }

    [Fact]
    public void Nothing_is_wanted_high_in_the_air()
    {
        Assert.False(CollisionReach.Wants(Here, 2600500, 1200500, 2600500, 1200500, airborne: true));
    }

    [Fact]
    public void The_key_changes_only_across_a_250_m_cell()
    {
        Assert.Equal(CollisionReach.CellKey(2600010, 1200010), CollisionReach.CellKey(2600240, 1200240));
        Assert.NotEqual(CollisionReach.CellKey(2600240, 1200010), CollisionReach.CellKey(2600260, 1200010));
        Assert.NotEqual(CollisionReach.CellKey(2600010, 1200240), CollisionReach.CellKey(2600010, 1200260));
    }
}
