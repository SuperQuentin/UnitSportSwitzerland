using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// A tile of the world's ground, opaque (#1532, G2 C1): what <see cref="IWorldGround"/>'s tile events and
/// per-tile lookups name. The flat world's instance (<see cref="ChunkManager"/>) fills it from its LV95 tile;
/// the leaf instance will fill it from a cube key, so code keyed by it runs on either. A dictionary key and a
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

    private GroundTile(TileId lv95) => _lv95 = lv95;

    /// <summary>The flat world's tile for an LV95 tile key (counted by the ratchet).</summary>
    public static GroundTile FromLv95(TileId id) => new(id);

    /// <summary>The LV95 tile key of a flat world's tile (counted by the ratchet).</summary>
    public TileId Lv95Tile => _lv95;

    public override string ToString() => _lv95.ToString();
}
