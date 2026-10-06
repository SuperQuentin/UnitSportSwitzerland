using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.World;

/// <summary>The ground's height under a point, for a vehicle standing its sections on it (#221: one copy, not two).</summary>
public static class GroundQuery
{
    /// <summary>
    /// Whatever <paramref name="self"/> collides with (trees aside) on a ray from 3 m above
    /// <paramref name="p"/> to 6 m below it, else the terrain's height, else <c>p.Y</c>. With
    /// <paramref name="pastPlayers"/>, a player on the ray is looked past (up to 4 of them): one
    /// standing in a parked bus by its front axle is not the road. A vehicle parked in a hold (a boat
    /// on its trailer, #463) is always looked past: cargo is never the ground of what carries it. Its
    /// carrier excludes it once it is hooked; this is for the moments before (a trailer just dropped
    /// stood itself on its own boat's hull, and the boat, lifted out of the cradle, never hooked).
    /// With <paramref name="pastVehicles"/> (a driven truck's own axles, #494) players and every other
    /// vehicle are looked past too: what the train stands on is the road, never a dropped plough or a
    /// parked car under an axle point — read as ground it pitched the cab 20° and the hull, dug into
    /// the road, threw the tractor up off its wheels.
    /// </summary>
    public static float Under(CollisionObject3D self, RayQuery ray, Godot.Collections.Array<Rid> exclude, Vector3 p,
        ChunkManager? terrain, bool pastPlayers = false, bool pastVehicles = false) =>
        Under(self.GetWorld3D(), self.CollisionMask, ray, exclude, p, terrain, pastPlayers, out _, pastVehicles);

    /// <summary>
    /// The same from a world and a collision mask, for something with no body of its own (a dormant
    /// train, #560). <paramref name="solid"/> is false where nothing was hit and the terrain's height
    /// (or <c>p.Y</c>) is the answer: the collision there has not been built.
    ///
    /// <para>
    /// A dormant vehicle's box is never ground (#560): a parked train stood its axles on its own
    /// dormant copy, still in the physics space the frame it woke, a metre up, and on a neighbour's.
    /// </para>
    /// </summary>
    public static float Under(World3D world, uint collisionMask, RayQuery ray, Godot.Collections.Array<Rid> exclude, Vector3 p,
        ChunkManager? terrain, bool pastPlayers, out bool solid, bool pastVehicles = false)
    {
        var space = world.DirectSpaceState;
        var from = p + Vector3.Up * 3f;
        var to = p + Vector3.Down * 6f;
        uint mask = collisionMask & ~TreeColliders.Layer;
        var hit = ray.Cast(space, from, to, mask, exclude);
        if (hit.Count > 0 && Past(hit["collider"].AsGodotObject(), pastPlayers, pastVehicles))
        {
            // rare: an array of its own, changed in place, so the query is told each time
            var past = exclude.Duplicate();
            for (int tries = 0; tries < 4 && hit.Count > 0 && Past(hit["collider"].AsGodotObject(), pastPlayers, pastVehicles); tries++)
            {
                past.Add(hit["rid"].AsRid());
                ray.Forget();
                hit = ray.Cast(space, from, to, mask, past);
            }
            if (hit.Count > 0 && Past(hit["collider"].AsGodotObject(), pastPlayers, pastVehicles)) hit.Clear();
        }
        solid = hit.Count > 0;
        if (solid) return hit["position"].AsVector3().Y;
        return terrain != null && terrain.TryGetHeight(p, out float g) ? g : p.Y;
    }

    private static bool Past(GodotObject? hit, bool players, bool vehicles) =>
        hit is Vehicles.DormantBody
        || (players || vehicles) && hit is FootPlayer
        || hit is Vehicles.VehicleBody { InHold: true }
        || vehicles && hit is Vehicles.VehicleBody;
}
