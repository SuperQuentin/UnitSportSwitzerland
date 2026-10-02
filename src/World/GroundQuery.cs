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
    /// standing in a parked bus by its front axle is not the road.
    /// </summary>
    public static float Under(CollisionObject3D self, RayQuery ray, Godot.Collections.Array<Rid> exclude, Vector3 p,
        ChunkManager? terrain, bool pastPlayers = false)
    {
        var space = self.GetWorld3D().DirectSpaceState;
        var from = p + Vector3.Up * 3f;
        var to = p + Vector3.Down * 6f;
        uint mask = self.CollisionMask & ~TreeColliders.Layer;
        var hit = ray.Cast(space, from, to, mask, exclude);
        if (pastPlayers && hit.Count > 0 && hit["collider"].AsGodotObject() is FootPlayer)
        {
            // rare: an array of its own, changed in place, so the query is told each time
            var past = exclude.Duplicate();
            for (int tries = 0; tries < 4 && hit.Count > 0 && hit["collider"].AsGodotObject() is FootPlayer; tries++)
            {
                past.Add(hit["rid"].AsRid());
                ray.Forget();
                hit = ray.Cast(space, from, to, mask, past);
            }
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is FootPlayer) hit.Clear();
        }
        if (hit.Count > 0) return hit["position"].AsVector3().Y;
        return terrain != null && terrain.TryGetHeight(p, out float g) ? g : p.Y;
    }
}
