using System.Collections.Concurrent;
using UnitSport.Terrain.Biomes;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The generated world (<see cref="ProceduralWorld"/>) built as cube leaves in memory (#1509, G2 B5), beside the LV95
/// tiles <see cref="FallbackChunkSource"/> serves; the game reads them once C1 draws leaves, behind <c>--frame earth</c>.
/// The world stays a function of LV95 position (its notation: a leaf vertex's LV95 point is its latitude and longitude
/// through <see cref="Lv95Source"/>), sampled as today:
/// <list type="bullet">
/// <item>heights: <see cref="ProceduralWorld.HeightIn"/> at every vertex, the generator's own function;</item>
/// <item>cover and water: the generator's tile products (its classifier fills 10 m squares of a tile whole), the 1 m
/// cover cell and the 2 m water sample nearest each vertex;</item>
/// <item>trees and buildings: the tile's, each moved into the leaf its position or middle lies in.</item>
/// </list>
/// Roads come through RoadGen's frame seams (<c>GeneratedLeafRoads</c>). The blend against real leaves is
/// <see cref="LeafBlend"/>, on the built leaf's lattice (a convex mix, so no correction to the generator's ground here).
/// Thread-safe; a leaf costs ~0.5 s of one core, most of it the tiles' products, kept for the
/// last <see cref="KeptTiles"/> tiles (a leaf reads one to four).
/// </summary>
/// <param name="pyramid">The globe's pyramid the earth world's fill stands on (#1678, <see cref="PyramidFill"/>):
/// <see cref="OnPyramid"/> moves a built leaf onto it; null, the generator's own ground.</param>
/// <param name="biomes">The fill's biomes outside the generator's own map (#1809, <see cref="BiomePass"/>), applied last by
/// <see cref="OnPyramid"/>: by default the chain this build embeds; null, the generator everywhere.</param>
public sealed class GeneratedLeaves(ProceduralWorld world, LeafFrames? frames = null, PyramidBase? pyramid = null, FillBiome? biomes = null,
    bool noBiomes = false)
{
    /// <summary>LV95 tiles whose products are held.</summary>
    public const int KeptTiles = 24;

    private readonly LeafFrames _frames = frames ?? LeafFrames.Swiss;
    private readonly ConcurrentDictionary<TileId, Lazy<Products>> _tiles = new();

    public ProceduralWorld World => world;

    public PyramidBase? Pyramid => pyramid;

    /// <summary>The biome chain the fill's leaves are finished in, or null (the generator everywhere).</summary>
    public FillBiome? Biomes { get; } = noBiomes ? null : biomes ?? FillBiome.Default;

    private sealed record Products(byte[] Cover, WaterTile? Water, List<TreeInstance> Trees, BuildingTile? Buildings);

    private Products Tile(TileId id)
    {
        // read per lattice vertex: a held tile is a lookup alone (ConcurrentDictionary.Count takes every lock, #1699)
        if (_tiles.TryGetValue(id, out var held)) return held.Value;
        if (_tiles.Count > KeptTiles) _tiles.Clear();
        return _tiles.GetOrAdd(id, t => new Lazy<Products>(() =>
            new Products(world.BuildCover(t), world.BuildWater(t), world.BuildTrees(t), world.BuildBuildings(t)))).Value;
    }

    /// <summary>
    /// A generated leaf: heights, cover, water, trees and buildings (roads: <c>GeneratedLeafRoads</c>), the generator's
    /// own; <see cref="LeafBlend.Apply"/> bends it into the real leaves near it.
    /// </summary>
    public CubeLeafContent Build(CubeKey leaf)
    {
        var on = new SourceOnLeaf(_frames, leaf, Lv95Source.Instance);
        var content = new CubeLeafContent
        {
            Key = leaf,
            Heights = LeafLattice.Heights(on, (e, n) => world.HeightIn(TileId.FromLv95(e, n), e, n)),
            Cover = LeafLattice.Cover(on, CoverAt),
            Water = LeafLattice.Water(on, WaterAt),
            Trees = new(),
            Buildings = new BuildingTile { Buildings = new() },
        };
        foreach (var id in TilesUnder(on))
        {
            var products = Tile(id);
            var map = new SourceTileToLeaf(on, id.Global);
            foreach (var tree in products.Trees)
                if (map.Holds(tree.X, tree.Z)) content.Trees.Add(map.Move(tree));
            if (products.Buildings is { } bt)
                foreach (var b in bt.Buildings)
                {
                    var (mx, mz) = SourceTileToLeaf.Middle(b);
                    if (map.Holds(mx, mz)) content.Buildings.Buildings.Add(map.Move(b));
                }
        }
        return content;
    }

    /// <summary>
    /// A generated leaf's heights alone, the generator's function every <paramref name="stride"/> vertices and bilinear
    /// between (the globe's far ground: none of the tiles' products, ~4,000 points a leaf at 8).
    /// </summary>
    public CubeTile Heights(CubeKey leaf, int stride)
    {
        var on = new SourceOnLeaf(_frames, leaf, Lv95Source.Instance);
        return LeafLattice.Heights(on, (e, n) => world.HeightIn(TileId.FromLv95(e, n), e, n), stride);
    }

    /// <summary>
    /// The generator at a point of a face's global lattice (the leaves' level and bits): its ground above sea level, a
    /// lake's surface rather than its bed, and its lake weight; for <see cref="PyramidFill"/>'s low-pass round a leaf.
    /// </summary>
    private Func<long, long, (double, double)> GeneratorOn(CubeKey leaf)
    {
        int level = leaf.Level + CubeKey.LatticeBits(leaf.Level), face = leaf.Face;
        return (gi, gj) =>
        {
            var g = CubeSphere.ToGeodetic(face, CubeSphere.Lattice(level, gi), CubeSphere.Lattice(level, gj));
            var (e, n) = Lv95Source.Instance.FromLatLon(g.LatDeg, g.LonDeg);
            var (lake, lakeLevel) = ProceduralWorld.LakeAt(e, n);
            return (lake >= 0.5 ? lakeLevel : world.HeightIn(TileId.FromLv95(e, n), e, n), lake);
        };
    }

    /// <summary>
    /// A built leaf (<see cref="Build"/>, its roads among it) moved onto the pyramid (#1678, <see cref="PyramidFill"/>);
    /// unchanged without one, or where the pyramid holds no height over it. Then in its biomes (<see cref="InBiomes"/>).
    /// </summary>
    public CubeLeafContent OnPyramid(CubeLeafContent content)
    {
        var on = new SourceOnLeaf(_frames, content.Key, Lv95Source.Instance);
        if (pyramid is not null && content.Heights is { } h
            && PyramidFill.Build(pyramid, on.Transform, h.Heights, GeneratorOn(content.Key)) is { } ground)
            PyramidFill.Apply(content, ground, on.Transform);
        InBiomes(content, on.Transform);
        return content;
    }

    /// <summary>
    /// A finished leaf in its biomes (#1809): outside the generator's own map its cover, trees, farms and still water
    /// follow the biome there; within it (the Swiss guard) untouched. Null without a chain.
    /// </summary>
    public BiomePass.Stats? InBiomes(CubeLeafContent content, LeafTransform? transform = null) =>
        Biomes is { } chain
            ? BiomePass.Apply(content, transform ?? new SourceOnLeaf(_frames, content.Key, Lv95Source.Instance).Transform, chain, InsideMapM)
            : null;

    /// <summary>How far a latitude and longitude lie inside the generator's own map, metres (negative outside).</summary>
    public static double InsideMapM(double latDeg, double lonDeg)
    {
        var (e, n) = Lv95Source.Instance.FromLatLon(latDeg, lonDeg);
        return ProceduralWorld.MapInsideM(e, n);
    }

    /// <summary>
    /// The globe's far ground of a fill leaf (<see cref="Heights(CubeKey, int)"/>), on the pyramid when there is one, and
    /// the pyramid's lakes on it (the globe draws them as the leaf's own water).
    /// </summary>
    public (CubeTile Heights, CubeWater? Lakes) Far(CubeKey leaf, int stride)
    {
        var g = Heights(leaf, stride);
        if (pyramid is null) return (g, null);
        var on = new SourceOnLeaf(_frames, leaf, Lv95Source.Instance);
        if (PyramidFill.Build(pyramid, on.Transform, g.Heights, GeneratorOn(leaf)) is not { } ground) return (g, null);
        return (new CubeTile { Key = leaf, Bits = g.Bits, Heights = ground.Heights, Quantum = g.Quantum }, PyramidFill.Lakes(ground, g.Bits));
    }

    /// <summary>The LV95 tiles a leaf's box reaches.</summary>
    public static IEnumerable<TileId> TilesUnder(SourceOnLeaf on)
    {
        var (minE, minN, maxE, maxN) = on.Box();
        return TileRect.Covering(minE, minN, maxE, maxN).Tiles();
    }

    private CoverClass CoverAt(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        var (x, z) = id.Local(e, n);
        int c = Math.Clamp((int)Math.Round(x), 0, CoverFormat.Size - 1), r = Math.Clamp((int)Math.Round(z), 0, CoverFormat.Size - 1);
        return (CoverClass)Tile(id).Cover[r * CoverFormat.Size + c];
    }

    private (double Level, double Fetch) WaterAt(double e, double n)
    {
        var id = TileId.FromLv95(e, n);
        if (Tile(id).Water is not { } water) return (double.NaN, 0);
        var (x, z) = id.Local(e, n);
        int size = WaterTile.Size;
        int c = Math.Clamp((int)Math.Round(x / WaterTile.Stride), 0, size - 1), r = Math.Clamp((int)Math.Round(z / WaterTile.Stride), 0, size - 1);
        float level = water.Level[r * size + c];
        return (level, water.FetchM is { } f ? f[r * size + c] : 0);
    }
}
