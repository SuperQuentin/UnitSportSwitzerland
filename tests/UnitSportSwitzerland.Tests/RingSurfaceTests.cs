using UnitSport.Core;
using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Saturn's rings as a surface for the speed rule (#2030): the slab's distance, the nearest reach, the floor.</summary>
public class RingSurfaceTests
{
    private static readonly RingSlab Rings = CelestialBody.SaturnRingSlab;

    [Fact]
    public void The_slab_is_400_m_thick_between_its_radii_in_the_equator_plane()
    {
        Assert.Equal(0, Rings.Distance(new DVec3(100_000_000, 0, 150)));            // inside, near a face
        Assert.Equal(800, Rings.Distance(new DVec3(0, 100_000_000, 1_000)), 6);     // 1 km over the plane: 800 m over the face
        Assert.Equal(500, Rings.Distance(new DVec3(Rings.Outer + 500, 0, 0)), 6);   // beside the outer edge, in the plane
        Assert.Equal(5_000_000, Rings.Distance(new DVec3(Rings.Inner - 5_000_000, 0, 0)), 3);   // inside the gap to Saturn
        Assert.Equal(500, Rings.Distance(new DVec3(Rings.Outer + 300, 0, 600)), 6); // past the corner: (300, 400) → 500
    }

    [Fact]
    public void Near_saturn_the_nearest_reach_is_the_ring_and_the_cap_falls_to_a_drift()
    {
        var m = SkyClock.Now;
        // 10 km over the B ring's face, in Saturn's fixed frame
        var p = new DVec3(100_000_000, 0, 10_200);
        var (body, d, ring) = Frames.NearestReach(BodyId.Saturn, p, m);
        Assert.Equal(BodyId.Saturn, body);
        Assert.True(ring);
        Assert.Equal(10_000, d, 3);
        Assert.Equal(5_000f, SpectatorSpeed.SurfaceCap(d, SpectatorSpeed.RingFloor));
        // in the slab: the ring's floor, a drift, not a planet's 3 km/s
        Assert.Equal(SpectatorSpeed.RingFloor, SpectatorSpeed.SurfaceCap(0, SpectatorSpeed.RingFloor));
        // far off the plane over Saturn's pole the cloud tops are nearer: not a ring
        var overPole = new DVec3(0, 0, CelestialBody.Saturn.Shape.B + 5_000_000);
        Assert.False(Frames.NearestReach(BodyId.Saturn, overPole, m).Ring);
    }
}
