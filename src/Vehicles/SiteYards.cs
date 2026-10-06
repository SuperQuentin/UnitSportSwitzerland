using Godot;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Vehicles;

/// <summary>
/// The open ground an industrial site parks its fleet on (#496 phase 3), worked out from the tile's
/// own files and handed to <see cref="DormantSlots.ForSite"/>. This is the Godot half of that
/// provider: it may use <c>src/Interiors</c> and the plan box, which
/// <see cref="VehicleSlot"/>'s file may not if it is to stay in tier 0.
///
/// <para>
/// A yard is the strip in front of the building's own front — the wall its door is on, which
/// <see cref="BuildingFootprint"/> has already pointed at the street. That is where the apron,
/// the forecourt and the lorry park actually are. It is kept clear of the doorway itself, and any
/// vehicle that would end up inside another building or on a road is dropped rather than nudged:
/// a dropped slot leaves a gap, a nudged one would be a car in a hedge.
/// </para>
///
/// <para>
/// Everything here is a pure function of the tile's bytes, like the provider it feeds, so the
/// server and every client work out the same yards and nothing about them is ever sent.
/// </para>
/// </summary>
public static class SiteYards
{
    /// <summary>The apron: no vehicle stands within this of the facade, so the doors stay usable.</summary>
    public const float Apron = 7f;

    /// <summary>Beyond this from the building there is no yard, whatever the site.</summary>
    private static float DepthFor(BuildingType site) => site switch
    {
        BuildingType.Depot => 34f,       // artics need to swing and to stand nose to tail
        BuildingType.Warehouse => 26f,   // trailers backed at the dock, and room to pull off it
        BuildingType.Factory => 20f,
        BuildingType.Dealership => 17f,  // a forecourt is shallow and faces the road
        _ => 13f,                        // a body shop: a handful of customers' cars
    };

    /// <summary>
    /// The yards of one tile's industrial sites, in building order. <paramref name="grid"/> gives
    /// the ground each stands on; without it the building's own base is used, which is close enough
    /// for flat ground and is what <see cref="BuildingFootprint"/> falls back to as well.
    /// </summary>
    /// <summary>
    /// Whether any building of <paramref name="tile"/> is an industrial site, the check
    /// <see cref="For"/> starts with, without the footprints. Lets a caller skip the tile's height
    /// grid (2 MB, streamed to a client without local terrain, #63) on the tiles with none.
    /// </summary>
    public static bool HasSite(BuildingTile tile)
    {
        var map = BuildingTypes.For(tile);
        for (int i = 0; i < tile.Buildings.Count; i++)
        {
            if (map.Boxes[i] is not { } box) continue;
            var b = tile.Buildings[i];
            if (BuildingTypes.SiteFor(new BuildingKey(tile.Id.E, tile.Id.N, i).ToString(), b.Kind, box.Width, box.Depth, b.MaxY - b.MinY) != BuildingType.None)
                return true;
        }
        return false;
    }

    public static List<SiteYard> For(BuildingTile tile, RoadTile? roads, ChunkGrid? grid) =>
        Fronts(tile, roads, grid).Select(f => f.Yard).ToList();

    /// <summary>
    /// Each site's yard with the front wall it lies against (<see cref="SiteFront"/>): the facade's
    /// span and the doors on it, in the yard's u. What the apron's pallets and forklift keep clear of.
    /// </summary>
    public static List<SiteFront> Fronts(BuildingTile tile, RoadTile? roads, ChunkGrid? grid)
    {
        var yards = new List<SiteFront>();
        var map = BuildingTypes.For(tile);
        for (int i = 0; i < tile.Buildings.Count; i++)
        {
            if (map.Boxes[i] is not { } box) continue;
            var b = tile.Buildings[i];
            var key = new BuildingKey(tile.Id.E, tile.Id.N, i);
            var site = BuildingTypes.SiteFor(key.ToString(), b.Kind, box.Width, box.Depth, b.MaxY - b.MinY);
            if (site == BuildingType.None) continue;

            // the front: the wall the door is on, already aimed at the street by the footprint
            var fp = BuildingFootprint.Compute(tile, i, roads, grid);
            if (fp == null || fp.Door.Width <= 0) continue;
            var outward = new Vector2(fp.Door.Outward.X, fp.Door.Outward.Z);
            if (outward.LengthSquared() < 1e-6f) continue;
            outward = outward.Normalized();

            // a vehicle in the yard stands nose out, away from the building: yaw 0 faces -Z, so the
            // heading whose facing is `outward` is atan2(-x, -z)
            float heading = Mathf.Atan2(-outward.X, -outward.Y);
            float depth = DepthFor(site);
            var door = new Vector2(fp.Door.Position.X, fp.Door.Position.Z);
            var centre = door + outward * (Apron + depth / 2);

            float ground = grid != null
                ? (float)grid.SampleMeshHeight(tile.Id.MinE + centre.X, tile.Id.MaxN - centre.Y)
                : b.MinY;
            // a little wider than the facade, because a yard is not walled to the building's line
            var yard = new SiteYard(key.ToString(), (int)site, centre.X, ground, centre.Y, heading, fp.Width + 6f, depth);
            // the doors on that wall, along it: u is (cos, -sin) in (x, z), as DormantSlots lays it
            var along = new Vector2(Mathf.Cos(heading), -Mathf.Sin(heading));
            var doors = fp.Doors
                .Where(d => new Vector2(d.Outward.X, d.Outward.Z).Normalized().Dot(outward) > 0.9f)
                .Select(d => new DoorSpan(along.Dot(new Vector2(d.Position.X, d.Position.Z) - centre), d.Width / 2))
                .ToArray();
            // the facade itself along the yard: the yard is centred on the main door, not on it
            var (from, to) = box.Along(along, centre);
            yards.Add(new SiteFront(yard, from, to, doors));
        }
        return yards;
    }

