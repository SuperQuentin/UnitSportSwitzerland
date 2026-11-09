using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// A leaf as its stand-in tile (<see cref="LeafStandIn"/>, #1510): the 1 m grid of its bare ground, its roads and
/// buildings in the tile's frame, its building sites and the doors of its buildings, all as the flat world's chunk
/// build makes them for a tile. Made once per leaf with its collision (<see cref="LeafCollision.Prepare"/>), on a
/// worker, on every peer alike (garage links, stoops and site offices collide), and read again to draw it.
/// </summary>
public sealed class LeafStand
{
    public required CubeKey Leaf { get; init; }

    /// <summary>The leaf's tile name (<see cref="LeafStandIn.Id"/>).</summary>
    public required TileId Id { get; init; }

    /// <summary>The bare ground (not blended to the roads, as a tile's grid), 1 m, quantised.</summary>
    public required ChunkGrid Grid { get; init; }

    /// <summary>Each 2 m sample's nearest lattice vertex (−1 off the leaf): the water layer reads the leaf's water through it.</summary>
    public required int[] Nearest { get; init; }

    public RoadTile? Roads { get; init; }
    public BuildingTile? Buildings { get; init; }

    /// <summary>The building sites (#605) planned from the buildings and roads; empty without any.</summary>
    public List<ConstructionSite> Sites { get; init; } = new();

    /// <summary>The doors of the buildings (in the tile's frame), as <c>BuildingFootprint.ComputeDoors</c> gives a tile's; null without buildings.</summary>
    public DoorSpot[]? Doors { get; init; }

    /// <summary>
    /// What collides besides the ground, roads and buildings' shells, in the tile's frame: the sites' shells, yards and
    /// office walls, garage links and stoops (the faces C2 left to C1).
    /// </summary>
    public Vector3[] ExtraFaces { get; init; } = [];

    /// <summary>The leaf's stand-in from its content and its bare vertices (anchor frame, before any blend).</summary>
    public static LeafStand Make(CubeLeafContent leaf, float[] bareVertices, int side)
    {
        var id = LeafStandIn.Id(leaf.Key);
        var nearest = new int[WaterTile.Size * WaterTile.Size];
        var grid = LeafStandIn.Grid(id, bareVertices, side, nearest);
        var roads = leaf.Roads is { Segments.Count: > 0 } r ? LeafStandIn.Roads(r, id) : null;
        var buildings = leaf.Buildings is { Buildings.Count: > 0 } b ? LeafStandIn.Buildings(b, id) : null;

        var sites = new List<ConstructionSite>();
        DoorSpot[]? doors = null;
        var extra = new List<Vector3>();
        if (buildings != null)
        {
            if (SitePlans.HasSite(buildings))
            {
                sites = SitePlans.For(buildings, roads);
                if (SiteShellBuilder.Build(buildings, sites, grid, mesh: false, collision: true) is { } shells)
                {
                    extra.AddRange(shells.Faces);
                    // the site offices' walls are a building's (#822)
                    extra.AddRange(shells.OfficeFaces);
                }
            }
            doors = BuildingFootprint.ComputeDoors(buildings, roads, grid, sites);
            // a garage door's access road is drivable ground (#558), a stoop's steps are climbed (#1417)
            extra.AddRange(BuildingMeshBuilder.LinkFaces(doors));
            extra.AddRange(BuildingMeshBuilder.StoopFaces(doors));
        }
        return new LeafStand
        {
            Leaf = leaf.Key, Id = id, Grid = grid, Nearest = nearest, Roads = roads, Buildings = buildings,
            Sites = sites, Doors = doors, ExtraFaces = extra.ToArray(),
        };
    }

    /// <summary>A point of the tile's frame in the leaf's anchor frame.</summary>
    public static Vector3 ToAnchor(Vector3 p) => new(p.X - (float)LeafStandIn.Offset, p.Y, p.Z - (float)LeafStandIn.Offset);

    /// <summary>The tile's frame in the leaf's anchor frame: a shift.</summary>
    public static Transform3D TileInAnchor => new(Basis.Identity, new Vector3(-(float)LeafStandIn.Offset, 0, -(float)LeafStandIn.Offset));
}
