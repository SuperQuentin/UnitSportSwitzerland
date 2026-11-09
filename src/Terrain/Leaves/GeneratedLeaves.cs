using System.Collections.Concurrent;
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
public sealed class GeneratedLeaves(ProceduralWorld world, LeafFrames? frames = null)
{
    /// <summary>LV95 tiles whose products are held.</summary>
    public const int KeptTiles = 24;

    private readonly LeafFrames _frames = frames ?? LeafFrames.Swiss;
    private readonly ConcurrentDictionary<TileId, Lazy<Products>> _tiles = new();

    public ProceduralWorld World => world;

    private sealed record Products(byte[] Cover, WaterTile? Water, List<TreeInstance> Trees, BuildingTile? Buildings);

    private Products Tile(TileId id)
    {
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
