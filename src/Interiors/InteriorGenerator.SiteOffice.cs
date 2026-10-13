using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A building site's interior (#822): its site office, the containers by the gate
/// (<see cref="SiteOffices"/>), not its half-built shell, which is walked into in the world (#608).
///
/// <para>
/// The plan box is the ground row of containers, its front wall their door end, so each door's
/// doorway stands where the container's door is and the <see cref="DoorLink"/> between them is a
/// plain drop. One room per container: the office (<see cref="RoomType.SiteOffice"/>), and beside
/// it the mess room, which is the changing room too (<see cref="RoomType.BreakRoom"/> with the
/// site's lockers, <see cref="KindExtras"/>), joined by a doorway at the back. Each container has
/// its own door (the site building's slots 0 and 1), the windows where the containers have
/// theirs. The upper containers are not inside yet: they have no stair (see the construction-sites note).
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>A container's inside height: its outside, less nothing (its floor is the ground's).</summary>
    public const float OfficeStorey = SiteOffices.ContainerHeight;

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
        var center = SiteShellBuilder.ToTile(site, new Vector3(office.Center.X, 0, office.Center.Y));
        var axisU = SiteShellBuilder.ToTileDir(site, office.Across);
        float hw = office.Width / 2, hd = office.Depth / 2;
        // a door's place across the front wall, interior X
        float DoorX(SiteOfficeDoor d) => office.ToInterior(new Vector2(d.Sill.X, d.Sill.Z)).X;

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
            Width = office.Width,
            Depth = office.Depth,
            DoorX = main.Position.X,
            DoorY = main.Position.Y,
            DoorZ = main.Position.Z,
            DoorOutX = main.Outward.X,
            DoorOutZ = main.Outward.Z,
            DoorWidth = main.Width,
            DoorHeight = main.Height,
            StoreyHeight = OfficeStorey,
            EntryX = DoorX(office.Doors[0]),
            EntryWidth = main.Width,
        };

        var floor = new FloorPlan();
        for (int i = 0; i < office.Rooms.Count; i++)
        {
            var (x0, x1) = office.Rooms[i];
            var room = new RoomPlan { X0 = x0, Z0 = -hd, X1 = x1, Z1 = hd, Type = i == 0 ? RoomType.SiteOffice : RoomType.BreakRoom };
            // the windows the container has on its long faces, where they face out
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                bool outside = side == Side.Left ? x0 <= -hw + 0.02f : x1 >= hw - 0.02f;
                if (!outside) continue;
                foreach (float z in office.WindowZ)
                    room.Openings.Add(new OpeningPlan
                    {
                        Side = side, Center = z, Width = SiteOffices.WindowLength,
                        Bottom = SiteOffices.WindowBottom, Top = SiteOffices.WindowTop, Kind = OpeningKind.Window,
                    });
            }
            floor.Rooms.Add(room);
        }
        // the two containers are coupled: a doorway between them at the back, clear of the windows
        for (int i = 0; i + 1 < floor.Rooms.Count; i++)
        {
            var (a, c) = floor.Rooms[i].X1 <= floor.Rooms[i + 1].X0 + 0.01f ? (i, i + 1) : (i + 1, i);
            float z = hd - 1.0f;
            floor.Rooms[a].Openings.Add(new OpeningPlan { Side = Side.Right, Center = z, Width = InnerDoor, Bottom = 0, Top = 2.0f, Kind = OpeningKind.Door, Other = c });
            floor.Rooms[c].Openings.Add(new OpeningPlan { Side = Side.Left, Center = z, Width = InnerDoor, Bottom = 0, Top = 2.0f, Kind = OpeningKind.Door, Other = a });
        }
        // a doorway per container door, in the room behind it, on the front wall
        for (int i = 0; i < office.Doors.Count; i++)
        {
            var d = office.Doors[i];
            var spot = spots[i];
            float x = DoorX(d);
            var room = floor.Rooms.FirstOrDefault(r => x > r.X0 && x < r.X1) ?? floor.Rooms[Math.Min(i, floor.Rooms.Count - 1)];
            float top = Math.Min(spot.Height, OfficeStorey - Slab - 0.15f);
            room.Openings.Add(new OpeningPlan
            {
                Side = Side.Front, Center = x, Width = spot.Width, Bottom = 0, Top = top, Kind = OpeningKind.Entry,
            });
            layout.Entrances.Add(new EntrancePlan
            {
                Door = spot.KeyIn(tile).ToString(),
                X = x, Z = -hd, InX = 0, InZ = 1, Width = spot.Width,
                DoorX = spot.Position.X, DoorY = spot.Position.Y, DoorZ = spot.Position.Z,
                DoorOutX = spot.Outward.X, DoorOutZ = spot.Outward.Z,
                DoorWidth = spot.Width, DoorHeight = top, Hang = spot.Hang, Vehicle = false,
            });
        }
        layout.Floors.Add(floor);
        Furnish(layout, rng);
        return layout;
    }
}
