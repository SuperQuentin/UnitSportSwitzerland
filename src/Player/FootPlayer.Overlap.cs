using Godot;

namespace UnitSport.Player;

/// <summary>
/// Two players inside each other (#203): spawned on the same spot, teleported onto one, or joined
/// at the default spawn together. Left to the physics, the owner's <c>MoveAndSlide</c> pushes its
/// body out of the remote copy every frame while the synchronizer puts that copy straight back,
/// and the shove added up to a player thrown 3.8 km sideways and 900 m up. Instead, while two
/// bodies overlap they ignore each other (a collision exception, on this body only) and the owner
/// walks its own body out at a calm pace; once clear they collide again as usual.
/// </summary>
public partial class FootPlayer
{
    /// <summary>How fast an overlapping body is eased out of another, m/s.</summary>
    private const float SeparateSpeed = 1.5f;

    /// <summary>Players further than this are not tested at all (a truck and its trailer fit inside), m.</summary>
    private const float SeparateRange = 30f;

    /// <summary>Players this body currently ignores because they are inside it.</summary>
    private readonly List<FootPlayer> _overlapping = new();

    private PhysicsShapeQueryParameters3D? _overlapQuery;
    private readonly List<FootPlayer> _overlapHits = new();

    /// <summary>
    /// Owner side, every physics frame before the body moves: finds the players inside this one,
    /// stops colliding with them, nudges this body apart from them, and collides with them again
    /// once clear. Each owner moves only its own body; two coincident players pick opposite ways
    /// from their names, so the pair splits without a word between them.
    /// </summary>
    private void SeparateFromPlayers(float dt)
    {
        _overlapHits.Clear();
        if (AnyPlayerNear()) CollectOverlaps();

        var push = Vector3.Zero;
        foreach (var other in _overlapHits)
        {
            if (!_overlapping.Contains(other))
            {
                // walking into someone presses capsules a little into each other, and they must
                // stay solid then: only bodies set down nearly on top of each other start this
                if (!DeeplyInside(other)) continue;
                _overlapping.Add(other);
                AddCollisionExceptionWith(other);
                GD.Print($"[spawn] player {Name} is inside {other.Name}: easing apart");
            }
            var away = (GlobalPosition - other.GlobalPosition) with { Y = 0 };
            if (away.LengthSquared() < 1e-4f)
                away = string.CompareOrdinal(Name, other.Name) > 0 ? Vector3.Right : Vector3.Left;
            push += away.Normalized();
        }

        // clear of them (or gone): solid to each other again
        for (int i = _overlapping.Count - 1; i >= 0; i--)
        {
            var other = _overlapping[i];
            if (IsInstanceValid(other) && _overlapHits.Contains(other)) continue;
            if (IsInstanceValid(other)) RemoveCollisionExceptionWith(other);
            _overlapping.RemoveAt(i);
        }

        if (push != Vector3.Zero) GlobalPosition += push.Normalized() * SeparateSpeed * dt;
    }

    /// <summary>
    /// <paramref name="cargo"/> rides in or on <paramref name="carrier"/> (a car in its hold, #418; a walker
    /// on its deck): inside it on purpose. Never eased apart: the exception the carrier's guests get
    /// (<see cref="WatchGuests"/>) would be taken back once "clear", and an A320 flew into the car in its hold.
    /// </summary>
    private static bool RidesIn(FootPlayer cargo, FootPlayer carrier) => cargo.DeckOn != "" && cargo.DeckOn == carrier.Name.ToString();

    /// <summary>Carrying capsules' centres closer than half the sum of their radii.</summary>
    private bool DeeplyInside(FootPlayer other)
    {
        float reach = 0.5f * ((_capsule?.Radius ?? BodyRadius) + (other._capsule?.Radius ?? BodyRadius));
        var apart = (GlobalPosition - other.GlobalPosition) with { Y = 0 };
        return apart.LengthSquared() < reach * reach
            && Mathf.Abs(GlobalPosition.Y - other.GlobalPosition.Y) < StandHeight;
    }

    /// <summary>Cheap pre-check, so a lone player never runs a shape query.</summary>
    private bool AnyPlayerNear()
    {
        if (_overlapping.Count > 0) return true;
        foreach (var s in PlayerSnapshot.Of(GetTree()))
            if (s.Player != this && s.Pos.DistanceSquaredTo(GlobalPosition) < SeparateRange * SeparateRange)
                return true;
        return false;
    }

    /// <summary>Every player one of this body's shapes is inside, into <see cref="_overlapHits"/>.</summary>
    private void CollectOverlaps()
    {
        var space = GetWorld3D().DirectSpaceState;
        _overlapQuery ??= new PhysicsShapeQueryParameters3D
        {
            CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { GetRid() },
        };
        _overlapQuery.CollisionMask = CollisionMask;
        foreach (var owner in GetShapeOwners())
        {
            if (ShapeOwnerGetOwner((uint)owner) is not CollisionShape3D { Disabled: false, Shape: { } shape } node) continue;
            _overlapQuery.Shape = shape;
            _overlapQuery.Transform = node.GlobalTransform;
            foreach (var hit in space.IntersectShape(_overlapQuery, 32))
                if (hit["collider"].AsGodotObject() is FootPlayer other && other != this
                    && !_overlapHits.Contains(other) && !RidesIn(this, other) && !RidesIn(other, this))
                    _overlapHits.Add(other);
        }
    }
}
