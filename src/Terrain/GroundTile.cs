using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// A tile of the world's ground, opaque (#1532, G2 C1): what <see cref="IWorldGround"/>'s tile events and
/// per-tile lookups name. The flat world's instance (<see cref="ChunkManager"/>) fills it from its LV95 tile;
/// the earth instance fills it from a cube leaf (<see cref="FromLeaf"/>), so code keyed by it runs on either. A dictionary key and a
/// name in a log: nothing else is read off it.
///
/// <para>
/// Code that still needs the LV95 tile (a <see cref="IChunkSource"/> load, a <c>BuildingKey</c> made of its
/// kilometres) crosses with <see cref="FromLv95"/> and <see cref="Lv95Tile"/>, the flat world's escape
/// hatch: <c>FlatLv95RatchetTests</c> counts both (with <c>ChunkManager.Of</c>), and step D takes the count to 0.
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

    /// <summary>The flat world's tile for an LV95 tile key (counted by the ratchet).</summary>
    public static GroundTile FromLv95(TileId id) => new(id);

    /// <summary>
    /// The LV95 tile key of a flat world's tile (counted by the ratchet); for an earth world's leaf, its stand-in tile's name
    /// (<see cref="Leaves.LeafStandIn"/>, #1510), which the origin places and the earth world's chunk source loads.
    /// </summary>
    public TileId Lv95Tile => !_isLeaf ? _lv95 : _leaf.Level == Leaves.LeafFrames.LeafLevel ? Leaves.LeafStandIn.Id(_leaf) : default;

    /// <summary>The earth world's tile: a cube leaf (#1510).</summary>
    public static GroundTile FromLeaf(CubeKey key) => new(key);

    /// <summary>Whether this is an earth world's leaf, not a flat world's LV95 tile.</summary>
    public bool IsLeaf => _isLeaf;

    /// <summary>The cube key of an earth world's leaf (default for a flat world's tile).</summary>
    public CubeKey Leaf => _leaf;

    public override string ToString() => _isLeaf ? _leaf.ToString() : _lv95.ToString();
}
