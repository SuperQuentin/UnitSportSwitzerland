using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Collision patches round bodies (#1511, G2 C2): which patches a body wants, built and released by the provider.</summary>
public class CollisionPatchesTests
{
    private static EcefPos At(double lat, double lon) => Ellipsoid.Wgs84.ToEcef(lat * Math.PI / 180, lon * Math.PI / 180, 500);

    [Fact]
    public void The_reach_in_metres_wants_near_holds_wider_and_never_in_the_air()
    {
        Assert.True(CollisionReach.Wants(250, 9999, false));
        Assert.True(CollisionReach.Wants(9999, 250, false));
        Assert.False(CollisionReach.Wants(350, 350, false));
        Assert.True(CollisionReach.Wants(350, 9999, false, holding: true));
        Assert.False(CollisionReach.Wants(0, 0, true));
    }

    [Fact]
    public void Patches_near_a_point_are_within_the_radius_and_hold_the_point()
    {
        var p = At(46.2, 6.14);
        var near = CollisionPatches.Near(p, CollisionReach.RadiusM);
        var own = CubeKey.FromLatLon(46.2, 6.14, CollisionPatches.PatchLevel);
        Assert.Equal(0, near[own]);
        Assert.All(near, kv => Assert.InRange(kv.Value, 0, CollisionReach.RadiusM));
        Assert.All(near.Keys, k => Assert.Equal(CollisionPatches.PatchLevel, k.Level));
        // ~150 m patches within 300 m: a disc of about 600 m across
        Assert.InRange(near.Count, 16, 60);
    }

    [Fact]
    public async Task A_body_gets_its_patches_built_and_released_when_it_leaves()
    {
        int built = 0;
        var provider = new CollisionPatches(k => { Interlocked.Increment(ref built); return new PatchFaces([Vector3.Zero, Vector3.Right, Vector3.Back], []); });
        var here = At(46.2, 6.14);
        provider.Update([new CollisionPatches.Body(here, here, false)]);
        await Idle(provider);
        var wanted = CollisionPatches.Near(here, CollisionReach.RadiusM).Keys.ToHashSet();
        var ready = Take(provider);
        Assert.Equal(wanted, ready);
        Assert.Equal(wanted.Count, built);

        // a step of 100 m keeps every patch still within the release distance, and builds the new ones only
        var step = At(46.2009, 6.14);
        provider.Update([new CollisionPatches.Body(step, step, false)]);
        await Idle(provider);
        var more = Take(provider);
        Assert.DoesNotContain(more, wanted.Contains);
        var stillNear = CollisionPatches.Near(step, CollisionReach.ReleaseM);
        Assert.DoesNotContain(Released(provider), stillNear.ContainsKey);

        // 5 km away: everything released
        var far = At(46.245, 6.14);
        provider.Update([new CollisionPatches.Body(far, far, false)]);
        await Idle(provider);
        Take(provider);
        provider.Update([new CollisionPatches.Body(far, far, true)]);   // and airborne: none at all
        Assert.Equal(0, provider.Held);
    }

    private static async Task Idle(CollisionPatches p)
    {
        for (int i = 0; i < 500 && p.Busy; i++) await Task.Delay(10);
        Assert.False(p.Busy);
    }

    private static HashSet<CubeKey> Take(CollisionPatches p)
    {
        var keys = new HashSet<CubeKey>();
        while (p.TryTakeReady(out var k, out _)) keys.Add(k);
        return keys;
    }

    private static List<CubeKey> Released(CollisionPatches p)
    {
        var keys = new List<CubeKey>();
        while (p.TryTakeReleased(out var k)) keys.Add(k);
        return keys;
    }
}
