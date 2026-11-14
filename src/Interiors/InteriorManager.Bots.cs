using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Net.Regions;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>How a bot's way in by a door stands (<see cref="InteriorManager.BotWantIn"/>).</summary>
public enum BotDoorState
{
    /// <summary>The plan is being made, or its collision built: ask again.</summary>
    Pending,
    /// <summary>The door is open and the interior is solid in the bot's region: walk through.</summary>
    Ready,
    /// <summary>The door has no doorway behind it (a locked door, #1766), or the building has no inside.</summary>
    Locked,
}

/// <summary>
/// Bots inside buildings (#1330), on the server. A client builds an interior when its player opens a door; a dedicated
/// server builds none (it draws nothing, and players' proxies collide with nothing). A bot is a body the server runs, so
/// the server builds the collision of an interior a bot is in or heading into, from the plan it already holds, in that
/// bot's region (its frame and physics space, #1456), and frees it once no bot has wanted it for
/// <see cref="BotRoomKeepSeconds"/>. A bot goes through a door as a player does, through the door's own map
/// (<see cref="DoorLink"/>), and its space is broadcast as a player's is (<see cref="Broadcast"/>), so clients see it
/// inside the building it is in.
/// </summary>
public partial class InteriorManager
{
    /// <summary>An interior no bot has wanted for this long is freed.</summary>
    private const double BotRoomKeepSeconds = 20;

    /// <summary>How near the sill (metres, along the way through) a bot crosses, from either side.</summary>
    private const float BotCrossReach = 1.0f;

    private sealed class BotRoom
    {
        public required InteriorLayout Layout;
        public Vector3[]? Collision;
        /// <summary>Where a body can walk, in the plan's frame: baked from the collision, so stairs, holes and furniture are in it.</summary>
        public NavigationMesh? Nav;
        public bool Building;
        public double Idle;
        /// <summary>
        /// The collision and the walkable surface in each region a bot wants it in: the body, the navigation region on that
        /// region's map, the placement they were put at, and the physics frames since (a map takes a sync to know it).
        /// </summary>
        public readonly Dictionary<int, Placed> Bodies = new();
    }

    private sealed record Placed(StaticBody3D Body, Rid NavRegion, Rid Map, Transform3D At)
    {
        public int Frames;
    }

    private readonly Dictionary<string, BotRoom> _botRooms = new();
    private readonly List<string> _botRoomsGone = new();

    /// <summary>The frame a bot's body is in: its region's, or this manager's (the main one, or a server without regions).</summary>
    private Core.WorldOrigin OriginOf(FootPlayer bot) => bot.Region is { IsMain: false } r ? r.Origin : Origin!;

    /// <summary>
    /// Server: a bot wants in by <paramref name="door"/>. Plans the building (once), opens the door, and builds the
    /// interior's collision in the bot's region. Call it each thought while heading there; it keeps the interior.
    /// </summary>
    public BotDoorState BotWantIn(FootPlayer bot, string door)
    {
        if (Origin == null || !DoorKey.TryParse(door, out _)) return BotDoorState.Locked;
        var task = GetOrCreate(door);
        if (!task.IsCompleted) return BotDoorState.Pending;
        var layout = task.Result;
        if (layout?.EntranceOf(door) == null) return BotDoorState.Locked;
        if (!Keep(bot, layout)) return BotDoorState.Pending;
        if (!_doors.ContainsKey(door)) BroadcastDoor(door, layout.Key, true);
        return BotDoorState.Ready;
    }

