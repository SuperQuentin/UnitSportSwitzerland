using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

// Plain C#, no Godot: linked into the unit tests (the stores' earth keys, #1726).

/// <summary>
/// A level-14 leaf named as a <see cref="TileId"/> (#1510, <see cref="LeafStandIn"/>), and the earth stores' file names
/// for it (#1726, G2 D2): a store that kept one file per LV95 tile keeps one per leaf under <c>--frame earth</c>, named by
/// the leaf's <see cref="CubeKey.FileStem"/>. D5 turns <see cref="TileId"/> itself into a leaf name.
/// </summary>
public static class LeafTileName
{
    /// <summary>The leaves' level (~610 m), <see cref="LeafFrames.LeafLevel"/>.</summary>
    public const int Level = 14;

    /// <summary>The stand-in tiles' first kilometre east: far past LV95's 2,480-2,840 (and a leaf's X fits under 20,000).</summary>
    private const int BaseE = 100_000, FaceStrideE = 20_000, BaseN = 100_000;

    /// <summary>A leaf's tile name: unique, the same every run.</summary>
    public static TileId Id(CubeKey leaf)
    {
        if (leaf.Level != Level) throw new ArgumentException($"{leaf} is not a level-{Level} leaf");
        int e = BaseE + leaf.Face * FaceStrideE + leaf.X, n = BaseN + leaf.Y;
        return new TileId(e, n);
    }

    /// <summary>The leaf a stand-in tile names; false for a real LV95 tile.</summary>
    public static bool TryLeaf(TileId id, out CubeKey leaf)
    {
        leaf = default;
        var (tileE, tileN) = id;
        int e = tileE - BaseE, n = tileN - BaseN;
        if (e < 0 || n < 0) return false;
        int face = e / FaceStrideE, x = e % FaceStrideE, side = 1 << Level;
        if (face > 5 || x >= side || n >= side) return false;
        leaf = new CubeKey(face, Level, x, n);
        return true;
    }

    /// <summary>The leaf under an Earth place, as its tile name.</summary>
    public static TileId At(GlobalPos at) => Id(CubeKey.FromEcef(at.Ecef, Level));

    /// <summary>A store file's name for a tile: the leaf's <see cref="CubeKey.FileStem"/> for a stand-in, <c>E_N</c> for an LV95 tile.</summary>
    public static string FileStem(TileId id) => TryLeaf(id, out var leaf) ? leaf.FileStem : $"{id.E}_{id.N}";

    /// <summary>The tile a store file's name (<see cref="FileStem"/>, no extension) names; false for anything else.</summary>
    public static bool TryParseStem(string stem, out TileId id)
    {
        id = default;
        if (stem.StartsWith('F'))
        {
            var p = stem.Split('_');
            if (p.Length == 4 && int.TryParse(p[0].AsSpan(1), out int f) && p[1] == "L" + Level
                && int.TryParse(p[2], out int x) && int.TryParse(p[3], out int y))
            {
                var key = new CubeKey(f, Level, x, y);
                if (!key.IsValid) return false;
                id = Id(key);
                return true;
            }
            return false;
        }
        var q = stem.Split('_');
        if (q.Length != 2 || !int.TryParse(q[0], out int e) || !int.TryParse(q[1], out int n)) return false;
        id = new TileId(e, n);
        return true;
    }

    /// <summary>
    /// Leaves apart, the larger of the two axes (a ring): on one face the lattice's; across a face edge the rings walked
    /// out from <paramref name="a"/> up to <paramref name="max"/>, <see cref="int.MaxValue"/> past it. Two LV95 tiles: theirs.
    /// </summary>
    public static int Rings(TileId a, TileId b, int max)
    {
        bool la = TryLeaf(a, out var ka), lb = TryLeaf(b, out var kb);
        if (!la && !lb) return Math.Max(Math.Abs(a.E - b.E), Math.Abs(a.N - b.N));
        if (la != lb) return int.MaxValue;
        if (ka.Face == kb.Face) return Math.Max(Math.Abs(ka.X - kb.X), Math.Abs(ka.Y - kb.Y));
        var seen = new HashSet<CubeKey> { ka };
        var ring = new List<CubeKey> { ka };
        for (int r = 1; r <= max; r++)
        {
            var next = new List<CubeKey>();
            foreach (var k in ring)
                foreach (var n in k.Neighbours())
                    if (seen.Add(n)) next.Add(n);
            if (next.Contains(kb)) return r;
            ring = next;
        }
        return int.MaxValue;
    }

    /// <summary>The tiles within <paramref name="rings"/> of <paramref name="id"/>, itself first: across face edges for a leaf.</summary>
    public static IEnumerable<TileId> Around(TileId id, int rings)
    {
        if (!TryLeaf(id, out var leaf))
        {
            for (int dx = -rings; dx <= rings; dx++)
                for (int dz = rings; dz >= -rings; dz--)
                    yield return id.Neighbour(dx, dz);
            yield break;
        }
        var seen = new HashSet<CubeKey> { leaf };
        var ring = new List<CubeKey> { leaf };
        yield return id;
        for (int r = 1; r <= rings; r++)
        {
            var next = new List<CubeKey>();
            foreach (var k in ring)
                foreach (var n in k.Neighbours())
                    if (seen.Add(n)) { next.Add(n); yield return Id(n); }
            ring = next;
        }
    }
}
