using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Biomes;

/// <summary>
/// A generated fill leaf in its biome (#1809, <c>docs/plans/fill-biomes.md</c> "Biomes"): the generator's cover, trees,
/// farms and still water changed by the biome at each place (<see cref="FillBiome"/>). It runs on the finished leaf, on
/// the pyramid, so the altitude is the real ground's. A leaf all inside the Swiss guard is left as it is, to the bit.
/// Every decision is a function of the place on its face's global lattice: neighbouring leaves agree.
/// </summary>
public static class BiomePass
{
    /// <summary>Control points every this many lattice vertices: latitude, longitude, undulation and the guard, bilinear between.</summary>
    public const int ControlStep = 16;

    /// <summary>The biome is decided every this many vertices (~2.4 m at level 14), as the leaves' water is.</summary>
    public const int Step = 2;

    /// <summary>A farm's cell, lattice vertices (~300 m at level 14): its buildings and their paving kept or dropped together.</summary>
    public const int FarmCellShift = 8;

    /// <summary>How many of a leaf's sampled points took each biome, and the step that decided them.</summary>
    public sealed class Stats
    {
        public readonly int[] ByBiome = new int[Enum.GetValues<Biome>().Length];
        public readonly int[] BySource = new int[Enum.GetValues<BiomeSource>().Length];
        public int Samples;
        public int TreesBefore, TreesAfter, BuildingsBefore, BuildingsAfter;
        public bool Untouched;

        public Biome Main
        {
            get
            {
                int best = 0;
                for (int k = 1; k < ByBiome.Length; k++) if (ByBiome[k] > ByBiome[best]) best = k;
                return (Biome)best;
            }
        }
    }

    /// <summary>
    /// The leaf in its biomes. <paramref name="insideMapM"/> is how far a latitude and longitude lie inside the
    /// generator's own map (the guard, <see cref="FillBiome"/>); it is read at the control points only.
    /// </summary>
    public static Stats Apply(CubeLeafContent content, LeafTransform t, FillBiome biomes, Func<double, double, double> insideMapM)
    {
        var stats = new Stats();
        stats.TreesBefore = stats.TreesAfter = content.Trees?.Count ?? 0;
        stats.BuildingsBefore = stats.BuildingsAfter = content.Buildings?.Buildings.Count ?? 0;
        var leaf = t.Key;
        int cells = t.Cells, n = cells + 1, face = leaf.Face, level = leaf.Level + t.Bits;
        long gi0 = (long)leaf.X << t.Bits, gj0 = (long)leaf.Y << t.Bits;

        // control points: latitude, longitude, undulation, the guard's distance
        int cs = cells / ControlStep + 1;
        var lat = new double[cs * cs];
        var lon = new double[cs * cs];
        var und = new double[cs * cs];
        var inside = new double[cs * cs];
        bool allGuarded = true;
        for (int b = 0; b < cs; b++)
            for (int a = 0; a < cs; a++)
            {
                int k = b * cs + a;
                var g = CubeSphere.ToGeodetic(face, CubeSphere.Lattice(level, gi0 + a * ControlStep), CubeSphere.Lattice(level, gj0 + b * ControlStep));
                lat[k] = g.LatDeg;
                lon[k] = g.LonDeg;
                und[k] = t.UndulationAt(a * ControlStep, b * ControlStep);
                inside[k] = insideMapM(g.LatDeg, g.LonDeg);
                if (inside[k] < FillBiome.GuardBandM) allGuarded = false;
            }
        if (allGuarded)
        {
            stats.Untouched = true;
            return stats;
        }
        // longitudes across the antimeridian: unwrapped round the first control point, so bilinear stays on the short side
        for (int k = 1; k < lon.Length; k++)
        {
            if (lon[k] - lon[0] > 180) lon[k] -= 360;
            else if (lon[k] - lon[0] < -180) lon[k] += 360;
        }

        var heights = content.Heights?.Heights;
        int side = cells / Step + 1;
        var biome = new Biome[side * side];
        var source = new BiomeSource[side * side];
        Parallel.For(0, side, y =>
        {
            for (int x = 0; x < side; x++)
            {
                int i = x * Step, j = y * Step;
                double fx = (double)i / ControlStep, fy = (double)j / ControlStep;
                int a = Math.Min((int)fx, cs - 2), b = Math.Min((int)fy, cs - 2);
                double u = fx - a, v = fy - b;
                int c = b * cs + a;
                double la = Bilerp(lat, c, cs, u, v), lo = Bilerp(lon, c, cs, u, v);
                if (lo > 180) lo -= 360;
                else if (lo < -180) lo += 360;
                double h = heights != null && !float.IsNaN(heights[j * n + i]) ? heights[j * n + i] : und[c];
                double alt = h - Bilerp(und, c, cs, u, v);
                biome[y * side + x] = biomes.At(la, lo, alt, Bilerp(inside, c, cs, u, v), out source[y * side + x]);
            }
        });
        foreach (var bm in biome) stats.ByBiome[(int)bm]++;
        foreach (var src in source) stats.BySource[(int)src]++;
        stats.Samples = biome.Length;
        if (stats.ByBiome[(int)Biome.Swiss] == biome.Length) return stats;

        Biome At(int i, int j) => biome[Math.Clamp((j + Step / 2) / Step, 0, side - 1) * side + Math.Clamp((i + Step / 2) / Step, 0, side - 1)];
        bool FarmKept(Biome b, int i, int j) => FillBiome.Hash01((gi0 + i) >> FarmCellShift, (gj0 + j) >> FarmCellShift, 1809 + face) < FarmShare(b);

        if (content.Cover is { } cover)
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int k = j * n + i;
                    if (k >= cover.Length) break;
                    var b = At(i, j);
                    if (b == Biome.Swiss) continue;
                    var cls = (CoverClass)cover[k];
                    if (IsBuilt(cls) && FarmKept(b, i, j)) continue;
                    var to = Ground(b, cls);
                    // the forest biomes' canopy over the generator's open ground (it has no forest past its map)
                    if (to == OpenGround(b) && Canopy(b, gi0 + i, gj0 + j)) to = CoverClass.Forest;
                    cover[k] = (byte)to;
                }

