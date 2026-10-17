using Godot;

namespace UnitSport.Bots;

/// <summary>
/// The rays bots cast, many a second each (sight, walls, crates): one query object and one exclude
/// list reused on the main thread, so a ray allocates only its answer (#837: a fresh query and list
/// per ray ran the server's garbage collector ten times a second with eight bots).
/// </summary>
public static class BotRays
{
    private static readonly PhysicsRayQueryParameters3D Query = new() { CollisionMask = uint.MaxValue };
    private static readonly Godot.Collections.Array<Rid> Exclude = new();

    /// <summary>What the ray from <paramref name="from"/> to <paramref name="to"/> hits first, leaving out <paramref name="self"/>; empty when nothing.</summary>
    public static Godot.Collections.Dictionary Cast(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, Rid self)
    {
        Query.From = from;
        Query.To = to;
        if (Exclude.Count != 1 || Exclude[0] != self)
        {
            Exclude.Clear();
            Exclude.Add(self);
            Query.Exclude = Exclude;
        }
        return space.IntersectRay(Query);
    }

    /// <summary>Nothing solid between the two points (other figures hide nobody).</summary>
    public static bool Clear(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, Rid self)
    {
        var hit = Cast(space, from, to, self);
        return hit.Count == 0 || hit["collider"].AsGodotObject() is Player.FootPlayer;
    }
}
