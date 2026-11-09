using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// The leaves' furniture (#1510, G2 C1 PR 2): roads, buildings and their doors, building sites, trees and water, made
/// by today's builders on each leaf's stand-in tile (<see cref="LeafStand"/>, the 1 m grid and the records shifted
/// into a tile's frame) and drawn in a <see cref="ChunkNode"/> placed by the leaf's transform and the shift. The doors
/// go into the <see cref="DoorIndex"/> under the stand-in tile's name, and <see cref="TileFurnished"/> carries the node
/// to the systems that dress a tile (signs, terraces, decorations), as the flat world's does.
/// </summary>
public sealed partial class EarthWorld
{
    /// <summary>The flat streamer, out of the tree: its materials, detail and ring policy, and what it keeps in world space.</summary>
    public ChunkManager? Flat { get; set; }

    /// <summary>Set once the node leaves the tree: a worker makes no Godot object after it (<c>tile-worker-create-godot-object</c>).</summary>
    private volatile bool _gone;

    public event Action<GroundTile, ChunkNode, DoorSpot[]>? TileFurnished;
    public event Action<GroundTile, ChunkNode>? TileUnfurnished;

    /// <summary>Leaves furnished (their node built).</summary>
    public int FurnishedCount { get; private set; }

    /// <summary>What a worker made for a leaf's node.</summary>
    private sealed class Furniture
    {
        public required TileId Id;
        public ArrayMesh? Roads;
        public ArrayMesh?[]? RoadCells, PaintCells;
        public SignalBuilder.Lamps? Lamps;
        public RoadSignBuilder.Label[]? SignNames;
        public ArrayMesh?[]? BuildingCells, Proxies;
        public Vector3[]? OccluderVertices;
        public int[]? OccluderIndices;
        public ArrayMesh? Sites;
        public Construction.CraneRig[]? Cranes;
        public ChunkNode.TreeMeshes? Trees;
        public ArrayMesh? Water;
        public DoorSpot[] Doors = [];
        public int RoadCount, BuildingCount, TreeCount;
    }

    /// <summary>A stand-in tile's frame in the world: the leaf's anchor frame, shifted.</summary>
    public Transform3D StandInToWorld(CubeKey leaf) => LeafToWorld(leaf.Anchor) * LeafStand.TileInAnchor;

    /// <summary>A leaf's furniture, on a worker: as the flat world's chunk build makes a near tile's.</summary>
    private Furniture? Furnish(LeafStand stand, CubeLeafContent content, int side)
    {
        var flat = Flat;
        if (flat == null || _gone) return null;
        var m = flat.FurnishMaterials;
        var detail = flat.Detail;
        bool roadCells = Core.GameSettings.Lean;
        var grid = stand.Grid;
        var f = new Furniture { Id = stand.Id, Doors = stand.Doors ?? [] };
        // patterns from the leaf's place on the Earth, never the world frame's (#1512)
        var pattern = LeafPattern.For(content.Key);

        if (stand.Roads is { } roads && m.Road is { } roadMaterial)
        {
            f.RoadCount = roads.Segments.Count;
            if (RoadMeshBuilder.Build(roads, grid, detail) is { } roadData)
            {
                var paint = RoadPaintBuilder.Build(roads);
                if (_gone) return null;
                if (roadCells)
                {
                    f.RoadCells = ChunkNode.ToArrayMeshes(ChunkNode.SplitByCell(roadData), roadMaterial, pattern);
                    if (paint != null) f.PaintCells = ChunkNode.ToArrayMeshes(ChunkNode.SplitByCell(paint), roadMaterial, pattern);
                    f.Roads = new ArrayMesh();
                }
                else
                {
                    f.Roads = ChunkNode.ToArrayMesh(roadData, roadMaterial, pattern: pattern);
                    if (paint != null) f.PaintCells = [ChunkNode.ToArrayMesh(paint, roadMaterial, pattern: pattern)];
                }
                f.Lamps = SignalBuilder.BuildLamps(roads);
                f.SignNames = RoadSignBuilder.Labels(roads);
                if (RoadSignBuilder.GantryLabels(roads) is { } gantry) f.SignNames = [.. f.SignNames ?? [], .. gantry];
            }
        }

        List<Construction.PitCut>? pits = null;
        if (stand.Buildings is { } buildings)
        {
            f.BuildingCount = buildings.Buildings.Count;
            if (stand.Sites.Count > 0 && m.Prop is { } prop
                && Construction.SiteShellBuilder.Build(buildings, stand.Sites, grid, mesh: true, collision: false) is { } shells)
            {
                if (_gone) return null;
                if (shells.Mesh is { } shellData) f.Sites = ChunkNode.ToPropMesh(shellData, prop);
                pits = shells.Pits;
                if (shells.Cranes.Count > 0) f.Cranes = shells.Cranes.Select(c => Construction.CraneRig.Make(c, prop)).ToArray();
            }
            if (m.Building is { } buildingMaterial
                && BuildingMeshBuilder.Build(buildings, f.Doors, detail, grid, 1) is { } buildingData)
            {
                if (_gone) return null;
                var parts = BuildingOcclusion.SplitByCell(buildingData);
                f.BuildingCells = new ArrayMesh?[parts.Length];
                for (int c = 0; c < parts.Length; c++)
                    if (parts[c] is { } part) f.BuildingCells[c] = ChunkNode.ToArrayMesh(part, buildingMaterial, pattern);
                if (BuildingOcclusion.Occluders(buildings) is var (ov, oi)) (f.OccluderVertices, f.OccluderIndices) = (ov, oi);
                if (BuildingMeshBuilder.BuildProxies(buildings) is { } proxyData)
                {
                    var proxyParts = BuildingOcclusion.SplitByCell(proxyData);
                    f.Proxies = new ArrayMesh?[proxyParts.Length];
                    for (int c = 0; c < proxyParts.Length; c++)
                        if (proxyParts[c] is { } part) f.Proxies[c] = ChunkNode.ToArrayMesh(part, buildingMaterial, pattern);
                }
            }
        }

        if (content.Trees is { Count: > 0 } leafTrees && m.Tree is { } treeMaterial)
        {
            var trees = LeafStandIn.Trees(leafTrees);
            // a tree does not stand in a pit (#619)
            if (pits is { Count: > 0 }) trees = trees.Where(t => !pits.Any(p => p.Rect.Contains(new Vector2(t.X, t.Z), 1f))).ToList();
            f.TreeCount = trees.Count;
            var bounds = new Aabb(new Vector3(0, grid.MinHeight - 10, 0), new Vector3(1000, grid.MaxHeight - grid.MinHeight + 80, 1000));
            var buffers = ChunkNode.BuildTreeBuffers(trees);
            if (_gone) return null;
            f.Trees = ChunkNode.BuildTreeMeshes(buffers, treeMaterial, bounds, detail);
        }

        // the still water and the streams riding in the roads, on the stand-in's 2 m layer
        if (m.Water is { } waterMaterial && content.Water is { } water)
        {
            var layer = LeafStandIn.Water(water, LeafFrames.Swiss.TransformOf(content.Key), side, stand.Nearest, grid);
            if (WaterMeshBuilder.Build(layer, stand.Roads, 1, detail) is { } waterData && !_gone)
                f.Water = ChunkNode.ToArrayMesh(waterData, waterMaterial, pattern);
        }
        else if (m.Water is { } streamMaterial && stand.Roads != null
            && WaterMeshBuilder.Build(null, stand.Roads, 1, detail) is { } streams && !_gone)
            f.Water = ChunkNode.ToArrayMesh(streams, streamMaterial, pattern);
        return f;
    }

