using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

public partial class ChunkManager
{
    /// <summary>
    /// The streamer behind a flat world's ground, for what still loads LV95 tiles through its
    /// <see cref="Source"/> (#1532: they move with G2 C1's leaf half). Null for any other ground.
    /// Counted by <c>FlatLv95RatchetTests</c> with the other flat-world escape hatches.
    /// </summary>
    public static ChunkManager? Of(IWorldGround? ground) => (ground as FlatGround)?.Owner;

    /// <summary>
    /// The flat world's <see cref="IWorldGround"/> (#1532): this streamer's LV95 tiles, answered by the
    /// streamer's own methods (the same code as before the interface, the same values). A class of its own,
    /// not the streamer, so a <see cref="ChunkManager"/> cannot be handed over where the ground is wanted:
    /// whoever wires a world passes <see cref="Ground"/>, which the earth world points at its leaves.
    /// </summary>
    private sealed class FlatGround(ChunkManager m) : IWorldGround
    {
        public ChunkManager Owner => m;

        public WorldOrigin? Origin => m._origin;

        public bool TryGetHeight(Vector3 worldPos, out float height) => m.HeightAt(worldPos, out height);

        public bool TryGetSurface(Vector3 worldPos, out float height) => m.SurfaceAt(worldPos, out height);

        public bool TryGetCover(Vector3 worldPos, out CoverClass cover) => m.CoverAt(worldPos, out cover);

        public bool TryGetWaterLevel(Vector3 worldPos, out float stillLevel) => m.WaterAt(worldPos, out stillLevel, out _);

        public bool TryGetWater(Vector3 worldPos, out float stillLevel, out float waveScale) =>
            m.WaterAt(worldPos, out stillLevel, out waveScale);

        public bool InTunnel(Vector3 worldPos) => m.InTunnelAt(worldPos);

        public bool InPit(Vector3 worldPos) => m.InPitAt(worldPos);

        public bool FloorBelow(Node3D body, Vector3 worldPos, Rid self, float depth = 4f) =>
            ChunkManager.FloorBelow(body, worldPos, self, depth);

        public bool HasCollisionAt(Vector3 worldPos) => m.CollisionAt(worldPos);

        public bool BuildingCollisionDoneAt(Vector3 worldPos) => m.BuildingCollisionAt(worldPos);

        public GroundTile TileAt(Vector3 worldPos) =>
            m._origin is { } origin ? GroundTile.FromLv95(origin.TileAt(worldPos)) : default;

        public MeshInstance3D? GroundAt(GroundTile tile) => m.GroundMeshAt(tile);

        public void AddAnchor(Node3D anchor, bool? collision = null) => m.AddAnchor(anchor, collision);

        public void RemoveAnchor(Node3D anchor) => m.RemoveAnchor(anchor);

        public bool HasAnchor(Node3D anchor) => m.HasAnchor(anchor);

        public event Action<GroundTile>? TileEntered
        {
            add => m._tileEntered += value;
            remove => m._tileEntered -= value;
        }

        public event Action<GroundTile>? TileUnloaded
        {
            add => m._tileUnloaded += value;
            remove => m._tileUnloaded -= value;
        }

        public event Action<GroundTile, ChunkNode, Interiors.DoorSpot[]>? TileFurnished
        {
            add => m._tileFurnished += value;
            remove => m._tileFurnished -= value;
        }

        public event Action<GroundTile, ChunkNode>? TileUnfurnished
        {
            add => m._tileUnfurnished += value;
            remove => m._tileUnfurnished -= value;
        }

        public event Action<Func<GroundTile, bool>?>? TerrainReplaced
        {
            add => m._terrainReplaced += value;
            remove => m._terrainReplaced -= value;
        }
    }
}