    /// <summary>
    /// The pallets out on the aprons of one tile's sites (#583 phase 3, <see cref="SitePallets"/>),
    /// each on its own ground: dropped, not nudged, where one would stand in a building or on a
    /// road, which keeps the slots that name the rest. Worker-thread safe, like <see cref="For"/>.
    /// </summary>
    /// <param name="fronts">The tile's fronts if already worked out (<see cref="Fronts"/>), else null.</param>
    public static List<YardPallet> Pallets(BuildingTile tile, RoadTile? roads, ChunkGrid? grid, List<SiteFront>? fronts = null)
    {
        var list = new List<YardPallet>();
        var map = BuildingTypes.For(tile);
        foreach (var front in fronts ?? Fronts(tile, roads, grid))
            SitePallets.ForSite(tile.Id, front, Apron, list);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var p = list[i];
            var at = new Vector2((float)(p.E - tile.Id.MinE), (float)(tile.Id.MaxN - p.N));
            if (Blocked(tile, roads, map, at, 0.9f)) { list.RemoveAt(i); continue; }
            if (grid != null) list[i] = p with { Height = grid.SampleMeshHeight(p.E, p.N) };
        }
        return list;
    }

    /// <summary>
    /// One yard pallet by its building and slot, worked out from the tile's own files as every peer
    /// draws it: what the server checks a forklift's request against. Null if there is none.
    /// Blocks on the source: call it off the main thread.
    /// </summary>
    public static YardPallet? PalletAt(IChunkSource source, string building, int slot)
    {
        if (!BuildingKey.TryParse(building, out var key)) return null;
        var tile = source.LoadBuildingsAsync(key.Tile).GetAwaiter().GetResult();
        if (tile is not { Buildings.Count: > 0 } || !HasSite(tile)) return null;
        var roads = source.LoadRoadsAsync(key.Tile).GetAwaiter().GetResult();
        var grid = source.LoadChunkAsync(key.Tile).GetAwaiter().GetResult();
        foreach (var p in Pallets(tile, roads, grid))
            if (p.Slot == slot && p.Building == building) return p;
        return null;
    }

    /// <summary>
    /// Whether a slot cannot stand where the grid put it: inside a building (its own included —
    /// an L-shaped works wraps round its own yard), or on a road. Measured in tile-local metres,
    /// from the same bytes on every peer.
    /// </summary>
    public static bool Blocked(BuildingTile tile, RoadTile? roads, BuildingTypeMap map, Vector2 at, float radius)
    {
        for (int i = 0; i < tile.Buildings.Count; i++)
            if (map.Boxes[i] is { } box && box.DistanceTo(at) < radius) return true;
        if (roads == null) return false;
        foreach (var s in roads.Segments)
        {
            if (RoadFormat.IsAerial(s.Class)) continue;
            float half = s.Width * 0.5f + radius;
            for (int k = 0; k + 1 < s.PointCount; k++)
            {
                var a = new Vector2(s.Points[k * 3], s.Points[k * 3 + 2]);
                var c = new Vector2(s.Points[k * 3 + 3], s.Points[k * 3 + 5]);
                var ac = c - a;
                float t = ac.LengthSquared() < 1e-6f ? 0 : Mathf.Clamp((at - a).Dot(ac) / ac.LengthSquared(), 0, 1);
                if ((a + ac * t).DistanceTo(at) < half) return true;
            }
        }
        return false;
    }
}
