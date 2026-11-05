namespace UnitSport.Terrain.Format;

/// <summary>
/// A tree's seed from where it stands on the cube (#1507, G2 B3 PR 5b): SplitMix64 of a global lattice vertex (face, i,
/// j at the leaf lattice's level), or of a planted row's grid node, so the same tree grows whatever the leaf, the frame,
/// the batch or the run asks for it.
/// </summary>
public static class TreeSeed
{
    public static ulong Of(int face, long i, long j)
    {
        ulong z = ((ulong)(uint)face << 58) ^ ((ulong)i << 29) ^ (ulong)j;
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A seed's uniform draw in [0, 1).</summary>
    public static double Unit(ulong seed) => (seed >> 11) * (1.0 / (1UL << 53));
}
