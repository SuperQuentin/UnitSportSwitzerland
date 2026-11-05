using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Rewrite;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// Records a producer made in a tile of its own coordinates (tile-local x east of <c>west</c>, z south of <c>north</c>,
/// y the height above sea level: an LV95 tile's frame, or a course tile's) moved into a leaf's anchor frame (#1509,
/// G2 B5): road records through <see cref="LeafRecords.Map"/>, building triangles and trees point by point. Positions
/// rigorously (<see cref="SourceOnLeaf.Local"/>), headings by the source's step and the leaf's Jacobian.
/// </summary>
public sealed class SourceTileToLeaf(SourceOnLeaf on, double west, double north) : LeafRecords.IMap
{
    /// <summary>A tile-local point's source position.</summary>
    public (double A, double B) SourceOf(double x, double z) => (west + x, north - z);

    /// <summary>Whether a tile-local point lies in the leaf.</summary>
    public bool Holds(double x, double z) => on.Holds(west + x, north - z);

    public (float X, float Y, float Z) Point(float x, float y, float z)
    {
        var (lx, ly, lz) = on.Local(west + x, north - z, y);
        return ((float)lx, (float)ly, (float)lz);
    }

    public float Godot(float heading, float x, float y, float z)
    {
        var (dx, dz) = on.DirectionToLocal(west + x, north - z, -System.Math.Sin(heading), System.Math.Cos(heading), y);
        return (float)System.Math.Atan2(-dx, -dz);
    }

    public float Math(float angle, float x, float y, float z)
    {
        var (dx, dz) = on.DirectionToLocal(west + x, north - z, System.Math.Cos(angle), System.Math.Sin(angle), y);
        return (float)System.Math.Atan2(-dz, dx);
    }

    /// <summary>A building's triangles into the leaf (a copy), its height range taken again.</summary>
    public Building Move(Building b)
    {
        var t = (float[])b.Triangles.Clone();
        float min = float.MaxValue, max = float.MinValue;
        for (int k = 0; k + 2 < t.Length; k += 3)
        {
            (t[k], t[k + 1], t[k + 2]) = Point(t[k], t[k + 1], t[k + 2]);
            min = MathF.Min(min, t[k + 1]);
            max = MathF.Max(max, t[k + 1]);
        }
        return new Building { Kind = b.Kind, Egid = b.Egid, YearBuilt = b.YearBuilt, Floors = b.Floors, MinY = min, MaxY = max, Triangles = t };
    }

    /// <summary>A building's middle in the tile (its triangles' box), which leaf files it.</summary>
    public static (float X, float Z) Middle(Building b)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        var t = b.Triangles;
        for (int k = 0; k + 2 < t.Length; k += 3)
        {
            minX = MathF.Min(minX, t[k]); maxX = MathF.Max(maxX, t[k]);
            minZ = MathF.Min(minZ, t[k + 2]); maxZ = MathF.Max(maxZ, t[k + 2]);
        }
        return ((minX + maxX) / 2, (minZ + maxZ) / 2);
    }

    public TreeInstance Move(TreeInstance tree)
    {
        var (x, y, z) = Point(tree.X, tree.Y, tree.Z);
        return tree with { X = x, Y = y, Z = z };
    }
}
