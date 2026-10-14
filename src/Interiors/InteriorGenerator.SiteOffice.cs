using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A building site's interior (#822): its site office, the containers by the gate
/// (<see cref="SiteOffices"/>), and its toilet cabins, not its half-built shell, which is walked
/// into in the world (#608).
///
/// <para>
/// Every room stands in the plan where its container or cabin stands in the yard, in a frame
/// square to the containers (the yard is square to the site): the plan box is the bounding box of
/// them all, so each door's doorway is where the door is and every <see cref="DoorLink"/> is a
/// plain drop. The office is a room per container: the office (<see cref="RoomType.SiteOffice"/>),
/// and beside it the mess room, which is the changing room too (<see cref="RoomType.BreakRoom"/>
/// with the site's own pieces, <see cref="SiteMess"/>), joined by a doorway at the back. Each cabin
/// is a <see cref="RoomType.WC"/> reached only through its own door: a seat, a urinal, paper, a
/// mirror and a hook. The upper containers are not inside yet: they have no stair (see the
/// construction-sites note).
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>A container's inside height: its outside (its floor is the ground's).</summary>
    public const float OfficeStorey = SiteOffices.ContainerHeight;

    /// <summary>A cabin's inside height: its roof less its shell.</summary>
    private const float CabinClear = SiteOffices.CabinHeight - 0.1f;

    private static InteriorLayout? SiteOffice(BuildingTile tile, int index, RoadTile? roads, ChunkGrid? grid)
    {
        if (SitePlans.Of(tile, roads, index) is not { } site
            || SiteShellBuilder.Office(tile, site, grid) is not { } office || office.Doors.Count == 0) return null;
        var spots = SiteShellBuilder.OfficeDoors(office, site, index);
        return SiteOffice(tile.Id, index, tile.Buildings[index], site, office, spots);
    }

    /// <summary>The site office's plan, from the office its yard draws and the doors its tile indexes.</summary>
    internal static InteriorLayout SiteOffice(TileId tile, int index, Building b, ConstructionSite site, SiteOffice office, DoorSpot[] spots)
    {
        var key = new BuildingKey(tile.E, tile.N, index);
        var rng = new Random(StableHash(key.ToString()));
        float hw = office.Width / 2, hd = office.Depth / 2;

        // the cabins in the office's interior frame (square to it: the yard is square to the site),
        // and the plan box round them all, its middle the plan's origin
        var cabins = new List<RectPlan>();
        float bx0 = -hw, bz0 = -hd, bx1 = hw, bz1 = hd;
        foreach (var t in office.Toilets)
        {
            var a = office.ToInterior(t.Min);
            var c = office.ToInterior(t.Max);
            var r = new RectPlan(Math.Min(a.X, c.X), Math.Min(a.Y, c.Y), Math.Max(a.X, c.X), Math.Max(a.Y, c.Y));
            cabins.Add(r);
            bx0 = Math.Min(bx0, r.X0); bz0 = Math.Min(bz0, r.Z0);
            bx1 = Math.Max(bx1, r.X1); bz1 = Math.Max(bz1, r.Z1);
        }
        float sx = (bx0 + bx1) / 2, sz = (bz0 + bz1) / 2;
        // a point of the office's interior frame in the plan's, and a door's place in it
        Vector2 Plan(Vector2 p) => new(p.X - sx, p.Y - sz);
        Vector2 DoorAt(SiteOfficeDoor d) => Plan(office.ToInterior(new Vector2(d.Sill.X, d.Sill.Z)));
        var origin = office.Center + office.Across * sx - office.Outward * sz;
        var center = SiteShellBuilder.ToTile(site, new Vector3(origin.X, 0, origin.Y));
        var axisU = SiteShellBuilder.ToTileDir(site, office.Across);

        var main = spots[0];
        var layout = new InteriorLayout
        {
            Key = key.ToString(),
            Kind = BuildingKind.UnderConstruction,
            TriangleCount = b.TriangleCount,
            MinY = b.MinY,
            MaxY = b.MaxY,
            CenterX = center.X,
            CenterZ = center.Z,
            Yaw = Mathf.Atan2(-axisU.Y, axisU.X),
            Width = bx1 - bx0,
            Depth = bz1 - bz0,
            DoorX = main.Position.X,
            DoorY = main.Position.Y,
            DoorZ = main.Position.Z,
            DoorOutX = main.Outward.X,
            DoorOutZ = main.Outward.Z,
            DoorWidth = main.Width,
            DoorHeight = main.Height,
            StoreyHeight = OfficeStorey,
            EntryX = DoorAt(office.Doors[0]).X,
            EntryWidth = main.Width,
        };

        var floor = new FloorPlan();
        for (int i = 0; i < office.Rooms.Count; i++)
        {
            var (x0, x1) = office.Rooms[i];
            var room = new RoomPlan
            {
                X0 = x0 - sx, Z0 = -hd - sz, X1 = x1 - sx, Z1 = hd - sz, Type = i == 0 ? RoomType.SiteOffice : RoomType.BreakRoom,
            };
            // the windows the container has on its long faces, where they face out
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                bool outside = side == Side.Left ? x0 <= -hw + 0.02f : x1 >= hw - 0.02f;
                if (!outside) continue;
                foreach (float z in office.WindowZ)
                    room.Openings.Add(new OpeningPlan
                    {
                        Side = side, Center = z - sz, Width = SiteOffices.WindowLength,
                        Bottom = SiteOffices.WindowBottom, Top = SiteOffices.WindowTop, Kind = OpeningKind.Window,
                    });
            }
            floor.Rooms.Add(room);
        }
        // the two containers are coupled: a doorway between them at the back, clear of the windows
        for (int i = 0; i + 1 < floor.Rooms.Count; i++)
        {
            var (a, c) = floor.Rooms[i].X1 <= floor.Rooms[i + 1].X0 + 0.01f ? (i, i + 1) : (i + 1, i);
            float z = hd - 1.0f - sz;
            floor.Rooms[a].Openings.Add(new OpeningPlan { Side = Side.Right, Center = z, Width = InnerDoor, Bottom = 0, Top = 2.0f, Kind = OpeningKind.Door, Other = c });
            floor.Rooms[c].Openings.Add(new OpeningPlan { Side = Side.Left, Center = z, Width = InnerDoor, Bottom = 0, Top = 2.0f, Kind = OpeningKind.Door, Other = a });
        }
        // a doorway per container door, in the room behind it, on the front wall
        int containers = office.ContainerDoors;
        for (int i = 0; i < containers; i++)
        {
            var spot = spots[i];
            float x = DoorAt(office.Doors[i]).X;
            var room = floor.Rooms.FirstOrDefault(r => x > r.X0 && x < r.X1) ?? floor.Rooms[Math.Min(i, floor.Rooms.Count - 1)];
            float top = Math.Min(spot.Height, OfficeStorey - Slab - 0.15f);
            room.Openings.Add(new OpeningPlan { Side = Side.Front, Center = x, Width = spot.Width, Bottom = 0, Top = top, Kind = OpeningKind.Entry });
            layout.Entrances.Add(Entrance(spot, tile, new Vector2(x, room.Z0), Side.Front, top));
        }
        layout.Floors.Add(floor);
        Furnish(layout, rng);

        // the cabins: rooms of their own, each opening only on its own door, furnished by hand (a
        // metre square leaves no room for the furnisher's clearances)
        for (int i = 0; i < office.Toilets.Count; i++)
        {
            var t = office.Toilets[i];
            var r = cabins[i];
            var room = new RoomPlan { X0 = r.X0 - sx, Z0 = r.Z0 - sz, X1 = r.X1 - sx, Z1 = r.Z1 - sz, Type = RoomType.WC, Clear = CabinClear };
            var spot = spots[containers + i];
            var at = DoorAt(t.Door);
            var o = new Vector2(t.Door.Outward.Dot(office.Across), -t.Door.Outward.Dot(office.Outward));
            var side = Math.Abs(o.X) > Math.Abs(o.Y) ? (o.X > 0 ? Side.Right : Side.Left) : (o.Y > 0 ? Side.Back : Side.Front);
            // the doorway on the cabin's wall line, where the door is along it
            var line = side switch
            {
                Side.Front => new Vector2(at.X, room.Z0),
                Side.Back => new Vector2(at.X, room.Z1),
                Side.Left => new Vector2(room.X0, at.Y),
                _ => new Vector2(room.X1, at.Y),
            };
            float top = Math.Min(spot.Height, CabinClear - 0.1f);
            room.Openings.Add(new OpeningPlan
            {
                Side = side, Center = side is Side.Front or Side.Back ? line.X : line.Y, Width = spot.Width, Bottom = 0, Top = top, Kind = OpeningKind.Entry,
            });
            floor.Rooms.Add(room);
            layout.Entrances.Add(Entrance(spot, tile, line, side, top));
            FurnishCabin(layout, room, side, (int)(StableHash($"{key}|cabin{i}") & 1) == 0);
        }
        return layout;
    }

    /// <summary>An entrance on a room's wall: the doorway at <paramref name="line"/>, into the room from <paramref name="side"/>.</summary>
    private static EntrancePlan Entrance(DoorSpot spot, TileId tile, Vector2 line, Side side, float top)
    {
        var (fx, fz) = Facing(side);
        return new EntrancePlan
        {
            Door = spot.KeyIn(tile).ToString(),
            X = line.X, Z = line.Y, InX = -fx, InZ = -fz, Width = spot.Width,
            DoorX = spot.Position.X, DoorY = spot.Position.Y, DoorZ = spot.Position.Z,
            DoorOutX = spot.Outward.X, DoorOutZ = spot.Outward.Z,
            DoorWidth = spot.Width, DoorHeight = top, Hang = spot.Hang, Vehicle = false,
        };
    }

    /// <summary>The quarter turns of a piece whose back is against a wall: as the furnisher turns them.</summary>
    private static int TurnsAgainst(Side wall) => wall switch { Side.Front => 0, Side.Left => 1, Side.Back => 2, _ => 3 };

    /// <summary>
    /// A toilet cabin's inside (#822): the seat against the back wall to one side, the urinal beside
    /// it, the paper on the side wall by the seat, a mirror and a coat hook on the other side wall
    /// above head height. Everything below 1.5 m stays out of the doorway's 0.6 m (the validator's).
    /// </summary>
    private static void FurnishCabin(InteriorLayout l, RoomPlan r, Side door, bool seatRight)
    {
        var back = Opposite(door);
        // the room's own terms: "along" the back wall, from its middle, toward the seat's side
        // wall; "in" from the back wall towards the door
        var (bx, bz) = Facing(back);
        var alongDir = new Vector2(-bz, bx) * (seatRight ? 1f : -1f);
        var seatWall = alongDir.X > 0.5f ? Side.Right : alongDir.X < -0.5f ? Side.Left : alongDir.Y > 0.5f ? Side.Back : Side.Front;
        var otherWall = Opposite(seatWall);
        var mid = new Vector2((r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2);
        var inDir = new Vector2(-bx, -bz);
        float half = (back is Side.Front or Side.Back ? r.Width : r.Depth) / 2 - WallInset - 0.02f;
        float deep = (back is Side.Front or Side.Back ? r.Depth : r.Width) / 2 - WallInset - 0.02f;
        // a piece w along its wall, d out of it, its middle at (along, in) from the back wall
        void Put(FurnitureType type, Side wall, float along, float inFromBack, float w, float d, float h, float lift = 0f)
        {
            var c = mid + alongDir * along + inDir * (inFromBack - deep);
            l.Furniture.Add(new FurniturePlan { Type = type, Floor = l.Below, X = c.X, Z = c.Y, Turns = TurnsAgainst(wall), W = w, D = d, H = h, Lift = lift });
        }
        Put(FurnitureType.Toilet, back, half - 0.25f, 0.21f, 0.4f, 0.42f, 0.75f);
        Put(FurnitureType.Urinal, back, -half + 0.19f, 0.15f, 0.34f, 0.3f, 0.7f);
        Put(FurnitureType.PaperRoll, seatWall, half - 0.025f, 0.3f, 0.14f, 0.05f, 0.14f, 0.7f);
        Put(FurnitureType.Mirror, otherWall, -half + 0.012f, deep, 0.54f, 0.025f, 0.64f, 1.3f);
        Put(FurnitureType.CoatHook, otherWall, -half + 0.03f, deep + 0.3f, 0.1f, 0.06f, 0.12f, 1.62f);
    }
}
