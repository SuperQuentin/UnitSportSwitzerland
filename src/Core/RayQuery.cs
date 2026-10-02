using Godot;

namespace UnitSport.Core;

/// <summary>
/// One reused ray query for a caller that casts every frame or tick (#221): a new
/// <see cref="PhysicsRayQueryParameters3D"/> and a new exclude array per ray were garbage per frame.
/// The exclude array is handed to the query only when a different array comes in (the query keeps
/// a copy), so pass arrays that are not changed afterwards, or call <see cref="Forget"/> after
/// changing one in place.
/// </summary>
public sealed class RayQuery
{
    private readonly PhysicsRayQueryParameters3D _query = new();
    private Godot.Collections.Array<Rid>? _exclude;

    /// <summary>The same as <c>space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, mask, exclude))</c>.</summary>
    public Godot.Collections.Dictionary Cast(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, uint mask,
        Godot.Collections.Array<Rid> exclude)
    {
        _query.From = from;
        _query.To = to;
        _query.CollisionMask = mask;
        if (!ReferenceEquals(exclude, _exclude)) { _query.Exclude = exclude; _exclude = exclude; }
        return space.IntersectRay(_query);
    }

    /// <summary>The exclude array last passed was changed in place: hand it over again on the next cast.</summary>
    public void Forget() => _exclude = null;
}
