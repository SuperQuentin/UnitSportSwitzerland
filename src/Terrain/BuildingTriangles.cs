using Godot;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Godot-side reads of a <see cref="Building"/>'s triangle soup (9 floats per triangle); the
/// format project has no Godot types (<c>docs/notes/terrain/building-triangles.md</c>).
/// </summary>
public static class BuildingTriangles
{
    /// <summary>Faces whose |normal.y| is at least this are roof; steeper ones are walls.</summary>
    public const float RoofNormalY = 0.45f;

    /// <summary>The three corners of triangle <paramref name="t"/>, in tile-local metres.</summary>
    public static (Vector3 A, Vector3 B, Vector3 C) Tri(this Building b, int t)
    {
        var f = b.Triangles;
        int o = t * 9;
        return (new Vector3(f[o], f[o + 1], f[o + 2]),
                new Vector3(f[o + 3], f[o + 4], f[o + 5]),
                new Vector3(f[o + 6], f[o + 7], f[o + 8]));
    }
}