        if (content.Water is { } water)
        {
            bool wet = false;
            for (int r = 0; r < water.Side; r++)
                for (int c = 0; c < water.Side; c++)
                {
                    int s = r * water.Side + c;
                    if (float.IsNaN(water.Level[s])) continue;
                    if (At(c * CubeWater.Stride, r * CubeWater.Stride) is Biome.Desert)
                    {
                        // the generator's rivers dry: their beds stay, wadis
                        water.Level[s] = float.NaN;
                        water.Fetch[s] = 0;
                        continue;
                    }
                    wet = true;
                }
            if (!wet) content.Water = null;
        }

        (int I, int J) Nearest(float x, float y, float z)
        {
            var (fi, fj, _) = t.LocalToLattice(x, y, z);
            return (Math.Clamp((int)Math.Round(fi), 0, n - 1), Math.Clamp((int)Math.Round(fj), 0, n - 1));
        }

        if (content.Trees is { } trees)
        {
            var kept = new List<TreeInstance>(trees.Count);
            foreach (var tree in trees)
            {
                var (i, j) = Nearest(tree.X, tree.Y, tree.Z);
                var b = At(i, j);
                if (b == Biome.Swiss)
                {
                    kept.Add(tree);
                    continue;
                }
                double r = FillBiome.Hash01(gi0 + i, gj0 + j, 31 + face);
                if (r >= TreeShare(b)) continue;
                kept.Add(Tree(b, tree, FillBiome.Hash01(gi0 + i, gj0 + j, 37 + face)));
            }
            if (content.Cover is { } cv && heights != null) Scatter(kept, cv, heights, n, t, At, gi0, gj0, face);
            content.Trees = kept;
            stats.TreesAfter = kept.Count;
        }

