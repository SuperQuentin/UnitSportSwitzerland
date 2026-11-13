using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// A tile of the world's ground, opaque (#1532, G2 C1): what <see cref="IWorldGround"/>'s tile events and
/// per-tile lookups name. The flat world's instance (<see cref="ChunkManager"/>) fills it from its LV95 tile;
/// the earth instance fills it from a cube leaf (<see cref="FromLeaf"/>), so code keyed by it runs on either. A dictionary key and a
/// name in a log: nothing else is read off it.
///
/// <para>
/// What a tile holds (roads, buildings, doors, interiors) is filed under a <see cref="TileId"/> name: the flat world's
/// LV95 tile, a leaf's stand-in (<see cref="Leaves.LeafStandIn"/>). Gameplay asks the ground for it
/// (<see cref="IWorldGround.ContentName"/>, <see cref="IWorldGround.Content"/>, #1684); only the flat world itself
/// (<c>ChunkManager</c>) makes a tile from its LV95 key or reads that key back (<see cref="Flat"/>, <see cref="FlatId"/>,
/// held there by <c>GroundTileContainmentTests</c>), and step D deletes both with it.
/// </para>
/// </summary>
public readonly record struct GroundTile
{
    private readonly TileId _lv95;
    private readonly CubeKey _leaf;
    private readonly bool _isLeaf;

    private GroundTile(TileId lv95) => _lv95 = lv95;

    private GroundTile(CubeKey leaf)
    {
        _leaf = leaf;
        _isLeaf = true;
    }

    /// <summary>The flat world's tile for its LV95 tile key: the flat world's own (<c>ChunkManager</c>), nobody else's.</summary>
    internal static GroundTile Flat(TileId id) => new(id);

    /// <summary>The LV95 tile key of a flat world's tile (default for a leaf): the flat world's own, nobody else's.</summary>
    internal TileId FlatId => _isLeaf ? default : _lv95;

    /// <summary>
    /// The name what this tile holds is filed under: a flat tile's LV95 key, a leaf's stand-in tile (#1510). Through
    /// <see cref="IWorldGround.ContentName"/>.
    /// </summary>
    internal TileId ContentName => !_isLeaf ? _lv95 : _leaf.Level == Leaves.LeafFrames.LeafLevel ? Leaves.LeafStandIn.Id(_leaf) : default;

    /// <summary>The earth world's tile: a cube leaf (#1510).</summary>
    public static GroundTile FromLeaf(CubeKey key) => new(key);

    /// <summary>Whether this is an earth world's leaf, not a flat world's LV95 tile.</summary>
    public bool IsLeaf => _isLeaf;

    /// <summary>The cube key of an earth world's leaf (default for a flat world's tile).</summary>
    public CubeKey Leaf => _leaf;

    public override string ToString() => _isLeaf ? _leaf.ToString() : _lv95.ToString();
}
