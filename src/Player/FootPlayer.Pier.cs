using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Getting out of a boat at a pier or a jetty (#377): onto its deck instead of over the side into the
/// water. See <c>docs/notes/world/landings.md</c>.
/// </summary>
public partial class FootPlayer
{
    private readonly RayQuery _pierRay = new();
    private Godot.Collections.Array<Rid>? _pierExclude;

    /// <summary>How far past the boat's side a deck is looked for, m (a jetty is 2.2 m wide).</summary>
    private const float PierReach = 2.6f;

    /// <summary>
    /// A deck or dry ground beside the boat just left, either side of <paramref name="at"/> (right
    /// first), standing clear: the player is put there and true is returned. Not the lake bed, not
    /// a vehicle. False: nothing to stand on, the water it is.
    /// </summary>
    private bool StepOntoPier(Vector3 at, Vector3 right, float side)
    {
        var space = GetWorld3D().DirectSpaceState;
        _pierExclude ??= new Godot.Collections.Array<Rid> { GetRid() };
        _standProbe ??= new CapsuleShape3D { Radius = BodyRadius - 0.03f, Height = StandHeight };
        for (float d = side - 0.4f; d <= side + PierReach; d += 0.4f)
            foreach (float sign in new[] { 1f, -1f })
            {
                var p = at + right * (sign * d);
                var hit = _pierRay.Cast(space, p + Vector3.Up * 3f, p + Vector3.Down * 4f, CollisionMask, _pierExclude);
                if (hit.Count == 0 || hit["collider"].As<Node>() is not StaticBody3D || hit["collider"].As<Node>() is AnimatableBody3D) continue;
                var spot = hit["position"].AsVector3();
                if (hit["normal"].AsVector3().Y < 0.7f) continue;
                // over the water, only a deck standing out of it counts: not the bed under it
                if (Terrain != null && Terrain.TryGetWaterLevel(spot, out float level) && spot.Y < level + 0.1f) continue;
                var query = new PhysicsShapeQueryParameters3D
                {
                    Shape = _standProbe,
                    Transform = new Transform3D(Basis.Identity, spot + Vector3.Up * (StandHeight * 0.5f + 0.12f)),
                    CollisionMask = CollisionMask,
                    Exclude = _pierExclude,
                };
                if (space.IntersectShape(query, 1).Count > 0) continue;
                GlobalPosition = spot + Vector3.Up * 0.05f;
                Velocity = Vector3.Zero;
                GD.Print($"[boat] {Name} steps ashore onto {hit["collider"].As<Node>().Name} {MathX.FlatDistance(at, spot):F1} m from the helm");
                return true;
            }
        return false;
    }
}
