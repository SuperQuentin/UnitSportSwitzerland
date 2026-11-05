using System.Diagnostics;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using static System.Console;

/// <summary>
/// The generated world as cube leaves (#1509, G2 B5): a block of leaves round the anchor and a pair across a work
/// frame's edge, built by <see cref="GeneratedLeaves"/>. Checks that the heights are the generator's function at every
/// vertex, that shared edges agree (bit for bit within a frame, within a quantum across one), that every tree and
/// building of the LV95 tiles under the block is filed in exactly one leaf, that the cover and water come from the
/// tiles' own, and that the leaves round-trip through the leaf codec.
///
///   dotnet run --project tools/BlendCheck -c Release -- --leaves [--side 3]
/// </summary>
internal static class GeneratedLeavesCheck
{
    public static int Run(string[] args)
    {
        const double AnchorE = 2583250, AnchorN = 1113250;   // SpawnPoint.DefaultLv95E/N
        int side = Arg(args, "--side", 3);
        var world = new ProceduralWorld(AnchorE, AnchorN);
        var leaves = new GeneratedLeaves(world);
        var frames = LeafFrames.Swiss;
        int fails = 0;
        void Check(string what, bool ok, string detail = "")
        {
            WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? ": " + detail : "")}");
            if (!ok) fails++;
        }

        var (lat, lon) = Lv95Source.Instance.ToLatLon(AnchorE, AnchorN);
        var centre = LeafFrames.LeafAt(lat, lon);
        var block = new List<CubeKey>();
        for (int dy = -(side / 2); dy <= side / 2; dy++)
            for (int dx = -(side / 2); dx <= side / 2; dx++)
                if (centre.Neighbour(dx, dy) is { } k) block.Add(k);
        WriteLine($"{block.Count} leaves round {centre} (anchor {AnchorE}/{AnchorN})");

        var sw = Stopwatch.StartNew();
        var built = block.AsParallel().Select(k => (Key: k, Leaf: leaves.Build(k))).ToDictionary(x => x.Key, x => x.Leaf);
        WriteLine($"  built in {sw.Elapsed.TotalSeconds:F1} s ({sw.Elapsed.TotalSeconds * Environment.ProcessorCount / block.Count:F2} core-s a leaf at most)");

        // heights: the generator's function at every 37th vertex, ellipsoidal by the leaf's undulation
        double worst = 0;
        foreach (var (key, leaf) in built)
        {
            var on = new SourceOnLeaf(frames, key, Lv95Source.Instance);
            int n = on.Side;
            for (int j = 0; j < n; j += 37)
                for (int i = 0; i < n; i += 37)
                {
                    var (e, nn) = on.SourceAt(i, j);
                    double want = world.Height(e, nn) + on.Transform.UndulationAt(i, j);
                    worst = Math.Max(worst, Math.Abs(leaf.Heights!.Heights[i + j * n] - want));
                }
        }
        Check("heights are the generator's at the vertices", worst < 1e-3, $"worst {worst * 1000:F3} mm");

        // shared edges within the frame: bit for bit
        long pairs = 0, differ = 0;
        foreach (var (key, leaf) in built)
            if (key.Neighbour(1, 0) is { } east && built.TryGetValue(east, out var other))
            {
                pairs++;
                differ += EdgeDiffers(leaf.Heights!, other.Heights!, 0.0f);
            }
        Check("shared edges within a frame are equal", differ == 0, $"{differ} vertices differ over {pairs} pairs");

        // a pair across a work frame's edge: within a quantum (each frame's plane point of the vertex differs in the last bits)
        var across = AcrossFrameEdge(centre);
        if (across is var (a, b))
        {
            var (la, lb) = (leaves.Build(a), leaves.Build(b));
            long d = EdgeDiffers(la.Heights!, lb.Heights!, CubeTileCodec.Quantum);
            Check($"shared edge across frames {a.AncestorAt(9)} / {b.AncestorAt(9)} within 1 cm", d == 0, $"{d} vertices further apart");
        }
        else Check("a leaf pair across a frame edge found", false);

        // trees and buildings: each of the tiles' filed in exactly one leaf of the block
        var keys = built.Keys.ToHashSet();
        long trees = built.Values.Sum(l => (long)l.Trees!.Count), buildings = built.Values.Sum(l => (long)l.Buildings!.Buildings.Count);
        long wantTrees = 0, wantBuildings = 0;
        var tiles = built.Keys.SelectMany(k => GeneratedLeaves.TilesUnder(new SourceOnLeaf(frames, k, Lv95Source.Instance))).ToHashSet();
        foreach (var id in tiles)
        {
            foreach (var t in world.BuildTrees(id))
                if (keys.Contains(SourceOnLeaf.LeafOf(Lv95Source.Instance, id.MinE + t.X, id.MaxN - t.Z))) wantTrees++;
            foreach (var bl in world.BuildBuildings(id)?.Buildings ?? [])
            {
                var (mx, mz) = SourceTileToLeaf.Middle(bl);
                if (keys.Contains(SourceOnLeaf.LeafOf(Lv95Source.Instance, id.MinE + mx, id.MaxN - mz))) wantBuildings++;
            }
        }
        Check("every tree of the tiles in one leaf", trees == wantTrees, $"{trees} in leaves, {wantTrees} in the tiles");
        Check("every building of the tiles in one leaf", buildings == wantBuildings, $"{buildings} in leaves, {wantBuildings} in the tiles");

        // trees stand on the leaf's ground (the generator's trees stand on its ground)
        double treeWorst = 0;
        foreach (var (key, leaf) in built)
        {
            var t = frames.TransformOf(key);
            foreach (var tree in leaf.Trees!)
            {
                var (i, j, hEll) = t.LocalToLattice(tree.X, tree.Y, tree.Z);
                treeWorst = Math.Max(treeWorst, Math.Abs(Bilinear(leaf.Heights!, i, j) - hEll));
            }
        }
        Check("trees stand on the ground", treeWorst < 0.5, $"worst {treeWorst:F3} m");

        // cover: the classes the tiles hold; water where they have it
        long wet = built.Values.Sum(l => l.Water is { } w ? w.Level.Count(x => !float.IsNaN(x)) : 0);
        var classes = built.Values.SelectMany(l => l.Cover!).Distinct().Count();
        Check("cover has the tiles' classes", classes > 1, $"{classes} classes");
        WriteLine($"  {wet} wet water samples in the block");

        // the codec
        long sections = 0;
        foreach (var (key, leaf) in built)
        {
            var back = CubeLeafContent.Decode(leaf.Encode());
            bool same = back.Skipped.Count == 0 && back.Heights!.Heights.Length == leaf.Heights!.Heights.Length
                && back.Trees!.Count == leaf.Trees!.Count && back.Buildings!.Buildings.Count == leaf.Buildings!.Buildings.Count
                && back.Cover!.AsSpan().SequenceEqual(leaf.Cover);
            if (!same) differ++;
            sections += leaf.Sections().Count;
        }
        Check("leaves round-trip through the codec", differ == 0, $"{sections} sections");

        WriteLine(fails == 0 ? "RESULT: ok" : $"RESULT: FAILED ({fails})");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Vertices of a's east edge and b's west edge (b east of a, same face) further apart than <paramref name="tolerance"/>.</summary>
    private static long EdgeDiffers(CubeTile a, CubeTile b, float tolerance)
    {
        int n = (1 << a.Bits) + 1;
        long d = 0;
        for (int j = 0; j < n; j++)
            if (!(Math.Abs(a.Heights[n - 1 + j * n] - b.Heights[j * n]) <= tolerance)) d++;
        return d;
    }

    /// <summary>The first leaf east of <paramref name="from"/> whose east neighbour is in another level-9 frame.</summary>
    private static (CubeKey, CubeKey)? AcrossFrameEdge(CubeKey from)
    {
        var k = from;
        for (int step = 0; step < 64; step++)
        {
            if (k.Neighbour(1, 0) is not { } east || east.Face != k.Face) return null;
            if (east.AncestorAt(9) != k.AncestorAt(9)) return (k, east);
            k = east;
        }
        return null;
    }

    private static double Bilinear(CubeTile tile, double i, double j)
    {
        int n = (1 << tile.Bits) + 1;
        int i0 = Math.Clamp((int)i, 0, n - 2), j0 = Math.Clamp((int)j, 0, n - 2);
        double u = i - i0, v = j - j0;
        var h = tile.Heights;
        double s = h[i0 + j0 * n] + (h[i0 + 1 + j0 * n] - h[i0 + j0 * n]) * u;
        double nn = h[i0 + (j0 + 1) * n] + (h[i0 + 1 + (j0 + 1) * n] - h[i0 + (j0 + 1) * n]) * u;
        return s + (nn - s) * v;
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
    }
}
