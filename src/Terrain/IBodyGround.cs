using Godot;

namespace UnitSport.Terrain;

/// <summary>
/// The ground of the anchor's body when that is not the Earth (#1850, docs/plans/planets.md section 4): the Earth's
/// world (<see cref="Earth.EarthWorld"/>) asks it for heights and collision there, so the walker, its drop onto the
/// ground and its rescue from the void work on any body through the one <see cref="IWorldGround"/>. The Moon's is
/// <c>World/Bodies/BodyGround</c>, its near square.
/// </summary>
public interface IBodyGround
{
    /// <summary>The ground's world Y under a world point, where it is known (the near square covers it).</summary>
    bool TryGetHeight(Vector3 worldPos, out float height);

    /// <summary>The ground there is solid already.</summary>
    bool HasCollisionAt(Vector3 worldPos);
}