        if (content.Buildings is { } bt)
        {
            var kept = new List<Building>(bt.Buildings.Count);
            foreach (var bd in bt.Buildings)
            {
                var (mx, mz) = SourceTileToLeaf.Middle(bd);
                var (i, j) = Nearest(mx, bd.MinY, mz);
                var b = At(i, j);
                if (b == Biome.Swiss || FarmKept(b, i, j)) kept.Add(bd);
            }
            content.Buildings = new BuildingTile { Id = bt.Id, Buildings = kept };
            stats.BuildingsAfter = kept.Count;
        }
        return stats;
    }

    /// <summary>Forest trees scattered one a block of this many vertices a side at most (~4.8 m), on the global lattice.</summary>
    public const int ScatterBlock = 4;

    /// <summary>The share of blocks under a forest biome's canopy that get a tree (~150 a hectare at level 14).</summary>
    public const double ScatterShare = 0.35;

    /// <summary>The forest biomes' canopy patches: cells of this many lattice vertices (~480 m) of smooth noise.</summary>
    public const double CanopyCell = 400;

    /// <summary>The ground the generator's open land becomes in a biome (<see cref="Ground"/> of <see cref="CoverClass.Open"/>).</summary>
    private static CoverClass OpenGround(Biome b) => Ground(b, CoverClass.Open);

    /// <summary>Whether a forest biome's canopy covers a vertex of the face's global lattice (most of the wet tropics, half the taiga).</summary>
    public static bool Canopy(Biome b, long gi, long gj) => b switch
    {
        Biome.TropicalForest => FillBiome.ValueNoise(gi / CanopyCell, gj / CanopyCell, 41) > 0.2,
        Biome.Boreal => FillBiome.ValueNoise(gi / CanopyCell, gj / CanopyCell, 43) > 0.45,
        _ => false,
    };

    /// <summary>
    /// Trees on the forest biomes' canopy (the generator has none past its map): one block of <see cref="ScatterBlock"/>
    /// vertices on the global lattice at a time, a tree at a hashed vertex in it, so a block is one leaf's and the
    /// leaves agree. Broadleaf in the tropics, conifers in the taiga.
    /// </summary>
    private static void Scatter(List<TreeInstance> trees, byte[] cover, float[] heights, int n, LeafTransform t, Func<int, int, Biome> at,
        long gi0, long gj0, int face)
    {
        int cells = n - 1;
        for (int j0 = 0; j0 < cells; j0 += ScatterBlock)
            for (int i0 = 0; i0 < cells; i0 += ScatterBlock)
            {
                long bi = (gi0 + i0) / ScatterBlock, bj = (gj0 + j0) / ScatterBlock;
                if (FillBiome.Hash01(bi, bj, 51 + face) >= ScatterShare) continue;
                int i = i0 + (int)(FillBiome.Hash01(bi, bj, 53 + face) * ScatterBlock), j = j0 + (int)(FillBiome.Hash01(bi, bj, 59 + face) * ScatterBlock);
                int k = j * n + i;
                var b = at(i, j);
                if (b is not (Biome.TropicalForest or Biome.Boreal) || cover[k] != (byte)CoverClass.Forest || float.IsNaN(heights[k])) continue;
                double r = FillBiome.Hash01(bi, bj, 61 + face);
                var (x, y, z) = t.LatticeToLocal(i, j, heights[k]);
                trees.Add(b == Biome.TropicalForest
                    ? new TreeInstance((float)x, (float)y, (float)z, 18f + (float)r * 14f, KindSolitary)
                    : new TreeInstance((float)x, (float)y, (float)z, 10f + (float)r * 10f, KindConifer));
            }
    }

    private static double Bilerp(double[] f, int c, int side, double u, double v)
    {
        double s = f[c] + (f[c + 1] - f[c]) * u, n = f[c + side] + (f[c + side + 1] - f[c + side]) * u;
        return s + (n - s) * v;
    }

    /// <summary>Rock, scree, boulders, ice and water: the generator's in every biome but ice (and sand under the sea).</summary>
    private static bool IsMineral(CoverClass c) => c is CoverClass.Rock or CoverClass.LooseRock or CoverClass.Scree or CoverClass.LooseScree
        or CoverClass.Boulders or CoverClass.Glacier or CoverClass.Snowfield or CoverClass.Water;

    /// <summary>What a farm brings with it: paving, parking, gardens.</summary>
    public static bool IsBuilt(CoverClass c) => c is CoverClass.ParkingPublic or CoverClass.ParkingPrivate or CoverClass.ParkingPaved
        or CoverClass.RestArea or CoverClass.PavedArea or CoverClass.TownPaving or CoverClass.TunnelRoof or CoverClass.Allotment
        or CoverClass.Institution or CoverClass.Industrial;

    private static bool IsWooded(CoverClass c) => c is CoverClass.Forest or CoverClass.OpenForest or CoverClass.Woodland or CoverClass.Shrub
        or CoverClass.Clearcut or CoverClass.Orchard or CoverClass.Nursery;

    /// <summary>The generator's cover class in a biome (the plan's "Ground" and "Generator's forest" columns).</summary>
    public static CoverClass Ground(Biome b, CoverClass c)
    {
        switch (b)
        {
            case Biome.Swiss or Biome.TemperateGrass or Biome.TemperateForest or Biome.Cropland or Biome.Urban:
                return c;
            case Biome.Sea:
                return c == CoverClass.Water ? c : CoverClass.Sand;
            case Biome.Ice:
                return c is CoverClass.Rock or CoverClass.LooseRock or CoverClass.Boulders or CoverClass.Water ? c : CoverClass.Glacier;
        }
        if (IsMineral(c)) return c;
        return b switch
        {
            Biome.Desert => CoverClass.Sand,
            Biome.Steppe => CoverClass.DryGrass,
            Biome.Savanna => IsWooded(c) ? CoverClass.OpenForest : CoverClass.DryGrass,
            Biome.TropicalForest => IsWooded(c) ? c : CoverClass.LushGround,
            Biome.Boreal => c,
            Biome.Tundra => CoverClass.Tundra,
            Biome.Wetland => IsWooded(c) ? c : CoverClass.Wetland,
            _ => c,
        };
    }

    /// <summary>The share of the generator's farms a biome keeps (whole, by their ~300 m cell).</summary>
    public static double FarmShare(Biome b) => b switch
    {
        Biome.Desert or Biome.Tundra or Biome.Ice or Biome.Sea => 0,
        Biome.Steppe => 1 / 6.0,
        Biome.Savanna => 1 / 4.0,
        Biome.TropicalForest or Biome.Boreal => 1 / 5.0,
        Biome.Wetland => 1 / 3.0,
        _ => 1,
    };

    /// <summary>The share of the generator's trees a biome keeps.</summary>
    public static double TreeShare(Biome b) => b switch
    {
        Biome.Desert => 1 / 50.0,
        Biome.Steppe => 1 / 6.0,
        Biome.Savanna => 1 / 4.0,
        Biome.Tundra => 1 / 30.0,
        Biome.Wetland => 1 / 3.0,
        Biome.Ice or Biome.Sea => 0,
        _ => 1,
    };

    // the generator's tree kinds (ProceduralWorld.BuildTrees, the preprocessor's)
    private const byte KindConifer = 0, KindShrub = 1, KindSolitary = 3;

    /// <summary>A kept tree in its biome: kind and height.</summary>
    public static TreeInstance Tree(Biome b, TreeInstance tree, double r) => b switch
    {
        Biome.Desert => tree with { Kind = KindShrub, Height = 1.2f + (float)r * 1.3f },
        Biome.Tundra => tree with { Kind = KindShrub, Height = 0.6f + (float)r * 0.8f },
        Biome.Steppe => tree with { Kind = r < 0.5 ? KindShrub : KindSolitary, Height = tree.Height * 0.6f },
        Biome.Savanna => tree with { Kind = KindSolitary, Height = 6f + (float)r * 5f },
        Biome.TropicalForest => tree.Kind == KindConifer ? tree with { Kind = KindSolitary, Height = tree.Height * 1.2f } : tree,
        Biome.Boreal => tree.Kind == KindSolitary ? tree with { Kind = KindConifer } : tree,
        _ => tree,
    };
}
