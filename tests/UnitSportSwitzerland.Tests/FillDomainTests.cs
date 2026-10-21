using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Where the generated fill may generate, and how it grows (#937, src/Terrain/FillDomain.cs, linked in).</summary>
public class FillDomainTests
{
    private const int Radius = 40;
    private static readonly TileId Spawn = new(2583, 1113);    // the default spawn, where a server's fill starts
    private static readonly TileId Zurich = new(2682, 1251);   // brsquadcheck's match, 170 km away

    [Fact]
    public void A_fill_reaches_its_radius_round_the_spawn_and_no_further()
    {
        var d = FillDomain.At(Spawn, Radius);
        Assert.True(d.Contains(Spawn));
        Assert.True(d.Contains(new TileId(Spawn.E + Radius, Spawn.N - Radius)));
        Assert.False(d.Contains(new TileId(Spawn.E + Radius + 1, Spawn.N)));
        Assert.False(d.Contains(Zurich));
    }

    [Fact]
    public void Reaching_a_far_tile_adds_a_fill_round_it_as_one_started_there()
    {
        var d = FillDomain.At(Spawn, Radius).Reach(Zurich, Radius);
        Assert.True(d.Contains(Zurich));
        Assert.True(d.Contains(new TileId(Zurich.E - Radius, Zurich.N + Radius)));
        Assert.False(d.Contains(new TileId(Zurich.E - Radius - 1, Zurich.N)));
        Assert.True(d.Contains(Spawn));   // never shrinks
        Assert.Equal(2, d.Boxes.Count);
        // the gap between the two boxes is not filled: only the places a body stands
        Assert.False(d.Contains(new TileId(2632, 1180)));
    }

    [Fact]
    public void Reaching_a_tile_already_in_the_domain_changes_nothing()
    {
        var d = FillDomain.At(Spawn, Radius);
        Assert.Same(d, d.Reach(new TileId(Spawn.E + 10, Spawn.N + 10), Radius));
        var far = d.Reach(Zurich, Radius);
        Assert.Same(far, far.Reach(new TileId(Zurich.E + 5, Zurich.N), Radius));
    }

    [Fact]
    public void Bounds_hold_every_box_and_the_real_set()
    {
        var real = new TileRect(2600, 1100, 2610, 1110).Grow(Radius);
        var d = FillDomain.At(Spawn, Radius).WithReal(real).Reach(Zurich, Radius);
        Assert.Equal(new TileRect(Spawn.E - Radius, real.MinN, Zurich.E + Radius, Zurich.N + Radius), d.Bounds);
        Assert.True(d.Contains(new TileId(2650, 1070)));   // in the real box only
    }

    [Fact]
    public void The_real_box_never_shrinks()
    {
        var first = new TileRect(2600, 1100, 2610, 1110);
        var d = FillDomain.At(Spawn, Radius).WithReal(first).WithReal(new TileRect(2700, 1200, 2701, 1201));
        Assert.Equal(first.Union(new TileRect(2700, 1200, 2701, 1201)), d.RealBox);
        Assert.Equal(first, FillDomain.At(Spawn, Radius).WithReal(first).WithReal(null).RealBox);
    }
}
