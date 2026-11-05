using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The cube water stage's walks across face edges (#1552; <see cref="CubeWaterStage"/>, docs/notes/tools/cube-water.md):
/// the leaf vertices phase B joins with another face's, and the leaf vertex under a window cell past its face's edge.
/// Pure, over TerrainFormat.
/// </summary>
public static class CubeWaterLattice
{
    /// <summary>
    /// The boundary vertices of a leaf (2^bits cells a side) that another face's leaves hold too: the vertex's index
    /// in the leaf (j · side + i), the other leaf and the index in it (<see cref="LeafSeam.Holders"/>). Same-face
    /// holders are left out: phase B joins those along east and north edges.
    /// </summary>
    public static IEnumerable<(int Mine, CubeKey Other, int Theirs)> AcrossFaces(CubeKey leaf)
    {
        int bits = CubeKey.LatticeBits(leaf.Level), n = 1 << bits, side = n + 1;
        // only a leaf on its face's edge has any
        bool edge = leaf.X == 0 || leaf.Y == 0 || leaf.X == leaf.Side - 1 || leaf.Y == leaf.Side - 1;
        if (!edge) yield break;
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                if (i != 0 && j != 0 && i != n && j != n) { i = n - 1; continue; }
                foreach (var (k, a, b) in LeafSeam.Holders(leaf, 1, i, j))
                    if (k.Face != leaf.Face)
                        yield return (j * side + i, k, b * side + a);
            }
    }

    /// <summary>
    /// The leaf at <paramref name="level"/> (2^bits cells a side) and the vertex index in it of the face lattice vertex
    /// (gx, gy) of <paramref name="face"/> (at level + bits), past the face's edge: folded onto the next face
    /// (<see cref="CubeSphere.Fold"/>), so a window across an edge reads the other face's leaves turned and mirrored as
    /// the faces meet. Null past a cube corner, where the window is not a grid (those cells stay unknown). A vertex on
    /// a leaf edge is taken from the leaf east or north of it, but for the face's last.
    /// </summary>
    public static (CubeKey Leaf, int Index)? Past(int face, int level, int bits, long gx, long gy)
    {
        if (CubeSphere.Fold(face, level + bits, gx, gy) is not { } p) return null;
        int n = 1 << bits, last = (1 << level) - 1;
        int x = (int)Math.Min(p.I >> bits, last), y = (int)Math.Min(p.J >> bits, last);
        int i = (int)(p.I - ((long)x << bits)), j = (int)(p.J - ((long)y << bits));
        return (new CubeKey(p.Face, level, x, y), j * (n + 1) + i);
    }
}