    /// <summary>A leaf's furniture into its node, on the main thread, as the flat world commits a tile's tail.</summary>
    private ChunkNode? Commit(CubeKey key, Furniture f)
    {
        var flat = Flat;
        var node = new ChunkNode { Name = $"Leaf_{key.FileStem}" };
        AddChild(node);
        node.Transform = StandInToWorld(key);
        if (f.Roads != null)
        {
            node.SetRoads(f.Roads);
            node.SetRoadCells(f.RoadCells, f.PaintCells, flat?.Lod.RoadPaintToM ?? 1000f);
            node.SetSignalLamps(f.Lamps);
            node.SetSignNames(f.SignNames);
        }
        if (f.BuildingCells != null)
            node.SetBuildingCells(f.BuildingCells, f.OccluderVertices, f.OccluderIndices, f.Proxies, flat?.Lod.BuildingBoxFromM ?? 1000f);
        if (f.Sites != null) node.SetSites(f.Sites, f.Cranes, flat?.PierMaterial);
        DoorIndex.SetTile(f.Id, node.Transform, f.Doors);
        TileFurnished?.Invoke(GroundTile.FromLeaf(key), node, f.Doors);
        flat?.RaiseFurnished(GroundTile.FromLeaf(key), node, f.Doors);
        if (f.Trees != null)
        {
            node.SetTrees(f.Trees);
            node.SetTreeDensity(flat?.Lod.TreeDensity(0) ?? 1f);
        }
        if (f.Water != null) node.SetWater(f.Water);
        FurnishedCount++;
        return node;
    }

    /// <summary>A leaf's node out: its doors and what dressed it go first.</summary>
    private void Unfurnish(CubeKey key, ChunkNode node)
    {
        TileUnfurnished?.Invoke(GroundTile.FromLeaf(key), node);
        Flat?.RaiseUnfurnished(GroundTile.FromLeaf(key), node);
        DoorIndex.ClearTile(LeafStandIn.Id(key));
        node.QueueFree();
        FurnishedCount--;
    }

    /// <summary>A stand-in tile's frame for <see cref="WorldOrigin.StandIns"/>: any leaf, loaded or not.</summary>
    private Transform3D? StandInFrame(TileId id) => LeafStandIn.TryLeaf(id, out var leaf) ? StandInToWorld(leaf) : null;

    /// <summary>The leaf's stand-in, prepared with its collision (kept for the last few leaves).</summary>
    public LeafStand? StandOf(CubeKey leaf) => _collisionSource.Prepared(leaf)?.Stand;

    /// <summary>A chunk source answering the leaves' stand-in tiles too, over <paramref name="inner"/>.</summary>
    public StandInChunkSource Source(IChunkSource inner) => new(inner, StandOf, Leaves.Get);

    public override void _EnterTree()
    {
        _gone = false;
        Origin.StandIns = StandInFrame;
    }

    public override void _ExitTree()
    {
        _gone = true;
        if (Origin.StandIns == StandInFrame) Origin.StandIns = null;
    }
}
