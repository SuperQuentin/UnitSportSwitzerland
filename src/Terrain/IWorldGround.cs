using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// The ground as gameplay asks it (#1532, G2 C1): heights, cover, water, tunnels and pits, whether the
/// collision is built, the streaming anchors, and the tile events. Positions are world-space; a tile is a
/// <see cref="GroundTile"/>, never an LV95 key or corner, so the leaf instance of G2's earth world
/// satisfies it unchanged. <see cref="ChunkManager"/> serves it in the flat world through
/// <see cref="ChunkManager.Ground"/>: gameplay holds an <see cref="IWorldGround"/>, never asks a
/// <see cref="ChunkManager"/> for the ground. (B3's <c>Terrain.Format.IGround</c> is the build tools' ground, not this one.)
/// </summary>
public interface IWorldGround
{
    /// <summary>The frame world positions are in; null before the world is set up.</summary>
    WorldOrigin? Origin { get; }

    /// <summary>
    /// Whether surface patterns (and the waves) come from the cube face's (u, v) (<see cref="Earth.PatternSpace"/>,
    /// the round world's, #1512) rather than LV95 modulo 9.6 km (the flat world's).
    /// </summary>
    bool PatternsOnFaces => false;

    /// <summary>
    /// The terrain's height under a point, of the surface on screen (its flat-shaded triangles), on a
    /// tile that is loaded. Under a lake it is the bed: see <see cref="TryGetSurface"/>.
    /// </summary>
    bool TryGetHeight(Vector3 worldPos, out float height);

    /// <summary>
    /// Where something put down here rests (#299): the ground, or the still water surface where there is
    /// water over it. Spawns, respawns, drops, birds and the void rescue want this one.
    /// </summary>
    bool TryGetSurface(Vector3 worldPos, out float height);

    /// <summary>The land-cover class under a point, where the tile holds its cover (near a player).</summary>
    bool TryGetCover(Vector3 worldPos, out CoverClass cover);

    /// <summary>The still water level at a point (#299), without waves; false where dry or not loaded.</summary>
    bool TryGetWaterLevel(Vector3 worldPos, out float stillLevel);

    /// <summary><see cref="TryGetWaterLevel"/> plus the wave scale there, 0..1 (fetch x depth).</summary>
    bool TryGetWater(Vector3 worldPos, out float stillLevel, out float waveScale);

    /// <summary>Inside a tunnel bore whose collision is built (#119): legitimately under the terrain.</summary>
    bool InTunnel(Vector3 worldPos);

    /// <summary>Down in a building site's pit (#619): under the height queries' ground, over the pit's floor.</summary>
    bool InPit(Vector3 worldPos);

    /// <summary>Collision under a body within <paramref name="depth"/> metres, itself excluded (#119).</summary>
    bool FloorBelow(Node3D body, Vector3 worldPos, Rid self, float depth = 4f);

    /// <summary>The ground's collision under a point is committed: a body placed before falls through.</summary>
    bool HasCollisionAt(Vector3 worldPos);

    /// <summary>Every building collision of the tile under a point is committed (#517).</summary>
    bool BuildingCollisionDoneAt(Vector3 worldPos);

    /// <summary>The tile under a world point, loaded or not (the default tile before the world is set up).</summary>
    GroundTile TileAt(Vector3 worldPos);

    /// <summary>
    /// The name what a tile holds is filed under (#1684): what <see cref="Content"/> loads it by, what its doors and
    /// buildings are keyed by (<c>DoorIndex</c>, <c>BuildingKey</c>). The flat world's LV95 tile; an earth leaf's
    /// stand-in (<see cref="Leaves.LeafStandIn"/>, #1510). Step D re-keys what is keyed by it (C5's <c>BuildingId</c>).
    /// </summary>
    TileId ContentName(GroundTile tile) => tile.ContentName;

    /// <summary>
    /// What the world's tiles hold, loaded by <see cref="ContentName"/> (#1684): roads, buildings, trees, the 1 m grid;
    /// the region-wide files (landings, airports, ropeways) too. The flat world's chunk source; an earth world's
    /// stand-ins over its leaves (<c>StandInChunkSource</c>) with the region's files beneath. Null before it is wired.
    /// </summary>
    IChunkSource? Content { get; }

    /// <summary>A loaded tile's ground mesh, for per-tile shader parameters; null when not drawn.</summary>
    MeshInstance3D? GroundAt(GroundTile tile);

    /// <summary>
    /// Streams the ground round <paramref name="anchor"/>; collision too when <paramref name="collision"/>
    /// says so (by default, for a physics body).
    /// </summary>
    void AddAnchor(Node3D anchor, bool? collision = null);

    void RemoveAnchor(Node3D anchor);

    bool HasAnchor(Node3D anchor);

    /// <summary>What the ground streams round now (for the systems that follow it: the dormant fleets, #1649).</summary>
    IReadOnlyList<Node3D> Anchors { get; }

    /// <summary>Main thread: a tile has just come into the streamed rings (nothing is built yet).</summary>
    event Action<GroundTile>? TileEntered;

    /// <summary>Main thread: a tile has been unloaded and its node freed.</summary>
    event Action<GroundTile>? TileUnloaded;

    /// <summary>
    /// Main thread: a tile's buildings, doors and trees have just committed. Whatever hangs on the node
    /// unloads with it; doors are tile-local, like <see cref="Interiors.DoorSpot.Position"/>.
    /// </summary>
    event Action<GroundTile, ChunkNode, Interiors.DoorSpot[]>? TileFurnished;

    /// <summary>Main thread: a tile has shed its buildings (#553); what <see cref="TileFurnished"/> hung on them goes.</summary>
    event Action<GroundTile, ChunkNode>? TileUnfurnished;

    /// <summary>
    /// Main thread: tiles were unloaded because the world under them changed, for anything that cached
    /// what it read from them. The argument selects the affected tiles; null means all of them.
    /// </summary>
    event Action<Func<GroundTile, bool>?>? TerrainReplaced;
}