    /// <summary>
    /// Server: keeps the interior of the plan a bot is in or going to solid in its region. False while the collision is
    /// still being made.
    /// </summary>
    private bool Keep(FootPlayer bot, InteriorLayout layout)
    {
        if (!_botRooms.TryGetValue(layout.Key, out var room)) _botRooms[layout.Key] = room = new BotRoom { Layout = layout };
        room.Idle = 0;
        if (room.Collision == null)
        {
            if (!room.Building)
            {
                room.Building = true;
                _ = BuildBotRoom(room);
            }
            return false;
        }
        var region = bot.Region is { IsMain: false } r ? r : null;
        int id = region?.Id ?? 0;
        var placed = PlacementFor(layout, OriginOf(bot));
        if (room.Bodies.TryGetValue(id, out var have) && IsInstanceValid(have.Body))
        {
            // the region re-anchored, or the origin shifted: the interior moves with its frame
            if (have.At != placed)
            {
                have.Body.GlobalTransform = placed;
                NavigationServer3D.RegionSetTransform(have.NavRegion, placed);
                room.Bodies[id] = have with { At = placed, Frames = 0 };
            }
            return true;
        }
        if (have != null) Free(have);
        // BackfaceCollision: every wall is a single face, met from whichever side the body is on (InteriorNode.AddBody)
        var body = new StaticBody3D { Name = $"BotInterior_{layout.Key}_{id}" };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = room.Collision, BackfaceCollision = true } });
        if (region?.Root is { } root) root.AddChild(body);
        else AddChild(body);
        body.GlobalTransform = placed;
        // the walkable surface on that region's navigation map, in the same frame
        var map = (region?.World ?? GetWorld3D()).NavigationMap;
        FitMap(map);
        var nav = NavigationServer3D.RegionCreate();
        NavigationServer3D.RegionSetMap(nav, map);
        if (room.Nav != null) NavigationServer3D.RegionSetNavigationMesh(nav, room.Nav);
        NavigationServer3D.RegionSetTransform(nav, placed);
        room.Bodies[id] = new Placed(body, nav, map, placed);
        GD.Print($"[interior] bots: {layout.Key} solid in region {id} ({room.Collision.Length / 3} triangles, {room.Nav?.GetPolygonCount() ?? 0} walkable polygons)");
        return true;
    }

    private static void Free(Placed p)
    {
        if (IsInstanceValid(p.Body)) p.Body.QueueFree();
        if (p.NavRegion.IsValid) NavigationServer3D.FreeRid(p.NavRegion);
    }

    /// <summary>
    /// The baked surfaces' cell size: a map merges a region's edges only on its own cells. Fine enough that the agent's
    /// radius (<see cref="NavRadius"/>) stays what it says: at 0.1 m it rounded up to 0.4 m and shut every 0.8 m inner door.
    /// </summary>
    private const float NavCell = 0.05f, NavCellHeight = 0.05f;

    /// <summary>
    /// The agent's radius, over the body's (0.32 m): a path is pulled tight round a corner at this distance, and at 0.3 m a
    /// bot walking it straight caught on a doorway's jamb and asked the same way again for ever (#1330).
    /// </summary>
    private const float NavRadius = 0.35f;

    private readonly HashSet<Rid> _fitMaps = new();

    /// <summary>A region's navigation map on the interiors' cells (only bots' interiors use the server's maps).</summary>
    private void FitMap(Rid map)
    {
        if (!_fitMaps.Add(map)) return;
        NavigationServer3D.MapSetCellSize(map, NavCell);
        NavigationServer3D.MapSetCellHeight(map, NavCellHeight);
    }

    /// <summary>
    /// Bakes where a body can walk from an interior's collision faces (on a worker): a body <see cref="NavRadius"/> round and 1.7 m tall,
    /// steps of 0.3 m (a stair's), slopes to 50°. Up to ~2 s for a block; a house is a few hundred milliseconds.
    /// </summary>
    private static NavigationMesh Bake(Vector3[] faces)
    {
        var source = new NavigationMeshSourceGeometryData3D();
        source.AddFaces(faces, Transform3D.Identity);
        var mesh = new NavigationMesh
        {
            CellSize = NavCell, CellHeight = NavCellHeight,
            AgentRadius = NavRadius, AgentHeight = 1.7f, AgentMaxClimb = 0.3f, AgentMaxSlope = 50f,
            GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
        };
        NavigationServer3D.BakeFromSourceGeometryData(mesh, source);
        return mesh;
    }

    /// <summary>
    /// Server: a walkable way from <paramref name="from"/> to <paramref name="to"/> inside the interior a bot is in, in its
    /// frame, round furniture, over stairs and past holes; empty while the surface is not on the map yet or no way joins
    /// them. The last point is the nearest walkable one to <paramref name="to"/>.
    /// </summary>
    public Vector3[] BotPath(FootPlayer bot, Vector3 from, Vector3 to)
    {
        if (bot.InteriorKey is not { } plan || !_botRooms.TryGetValue(plan, out var room)) return [];
        int id = bot.Region is { IsMain: false } r ? r.Id : 0;
        // a map knows a region after its next sync (a physics frame or two)
        if (!room.Bodies.TryGetValue(id, out var p) || p.Frames < 3 || room.Nav == null) return [];
        return NavigationServer3D.MapGetPath(p.Map, from, to, true);
    }

    private async Task BuildBotRoom(BotRoom room)
    {
        try
        {
            var layout = room.Layout;
            (room.Collision, room.Nav) = await Task.Run(() =>
            {
                var faces = InteriorMeshBuilder.Build(layout).Collision;
                return (faces, Bake(faces));
            });
        }
        catch (System.Exception e)
        {
            GD.PushError($"[interior] bots: building {room.Layout.Key} failed: {e}");
            room.Collision = [];
        }
        finally { room.Building = false; }
    }

    /// <summary>
    /// Server: puts a bot through <paramref name="door"/> if it stands at its sill, in or out, as a player's crossing
    /// does (<see cref="Cross"/>): the door's map carries position, heading and momentum. True when it crossed.
    /// </summary>
    public bool BotCross(FootPlayer bot, long id, string door)
    {
        if (Origin == null || !_planKeys.TryGetValue(door, out var plan) || !_cache.TryGetValue(plan, out var layout)
            || layout.EntranceOf(door) is not { } e || !_doors.ContainsKey(door)) return false;
        bool inward = !bot.Indoors;
        if (!inward && bot.InteriorKey != plan) return false;
        if (inward && !Keep(bot, layout)) return false;
        var link = DoorLink.Create(layout, e, OriginOf(bot), null);
        var frame = (inward ? link.Outside : link.Inside).AffineInverse();
        var local = frame * bot.GlobalPosition;
        float half = inward ? link.HalfPass : link.InsideWidth / 2;
        // both frames' Z points out of the building: outside a bot stands at +Z (the street), inside at −Z (the room)
        float z = inward ? local.Z : -local.Z;
        if (z < -0.3f || z > BotCrossReach || Mathf.Abs(local.X) > Mathf.Max(half, 0.2f) || Mathf.Abs(local.Y) > 1.2f) return false;
        // just over the sill on the other side, on its floor, never in the slab: into the room (−Z), or onto the street (+Z)
        var to = inward ? link.Inside : link.Outside;
        var at = to * new Vector3(local.X, 0.05f, inward ? -0.4f : 0.4f);
        var map = inward ? link.ToInside : link.ToOutside;
        var x = map.Basis.X;
        bot.CrossDoor(inward ? plan : null, at, Mathf.Atan2(-x.Z, x.X), map.Basis);
        Broadcast(id, inward ? plan : "");
        GD.Print($"[interior] bots: {bot.Name} went {(inward ? "in" : "out")} through {door}");
        return true;
    }

    /// <summary>Server: the plan a bot is inside, and where it stands in the bot's frame; null outdoors or unknown.</summary>
    public (InteriorLayout Layout, Transform3D Placement)? BotInterior(FootPlayer bot)
    {
        if (Origin == null || bot.InteriorKey is not { } plan || !_cache.TryGetValue(plan, out var layout)) return null;
        Keep(bot, layout);
        return (layout, PlacementFor(layout, OriginOf(bot)));
    }

    /// <summary>Server: the door frames of a plan's entrance in a bot's frame (outside: on the facade; inside: the doorway).</summary>
    public DoorLink? BotDoor(FootPlayer bot, string door) =>
        Origin != null && _planKeys.TryGetValue(door, out var plan) && _cache.TryGetValue(plan, out var layout) && layout.EntranceOf(door) is { } e
            ? DoorLink.Create(layout, e, OriginOf(bot), null) : null;

    /// <summary>Server: the interiors bots are in stay; the others are freed after <see cref="BotRoomKeepSeconds"/>.</summary>
    private void TickBotRooms(double delta)
    {
        if (_botRooms.Count == 0) return;
        if (Players != null)
            foreach (var n in Players.GetChildren())
                if (n is FootPlayer { Bot: true, InteriorKey: { } plan } && _botRooms.TryGetValue(plan, out var held)) held.Idle = 0;
        _botRoomsGone.Clear();
        foreach (var room in _botRooms.Values)
            foreach (var placed in room.Bodies.Values) placed.Frames++;
        foreach (var (plan, room) in _botRooms)
        {
            room.Idle += delta;
            if (room.Idle < BotRoomKeepSeconds || room.Building) continue;
            foreach (var placed in room.Bodies.Values) Free(placed);
            _botRoomsGone.Add(plan);
        }
        foreach (var plan in _botRoomsGone)
        {
            _botRooms.Remove(plan);
            GD.Print($"[interior] bots: {plan} freed (no bot for {BotRoomKeepSeconds:F0} s)");
        }
    }

    private readonly Dictionary<TileId, Task<DoorSpot[]>> _botTileDoors = new();

    /// <summary>
    /// Server: the street doors within <paramref name="radius"/> of a bot, in its frame: key, sill and outward. The
    /// server's <see cref="DoorIndex"/> is empty (it builds no building meshes, and one index cannot hold every region's
    /// frame), so the doors come from the tiles' own data as a client's tile build makes them, on the terrain grid, once a
    /// tile; one with no doorway inside is still listed (<see cref="BotWantIn"/> answers Locked). Empty while a tile's are
    /// being worked out. The flat world's tiles only: under <c>--frame earth</c> a leaf's buildings are on stand-in tiles
    /// this does not find yet.
    /// </summary>
    public List<(string Door, Vector3 At, Vector3 Outward)> BotDoorsNear(FootPlayer bot, float radius)
    {
        var found = new List<(string, Vector3, Vector3)>();
        if (Source == null) return found;
        var origin = OriginOf(bot);
        var lv = bot.Global.Lv95;
        var centre = TileId.FromLv95(lv.E, lv.N);
        var pos = bot.GlobalPosition;
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                var id = centre.Neighbour(de, dn);
                if (!_botTileDoors.TryGetValue(id, out var task)) _botTileDoors[id] = task = DoorsOf(Source, id);
                if (!task.IsCompletedSuccessfully) continue;
                var xf = origin.TileTransform(id);
                foreach (var d in task.Result)
                {
                    // a building site's office (#822) is containers up stairs: not a bot's visit yet
                    if (d.Width <= 0 || d.Level != 0 || d.Vehicle || d.Kind == BuildingKind.UnderConstruction) continue;
                    var at = xf * d.Position;
                    if (new Vector2(at.X - pos.X, at.Z - pos.Z).Length() > radius) continue;
                    found.Add((new DoorKey(id.E, id.N, d.Index, d.Slot).ToString(), at, (xf.Basis * d.Outward).Normalized()));
                }
            }
        return found;
    }

    private static Task<DoorSpot[]> DoorsOf(Terrain.IChunkSource source, TileId id) => Task.Run(async () =>
    {
        var tile = await source.LoadBuildingsAsync(id);
        if (tile == null) return System.Array.Empty<DoorSpot>();
        var roads = await source.LoadRoadsAsync(id);
        var grid = await source.LoadChunkAsync(id);
        return BuildingFootprint.ComputeDoors(tile, roads, grid);
    });

    /// <summary>How many interiors are solid for bots, and in how many regions (for checks).</summary>
    public (int Plans, int Bodies) BotRoomCounts()
    {
        int bodies = 0;
        foreach (var room in _botRooms.Values) bodies += room.Bodies.Count;
        return (_botRooms.Count, bodies);
    }
}
