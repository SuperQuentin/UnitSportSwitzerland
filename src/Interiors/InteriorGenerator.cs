using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Plans a building's inside from its outside: footprint box, storey count, kind.
///
/// <para>
/// Interior frame: X across the front, Z from the front wall (−Depth/2, where the door is) to
/// the back (+Depth/2), Y up from the ground floor. Multi-storey buildings get a <b>core</b>:
/// a strip across the plan, centred on the door, holding the hall and a switchback stair of two
/// lanes plus a walkway, so every flight starts where the previous one ended and every floor's
/// landings are reachable. Rooms fill the two sides of the core by treemap splitting of a
/// program (living, kitchen, bedrooms...) that depends on the kind. Doorways come from a
/// spanning tree over room adjacency, so every room is reachable by construction.
/// </para>
///
/// <para>
/// Pure function of its inputs plus a seed from the key, but the result is stored rather than
/// regenerated, so changing this code never changes a house somebody has already walked into.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    public const float Slab = 0.2f;
    public const float WallInset = 0.06f;
    private const float InnerDoor = 0.9f;
    private const float LaneWidth = 1.0f;
    private const float WalkWidth = 1.2f;
    private const float Landing = 1.0f;
    private const int MaxFloors = 30;

    public static InteriorLayout Generate(Footprint fp, Building b)
    {
        var rng = new Random(StableHash(fp.Key.ToString()));
        var (h, n) = Storeys(b);

        var layout = new InteriorLayout
        {
            Key = fp.Key.ToString(),
            Kind = b.Kind,
            TriangleCount = b.TriangleCount,
            MinY = b.MinY,
            MaxY = b.MaxY,
            CenterX = fp.Center.X,
            CenterZ = fp.Center.Y,
            Yaw = fp.Yaw,
            Width = fp.Width,
            Depth = fp.Depth,
            DoorX = fp.Door.Position.X,
            DoorY = fp.Door.Position.Y,
            DoorZ = fp.Door.Position.Z,
            DoorOutX = fp.Door.Outward.X,
            DoorOutZ = fp.Door.Outward.Z,
            DoorWidth = fp.Door.Width,
            StoreyHeight = h,
            EntryX = fp.EntryX,
            // a barn's or a garage's hall opens as wide as its door (a vehicle drives through);
            // elsewhere a door leads into a hall or a core
            EntryWidth = BuildingFootprint.VehicleDoor(b.Kind) ? fp.Door.Width : Math.Min(fp.Door.Width, 1.8f),
        };

        bool bank = BuildingFootprint.IsBank(fp);
        if (bank) layout.Type = BuildingType.Bank;

        bool single = b.Kind is BuildingKind.Industrial or BuildingKind.Agricultural or BuildingKind.Annex
            or BuildingKind.Garage
            or BuildingKind.UnderConstruction or BuildingKind.Sacral
            || fp.Width < 4.5f || fp.Depth < 4.5f || fp.Width * fp.Depth < 25f;

        int below = single ? 0 : Cellars(layout.Key, b.Kind, n);
        if (single || !TryCored(layout, fp, b.Kind, n, below, bank, rng)
            && (below == 0 || !TryCored(layout, fp, b.Kind, n, 0, bank, rng)))
        {
            layout.Below = 0;
            SingleRoom(layout, b.Kind, fp.Door.Height, rng);
            if (bank) layout.Floors[0].Rooms[0].Type = RoomType.BankHall;
        }

        Furnish(layout, rng);
        return layout;
    }

    /// <summary>
    /// Floor count and height. Uses the facade's own storeys where it has windows, so the inside
    /// matches the outside; kinds drawn without windows get one tall floor. Never below 2.7 m —
    /// the facade can afford a squat storey, a player walking under it cannot.
    /// </summary>
    public static (float Height, int Count) Storeys(Building b)
    {
        float wall = b.MaxY - b.MinY;
        var (height, count) = BuildingMeshBuilder.Storeys(b);
        if (b.Kind == BuildingKind.Sacral)
            return (Math.Clamp(wall * 0.8f, 4.5f, 16f), 1);
        if (count <= 0)
            return b.Kind is BuildingKind.Industrial or BuildingKind.Agricultural
                ? (Math.Clamp(wall * 0.78f, 3.5f, 12f), 1)
                : (Math.Clamp(wall * 0.78f, 2.7f, 4f), 1);
        return (Math.Max(2.7f, height), Math.Min(count, MaxFloors));
    }

    /// <summary>
    /// Whether a building gets a cellar under its ground floor (#213), from its own seed so the
    /// rest of the plan does not shift. Most Swiss homes have one, and in it, often, the
    /// civil-defence shelter the law asked of houses built from the 1960s.
    /// </summary>
    private static int Cellars(string key, BuildingKind kind, int floors)
    {
        double chance = kind switch
        {
            BuildingKind.House => 0.65,
            BuildingKind.Apartment => 0.85,
            BuildingKind.Other => floors <= 3 ? 0.5 : 0.7,
            _ => 0,
        };
        return new Random(StableHash(key + "|cellar")).NextDouble() < chance ? 1 : 0;
    }

    // ---- single room -------------------------------------------------------------------

    private static void SingleRoom(InteriorLayout l, BuildingKind kind, float doorHeight, Random rng)
    {
        l.Floors.Clear();
        float hw = l.Width / 2, hd = l.Depth / 2;
        var type = kind switch
        {
            BuildingKind.Sacral => RoomType.Nave,
            BuildingKind.Industrial => RoomType.Workshop,
            BuildingKind.Agricultural => RoomType.Barn,
            BuildingKind.Annex => l.Width * l.Depth > 16 ? RoomType.Garage : RoomType.Storage,
            BuildingKind.Garage => RoomType.Garage,
            BuildingKind.UnderConstruction => RoomType.Storage,
            BuildingKind.Commercial => RoomType.Shop,
            BuildingKind.House or BuildingKind.Apartment => RoomType.Living,
            _ => RoomType.Storage,
        };
        var room = new RoomPlan { X0 = -hw, Z0 = -hd, X1 = hw, Z1 = hd, Type = type };
        float clear = l.StoreyHeight - Slab;
        l.EntryWidth = Math.Min(l.EntryWidth, l.Width - 0.6f);
        l.EntryX = Fit(l.EntryX, -hw + l.EntryWidth / 2 + 0.2f, hw - l.EntryWidth / 2 - 0.2f);
        room.Openings.Add(new OpeningPlan
        {
            Side = Side.Front, Center = l.EntryX, Width = l.EntryWidth, Bottom = 0,
            // a barn's or a garage's as tall as its facade door, which the footprint kept under the eave
            Top = BuildingFootprint.VehicleDoor(kind) ? Math.Min(doorHeight, BuildingFootprint.DoorHeightFor(kind, clear))
                : Math.Min(kind == BuildingKind.Industrial ? 2.8f : 2.1f, clear - 0.15f),
            Kind = OpeningKind.Entry,
        });
        var floor = new FloorPlan();
        floor.Rooms.Add(room);
        l.Floors.Add(floor);
        AddWindows(l, floor, 0);
    }

    // ---- cored plan ----------------------------------------------------------------------

    private sealed record Item(RoomType Type, float Weight);

    private static bool TryCored(InteriorLayout l, Footprint fp, BuildingKind kind, int above, int below, bool bank, Random rng)
    {
        float W = l.Width, D = l.Depth, h = l.StoreyHeight;
        float hw = W / 2, hd = D / 2;
        // every floor's stair is the same flight, so a cellar is one more floor at the bottom of
        // the stack: index f stands at level f - below
        int floors = above + below;

        // stair geometry; steepen before giving up, and give up by dropping to one floor
        float run = 0, zs0 = 0;
        int steps = (int)MathF.Ceiling(h / 0.19f);
        if (floors > 1)
        {
            float room = D - 1.6f - 2 * Landing;
            float tread = Math.Min(0.27f, room / steps);
            if (tread < 0.2f) { floors = 1; below = 0; }
            else
            {
                run = tread * steps;
                zs0 = hd - (run + 2 * Landing);
            }
        }

        l.Below = below;
        float coreW = floors > 1 ? 2 * LaneWidth + WalkWidth : 2.2f;
        if (W < coreW + 1.0f) return false;

        // centre the core on the door, but never leave a side strip too thin to be a room
        float cx = Fit(l.EntryX, -hw + coreW / 2, hw - coreW / 2);
        if (cx - coreW / 2 - -hw < 2.4f) cx = -hw + coreW / 2;
        if (hw - (cx + coreW / 2) < 2.4f) cx = hw - coreW / 2;
        // both snapped: the building is too narrow for rooms beside a core
        if (cx - coreW / 2 <= -hw + 0.01f && cx + coreW / 2 >= hw - 0.01f) return false;
        float c0 = cx - coreW / 2, c1 = cx + coreW / 2;

        float dw = Math.Min(l.EntryWidth, coreW - 0.4f);
        l.EntryWidth = dw;
        // the walkway side is where the door goes, clear of the first flight's lane
        l.EntryX = floors > 1
            ? Fit(c1 - WalkWidth / 2, c0 + dw / 2 + 0.2f, c1 - dw / 2 - 0.2f)
            : Fit(l.EntryX, c0 + dw / 2 + 0.2f, c1 - dw / 2 - 0.2f);

        float laneA0 = c0, laneA1 = c0 + LaneWidth, laneB1 = c0 + 2 * LaneWidth;
        float runZ0 = zs0 + Landing, runZ1 = zs0 + Landing + run;

        bool residential = kind is BuildingKind.House
            || kind == BuildingKind.Other && floors <= 3 && W * D < 200;
        bool apartment = kind == BuildingKind.Apartment || kind == BuildingKind.Other && !residential;

        float clear = h - Slab;
        l.Floors.Clear();
        for (int f = 0; f < floors; f++)
        {
            int level = f - below;
            var floor = new FloorPlan();
            var core = new RoomPlan
            {
                X0 = c0, Z0 = -hd, X1 = c1, Z1 = hd,
                Type = level == 0 ? (apartment || kind is BuildingKind.Commercial or BuildingKind.Civic ? RoomType.Lobby : RoomType.Hall)
                    : RoomType.Landing,
            };
            floor.Rooms.Add(core);
            if (level == 0)
                core.Openings.Add(new OpeningPlan
                {
                    Side = Side.Front, Center = l.EntryX, Width = dw, Bottom = 0,
                    Top = Math.Min(2.2f, clear - 0.15f), Kind = OpeningKind.Entry,
                });

            if (floors > 1)
            {
                if (f < floors - 1)
                    floor.Flight = f % 2 == 0
                        ? new FlightPlan { X0 = laneA0, X1 = laneA1, ZBottom = runZ0, ZTop = runZ1 }
                        : new FlightPlan { X0 = laneA1, X1 = laneB1, ZBottom = runZ1, ZTop = runZ0 };
                if (f > 0)
                {
                    bool holeA = (f - 1) % 2 == 0;
                    floor.Holes.Add(holeA
                        ? new RectPlan(laneA0, runZ0, laneA1, runZ1)
                        : new RectPlan(laneA1, runZ0, laneB1, runZ1));
                    floor.Rails.Add(new RectPlan(laneA1, runZ0, laneA1, runZ1));
                    if (!holeA) floor.Rails.Add(new RectPlan(laneB1, runZ0, laneB1, runZ1));
                }
            }

            // where on each long side of the core a door may open: not beside a stair run,
            // except on the walkway side, which is floor all the way along
            var walkLeft = floors > 1
                ? new List<(float, float)> { (-hd, runZ0), (runZ1, hd) }
                : new List<(float, float)> { (-hd, hd) };
            var walkRight = new List<(float, float)> { (-hd, hd) };

            int unitId = 0;
            var sides = new List<RectPlan>();
            if (c0 - -hw >= 2.0f) sides.Add(new RectPlan(-hw, -hd, c0, hd));
            if (hw - c1 >= 2.0f) sides.Add(new RectPlan(c1, -hd, hw, hd));
            // a core with no room beside it is just a corridor: the single-room plan does better
            if (sides.Count == 0) return false;
            float sideArea = sides.Sum(s => (s.X1 - s.X0) * (s.Z1 - s.Z0));

            if (residential || level < 0 || bank && level == 0)
            {
                var program = level < 0 ? CellarProgram(apartment, sideArea, rng)
                    : bank ? BankProgram(sideArea)
                    : HouseProgram(level, above, sideArea, rng);
                // hand each side a share of the program by area, biggest rooms first
                var shares = sides.Select(_ => new List<Item>()).ToList();
                var load = new float[sides.Count];
                float totalW = program.Sum(p => p.Weight);
                foreach (var item in program.OrderByDescending(p => p.Weight))
                {
                    int best = 0;
                    float bestSlack = float.MinValue;
                    for (int s = 0; s < sides.Count; s++)
                    {
                        float area = (sides[s].X1 - sides[s].X0) * (sides[s].Z1 - sides[s].Z0);
                        float slack = area / sideArea - load[s] / totalW;
                        if (slack > bestSlack) { bestSlack = slack; best = s; }
                    }
                    shares[best].Add(item);
                    load[best] += item.Weight;
                }
                // a bank keeps its vault behind the banking hall, on the same side, so it opens
                // off the hall; the office and the WC take the other side
                if (bank && level == 0 && sides.Count > 1)
                {
                    int hall = sides.Select((r, i) => (Area: (r.X1 - r.X0) * (r.Z1 - r.Z0), i)).MaxBy(x => x.Area).i;
                    for (int s = 0; s < sides.Count; s++) shares[s].Clear();
                    foreach (var item in program)
                        shares[item.Type is RoomType.BankHall or RoomType.Vault ? hall : 1 - hall].Add(item);
                }
                for (int s = 0; s < sides.Count; s++)
                {
                    if (shares[s].Count == 0)
                        shares[s].Add(new Item(level < 0 ? RoomType.Cellar : bank ? RoomType.Office : level == 0 ? RoomType.Living : RoomType.Bedroom, 1));
                    // street end first, the deep rooms (shelter, vault, carnotzet) at the back
                    var ordered = shares[s].OrderBy(i => bank && level == 0 && i.Type is RoomType.Office or RoomType.WC ? -2 : Depth(i.Type)).ToList();
                    foreach (var r in Treemap(sides[s], ordered)) { r.Unit = -1; floor.Rooms.Add(r); }
                }
            }
            else
            {
                foreach (var side in sides)
                {
                    float sd = side.Z1 - side.Z0;
                    int slices = Math.Clamp((int)MathF.Round(sd / 10f), 1, 4);
                    while (slices > 1 && sd / slices < 5f) slices--;
                    for (int k = 0; k < slices; k++)
                    {
                        var unit = new RectPlan(side.X0, side.Z0 + sd * k / slices, side.X1, side.Z0 + sd * (k + 1) / slices);
                        float area = (unit.X1 - unit.X0) * (unit.Z1 - unit.Z0);
                        var program = UnitProgram(kind, apartment, level, area, rng);
                        foreach (var r in Treemap(unit, program)) { r.Unit = unitId; floor.Rooms.Add(r); }
                        unitId++;
                    }
                }
            }

            if (!Connect(floor, walkLeft, walkRight, c0, c1, clear)) return false;
            if (level >= 0) AddWindows(l, floor, level);
            l.Floors.Add(floor);
        }
        return true;
    }

    /// <summary>Where a room sits along a side, street end (low) to back (high): the treemap fills in this order.</summary>
    private static int Depth(RoomType t) => t switch
    {
        RoomType.Laundry or RoomType.Cellar or RoomType.BankHall => -1,
        RoomType.Shelter or RoomType.Vault or RoomType.Carnotzet => 1,
        _ => 0,
    };

    /// <summary>
    /// A cellar (#213). A house's mixes what Swiss basements hold: the laundry, then a few of a
    /// guest room, a home cinema, a carnotzet (the wine cellar you sit in), a music room and a
    /// storage cellar, and often the shelter with its blast door. A block of flats has the shared
    /// laundry, a cellar compartment per few flats and always the shelter.
    /// </summary>
    private static List<Item> CellarProgram(bool apartment, float area, Random rng)
    {
        var p = new List<Item> { new(RoomType.Laundry, apartment ? 1.5f : 1.2f) };
        if (apartment)
        {
            p.Add(new Item(RoomType.Shelter, 3f));
            int cellars = Math.Clamp((int)MathF.Round(area / 18f), 1, 6);
            for (int i = 0; i < cellars; i++) p.Add(new Item(RoomType.Cellar, 1.1f));
            return p;
        }
        if (rng.NextDouble() < 0.45) p.Add(new Item(RoomType.Shelter, 1.9f));
        var extras = new List<Item>
        {
            new(RoomType.GuestRoom, 2.2f), new(RoomType.HomeCinema, 2.5f), new(RoomType.Carnotzet, 2.1f),
            new(RoomType.MusicRoom, 2.2f), new(RoomType.Cellar, 1.3f),
        };
        for (int i = extras.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (extras[i], extras[j]) = (extras[j], extras[i]);
        }
        int n = Math.Clamp((int)MathF.Round(area / 22f), 1, extras.Count);
        p.AddRange(extras.Take(n));
        return p;
    }

    /// <summary>A bank's ground floor: the banking hall at the street, the advisers' office, the vault at the back.</summary>
    private static List<Item> BankProgram(float area)
    {
        var p = new List<Item> { new(RoomType.BankHall, 5f), new(RoomType.Vault, 1.8f) };
        if (area > 60) p.Add(new Item(RoomType.Office, 1.6f));
        if (area > 90) p.Add(new Item(RoomType.WC, 0.6f));
        return p;
    }

    private static List<Item> HouseProgram(int f, int floors, float area, Random rng)
    {
        var p = new List<Item>();
        if (f == 0)
        {
            p.Add(new Item(RoomType.Living, 4f));
            p.Add(new Item(RoomType.Kitchen, 2.2f));
            p.Add(new Item(RoomType.WC, 0.7f));
            if (area > 45) p.Add(new Item(RoomType.Dining, 1.8f));
            if (area > 60 && rng.NextDouble() < 0.4) p.Add(new Item(RoomType.Pantry, 0.7f));
            if (area > 75) p.Add(new Item(rng.NextDouble() < 0.5 ? RoomType.Office : RoomType.Study, 1.5f));
            if (area > 100) p.Add(new Item(RoomType.Storage, 1f));
            if (floors == 1 && area > 55)
            {
                p.Add(new Item(RoomType.Bedroom, 2.5f));
                p.Add(new Item(RoomType.Bathroom, 1.2f));
            }
        }
        else
        {
            int beds = Math.Clamp((int)MathF.Round(area / 16f) + rng.Next(-1, 1), 1, 5);
            p.Add(new Item(RoomType.Bedroom, 3f));
            for (int i = 1; i < beds; i++)
                // a family house: one of the children's rooms is sometimes a playroom
                p.Add(new Item(i == beds - 1 && beds >= 3 && rng.NextDouble() < 0.35 ? RoomType.Playroom : RoomType.Bedroom, 2.2f));
            p.Add(new Item(RoomType.Bathroom, 1.3f));
            if (area > 90) p.Add(new Item(rng.NextDouble() < 0.5 ? RoomType.Office : RoomType.Study, 1.5f));
            else if (area > 65 && rng.NextDouble() < 0.3) p.Add(new Item(RoomType.Study, 1.3f));
        }
        return p;
    }

    private static List<Item> UnitProgram(BuildingKind kind, bool apartment, int f, float area, Random rng)
    {
        var p = new List<Item>();
        if (apartment)
        {
            p.Add(new Item(RoomType.Living, 3.5f));
            if (area >= 30) p.Add(new Item(RoomType.Kitchen, 1.6f));
            p.Add(new Item(RoomType.Bathroom, 1f));
            int beds = Math.Clamp((int)MathF.Round((area - 25f) / 14f) + rng.Next(0, 2), 0, 3);
            for (int i = 0; i < beds; i++) p.Add(new Item(RoomType.Bedroom, 2.2f));
            if (area > 55 && rng.NextDouble() < 0.25) p.Add(new Item(RoomType.Study, 1.3f));
        }
        else if (kind == BuildingKind.Commercial)
        {
            if (f == 0)
            {
                p.Add(new Item(RoomType.Shop, 5f));
                if (area > 30) p.Add(new Item(RoomType.Storage, 1.2f));
            }
            else
            {
                int offices = Math.Clamp((int)MathF.Round(area / 18f), 1, 4);
                for (int i = 0; i < offices; i++) p.Add(new Item(RoomType.Office, 3f));
                p.Add(new Item(RoomType.WC, 0.8f));
            }
        }
        else // civic
        {
            int rooms = Math.Clamp((int)MathF.Round(area / 45f), 1, 3);
            for (int i = 0; i < rooms; i++) p.Add(new Item(RoomType.Classroom, 4f));
            if (area > 40) p.Add(new Item(RoomType.WC, 0.8f));
            if (area > 70 && f == 0) p.Add(new Item(RoomType.Office, 1.5f));
        }
        return p;
    }

    private static float MinSideFor(RoomType t) => t switch
    {
        RoomType.WC => 1.1f,
        RoomType.Bathroom or RoomType.Storage or RoomType.Laundry or RoomType.Cellar => 1.6f,
        RoomType.Pantry => 1.2f,
        _ => 2.2f,
    };

    /// <summary>
    /// Squarified-ish treemap: splits the item list in two by weight and the rectangle across its
    /// longer side in the same ratio, recursively. Items that end up too thin to be a room are
    /// dropped, lightest first, and the split redone.
    /// </summary>
    private static List<RoomPlan> Treemap(RectPlan rect, List<Item> items)
    {
        var list = new List<Item>(items);
        while (true)
        {
            var rooms = new List<RoomPlan>();
            Split(rect, list, rooms);
            var bad = rooms.FirstOrDefault(r => Math.Min(r.Width, r.Depth) < MinSideFor(r.Type));
            if (bad == null || list.Count == 1) return rooms;
            // drop the lightest item; ties drop the later one
            int drop = 0;
            for (int i = 1; i < list.Count; i++)
                if (list[i].Weight <= list[drop].Weight) drop = i;
            list.RemoveAt(drop);
        }
    }

    private static void Split(RectPlan r, List<Item> items, List<RoomPlan> rooms)
    {
        if (items.Count == 1)
        {
            rooms.Add(new RoomPlan { X0 = r.X0, Z0 = r.Z0, X1 = r.X1, Z1 = r.Z1, Type = items[0].Type });
            return;
        }
        float total = items.Sum(i => i.Weight);
        int k = 1;
        float acc = items[0].Weight, best = Math.Abs(acc - total / 2);
        for (int i = 1; i < items.Count - 1; i++)
        {
            float next = acc + items[i].Weight;
            if (Math.Abs(next - total / 2) < best) { best = Math.Abs(next - total / 2); acc = next; k = i + 1; }
            else break;
        }
        float t = acc / total;
        var a = items.Take(k).ToList();
        var b = items.Skip(k).ToList();
        if (r.X1 - r.X0 >= r.Z1 - r.Z0)
        {
            float x = r.X0 + (r.X1 - r.X0) * t;
            Split(new RectPlan(r.X0, r.Z0, x, r.Z1), a, rooms);
            Split(new RectPlan(x, r.Z0, r.X1, r.Z1), b, rooms);
        }
        else
        {
            float z = r.Z0 + (r.Z1 - r.Z0) * t;
            Split(new RectPlan(r.X0, r.Z0, r.X1, z), a, rooms);
            Split(new RectPlan(r.X0, z, r.X1, r.Z1), b, rooms);
        }
    }

    // ---- doorways ----------------------------------------------------------------------------

    private sealed record Edge(int A, int B, Side SideA, float S0, float S1);

    /// <summary>
    /// Grows a spanning tree from the core (room 0) over shared walls, so every room gets exactly
    /// the doorway it needs and is reachable. Rooms of one flat or office open into each other or
    /// onto the core; crossing between flats is a last resort for a unit with no core frontage.
    /// </summary>
    private static bool Connect(FloorPlan floor, List<(float, float)> walkLeft, List<(float, float)> walkRight,
        float c0, float c1, float clear)
    {
        var rooms = floor.Rooms;
        var edges = new List<Edge>();
        for (int i = 0; i < rooms.Count; i++)
            for (int j = i + 1; j < rooms.Count; j++)
                foreach (var e in Shared(rooms, i, j, walkLeft, walkRight, c0, c1))
                    edges.Add(e);

        var inTree = new bool[rooms.Count];
        inTree[0] = true;
        var hubs = new HashSet<int>();
        for (int remaining = rooms.Count - 1; remaining > 0; remaining--)
        {
            Edge? best = null;
            int bestScore = int.MinValue;
            foreach (var e in edges)
            {
                if (inTree[e.A] == inTree[e.B]) continue;
                int from = inTree[e.A] ? e.A : e.B, to = inTree[e.A] ? e.B : e.A;
                var rf = rooms[from];
                var rt = rooms[to];
                int score;
                if (from == 0) score = rt.Unit < 0 || !hubs.Contains(rt.Unit) ? 40 : 5;
                else if (rt.Unit >= 0 && rt.Unit == rf.Unit) score = 30;
                else if (rt.Unit < 0 && rf.Unit < 0) score = 20;
                else score = 0; // between flats: only if nothing else reaches it
                // private rooms prefer the hall, living rooms prefer to be the hub
                if (rt.Type is RoomType.Living or RoomType.Shop && from == 0) score += 5;
                if (rt.Type is RoomType.WC or RoomType.Bathroom && rf.Type is RoomType.Living or RoomType.Kitchen) score -= 8;
                // the vault opens off the banking hall, behind the counter; a pantry off the kitchen
                if (rt.Type == RoomType.Vault) score += rf.Type == RoomType.BankHall ? 15 : from == 0 ? -30 : 0;
                if (rt.Type == RoomType.Pantry && rf.Type == RoomType.Kitchen) score += 15;
                score += (int)Math.Min(e.S1 - e.S0, 4f);
                if (score > bestScore) { bestScore = score; best = e; }
            }
            if (best == null) return false; // an isolated room: fall back to a simpler plan
            int a = inTree[best.A] ? best.A : best.B, b = inTree[best.A] ? best.B : best.A;
            inTree[b] = true;
            if (a == 0 && rooms[b].Unit >= 0) hubs.Add(rooms[b].Unit);
            AddDoor(rooms, best, clear);
        }
        return true;
    }

    private static IEnumerable<Edge> Shared(List<RoomPlan> rooms, int i, int j,
        List<(float, float)> walkLeft, List<(float, float)> walkRight, float c0, float c1)
    {
        var a = rooms[i];
        var b = rooms[j];
        const float eps = 0.02f;
        // vertical shared wall (normal along X)
        if (Math.Abs(a.X1 - b.X0) < eps || Math.Abs(b.X1 - a.X0) < eps)
        {
            bool aLeft = Math.Abs(a.X1 - b.X0) < eps;
            float s0 = Math.Max(a.Z0, b.Z0), s1 = Math.Min(a.Z1, b.Z1);
            var allowed = new List<(float, float)> { (s0, s1) };
            // the core only has floor at some points along its sides
            if (i == 0) allowed = Clip(s0, s1, Math.Abs(a.X0 - b.X1) < eps && Math.Abs(a.X0 - c0) < eps ? walkLeft : walkRight);
            foreach (var (x0, x1) in allowed)
                if (x1 - x0 >= InnerDoor + 0.3f)
                    yield return new Edge(i, j, aLeft ? Side.Right : Side.Left, x0, x1);
        }
        if (Math.Abs(a.Z1 - b.Z0) < eps || Math.Abs(b.Z1 - a.Z0) < eps)
        {
            bool aFront = Math.Abs(a.Z1 - b.Z0) < eps;
            float s0 = Math.Max(a.X0, b.X0), s1 = Math.Min(a.X1, b.X1);
            if (s1 - s0 >= InnerDoor + 0.3f)
                yield return new Edge(i, j, aFront ? Side.Back : Side.Front, s0, s1);
        }
    }

    private static List<(float, float)> Clip(float s0, float s1, List<(float, float)> ok)
    {
        var r = new List<(float, float)>();
        foreach (var (a, b) in ok)
        {
            float x0 = Math.Max(s0, a), x1 = Math.Min(s1, b);
            if (x1 > x0) r.Add((x0, x1));
        }
        return r;
    }

    private static void AddDoor(List<RoomPlan> rooms, Edge e, float clear)
    {
        float mid = (e.S0 + e.S1) / 2;
        // not Math.Clamp: an interval of exactly the minimum length rounds to min > max
        float c = Math.Min(Math.Max(mid, e.S0 + InnerDoor / 2 + 0.15f), e.S1 - InnerDoor / 2 - 0.15f);
        float top = Math.Min(2.05f, clear - 0.2f);
        var opposite = (Side)(((int)e.SideA + 2) % 4);
        rooms[e.A].Openings.Add(new OpeningPlan { Side = e.SideA, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = e.B });
        rooms[e.B].Openings.Add(new OpeningPlan { Side = opposite, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = e.A });
    }

    // ---- windows -------------------------------------------------------------------------------

    private static void AddWindows(InteriorLayout l, FloorPlan floor, int f)
    {
        float hw = l.Width / 2, hd = l.Depth / 2;
        float clear = l.StoreyHeight - Slab;
        for (int ri = 0; ri < floor.Rooms.Count; ri++)
        {
            var r = floor.Rooms[ri];
            bool core = ri == 0 && floor.Rooms.Count > 1;
            for (int s = 0; s < 4; s++)
            {
                var side = (Side)s;
                bool exterior = side switch
                {
                    Side.Front => Math.Abs(r.Z0 + hd) < 0.02f,
                    Side.Back => Math.Abs(r.Z1 - hd) < 0.02f,
                    Side.Left => Math.Abs(r.X0 + hw) < 0.02f,
                    _ => Math.Abs(r.X1 - hw) < 0.02f,
                };
                // a vault and a shelter are blind on purpose
                if (!exterior || r.Type is RoomType.Vault or RoomType.Shelter) continue;
                // the core's front wall is the entrance; its sides are rooms
                if (core && side == Side.Front) continue;
                float a = side is Side.Front or Side.Back ? r.X0 : r.Z0;
                float b = side is Side.Front or Side.Back ? r.X1 : r.Z1;
                float len = b - a;

                var (width, bottom, top, spacing) = r.Type switch
                {
                    RoomType.WC or RoomType.Bathroom => (0.6f, 1.4f, 2.0f, 99f),
                    RoomType.Shop when side == Side.Front && f == 0 => (Math.Min(2.6f, len * 0.4f), 0.3f, 2.4f, 3.4f),
                    RoomType.Nave => (1.0f, 2.5f, clear - 1.2f, 4.5f),
                    RoomType.Workshop => (1.8f, clear - 1.6f, clear - 0.5f, 4f),
                    RoomType.Barn => (0.7f, clear * 0.6f, clear * 0.6f + 0.6f, 9f),
                    RoomType.Garage or RoomType.Storage => (0.7f, 1.4f, 2.0f, 99f),
                    _ => (1.1f, 0.9f, Math.Min(2.2f, clear - 0.3f), 3.2f),
                };
                if (top - bottom < 0.4f || len < width + 0.8f) continue;
                int count = Math.Max(1, (int)(len / spacing));
                for (int k = 0; k < count; k++)
                {
                    float c = a + len * (k + 0.5f) / count;
                    if (Blocked(r, side, c, width)) continue;
                    r.Openings.Add(new OpeningPlan
                    {
                        Side = side, Center = c, Width = width, Bottom = bottom, Top = top, Kind = OpeningKind.Window,
                    });
                }
            }
        }
    }

    private static bool Blocked(RoomPlan r, Side side, float c, float w) =>
        r.Openings.Any(o => o.Side == side && Math.Abs(o.Center - c) < (o.Width + w) / 2 + 0.3f);

    // ---- furniture -----------------------------------------------------------------------------

    private sealed record Piece(FurnitureType Type, float W, float D, float H, bool Wall);

    /// <summary>A room's pieces: what any room of its type has, then what its building kind adds (<see cref="KindExtras"/>).</summary>
    private static IEnumerable<Piece> Pieces(RoomType t, RoomPlan r, Random rng, BuildingKind kind) =>
        BasePieces(t, r, rng).Concat(KindExtras(t, kind));

    /// <summary>
    /// Stock that depends on what the building is for: a shop's back room is racks of goods, a
    /// works' store racks and crates, a farm's store sacks and bales, an office building's office
    /// another filing shelf. Added after the room's own pieces, so they only fill space left over.
    /// </summary>
    private static IEnumerable<Piece> KindExtras(RoomType t, BuildingKind kind) => (t, kind) switch
    {
        (RoomType.Storage, BuildingKind.Commercial) => new[]
        {
            new Piece(FurnitureType.Rack, 1.8f, 0.5f, 1.8f, true),
            new Piece(FurnitureType.Crate, 0.8f, 0.8f, 0.7f, true),
        },
        (RoomType.Storage, BuildingKind.Industrial) or (RoomType.Workshop, BuildingKind.Industrial) => new[]
        {
            new Piece(FurnitureType.Rack, 2.0f, 0.6f, 2.2f, true),
            new Piece(FurnitureType.Crate, 1.0f, 1.0f, 0.9f, false),
        },
        (RoomType.Storage or RoomType.Workshop, BuildingKind.Agricultural) => new[]
        {
            new Piece(FurnitureType.HayBale, 1.2f, 1.0f, 1.0f, true),
            new Piece(FurnitureType.Crate, 1.0f, 0.8f, 0.8f, true),
        },
        (RoomType.Office, BuildingKind.Commercial or BuildingKind.Industrial or BuildingKind.Civic) => new[]
        {
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.9f, true),
        },
        (RoomType.Kitchen, BuildingKind.Commercial) => new[]   // a restaurant's kitchen
        {
            new Piece(FurnitureType.Fridge, 0.6f, 0.65f, 1.8f, true),
            new Piece(FurnitureType.Shelf, 1.2f, 0.45f, 1.9f, true),
        },
        _ => Array.Empty<Piece>(),
    };

    private static IEnumerable<Piece> BasePieces(RoomType t, RoomPlan r, Random rng) => t switch
    {
        RoomType.Living => new[]
        {
            new Piece(FurnitureType.Sofa, 2.0f, 0.85f, 0.8f, true),
            new Piece(FurnitureType.Tv, 1.2f, 0.45f, 1.0f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.35f, 1.9f, true),
            new Piece(FurnitureType.Rug, 1.8f, 1.3f, 0.02f, false),
            new Piece(FurnitureType.CoffeeTable, 1.0f, 0.55f, 0.4f, false),
            new Piece(FurnitureType.Plant, 0.4f, 0.4f, 1.1f, true),
        },
        RoomType.Kitchen => new[]
        {
            new Piece(FurnitureType.Counter, Math.Clamp(Math.Max(r.Width, r.Depth) - 1.6f, 1.2f, 3.0f), 0.6f, 0.9f, true),
            new Piece(FurnitureType.Fridge, 0.6f, 0.65f, 1.8f, true),
            new Piece(FurnitureType.Stove, 0.6f, 0.6f, 0.9f, true),
            new Piece(FurnitureType.Table, 1.2f, 0.8f, 0.75f, false),
        },
        RoomType.Dining => new[]
        {
            new Piece(FurnitureType.Table, 1.8f, 0.9f, 0.75f, false),
            new Piece(FurnitureType.Shelf, 1.4f, 0.45f, 1.0f, true),
        },
        RoomType.Bedroom => Math.Min(r.Width, r.Depth) > 2.8f
            ? new[]
            {
                new Piece(FurnitureType.Bed, 1.6f, 2.05f, 0.55f, true),
                new Piece(FurnitureType.Nightstand, 0.45f, 0.4f, 0.5f, true),
                new Piece(FurnitureType.Wardrobe, 1.4f, 0.6f, 2.0f, true),
                new Piece(FurnitureType.Rug, 1.4f, 0.8f, 0.02f, false),
            }
            : new[]
            {
                new Piece(FurnitureType.SingleBed, 0.9f, 2.0f, 0.5f, true),
                new Piece(FurnitureType.Wardrobe, 1.0f, 0.6f, 2.0f, true),
                new Piece(FurnitureType.Desk, 1.0f, 0.55f, 0.75f, true),
            },
        RoomType.Bathroom => new[]
        {
            new Piece(FurnitureType.Bathtub, 0.75f, 1.7f, 0.55f, true),
            new Piece(FurnitureType.Toilet, 0.4f, 0.65f, 0.75f, true),
            new Piece(FurnitureType.Sink, 0.6f, 0.45f, 0.85f, true),
        },
        RoomType.WC => new[]
        {
            new Piece(FurnitureType.Toilet, 0.4f, 0.65f, 0.75f, true),
            new Piece(FurnitureType.Sink, 0.45f, 0.35f, 0.85f, true),
        },
        RoomType.Office => new[]
        {
            new Piece(FurnitureType.Desk, 1.4f, 0.7f, 0.75f, true),
            new Piece(FurnitureType.Desk, 1.4f, 0.7f, 0.75f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.9f, true),
            new Piece(FurnitureType.Plant, 0.4f, 0.4f, 1.2f, true),
        },
        RoomType.Shop => new[]
        {
            new Piece(FurnitureType.ShopCounter, 2.0f, 0.6f, 1.0f, true),
            new Piece(FurnitureType.Rack, 1.8f, 0.5f, 1.6f, true),
            new Piece(FurnitureType.Rack, 1.8f, 0.5f, 1.6f, true),
            new Piece(FurnitureType.Rack, 1.8f, 0.5f, 1.6f, true),
            new Piece(FurnitureType.Rack, 1.6f, 0.9f, 1.4f, false),
        },
        RoomType.Storage => new[]
        {
            new Piece(FurnitureType.Shelf, 1.2f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.Crate, 0.8f, 0.8f, 0.7f, true),
            new Piece(FurnitureType.Crate, 0.6f, 0.6f, 0.6f, true),
        },
        RoomType.Classroom => new[]
        {
            new Piece(FurnitureType.Blackboard, 2.6f, 0.12f, 2.1f, true),
            new Piece(FurnitureType.Desk, 1.4f, 0.7f, 0.75f, true),
        },
        RoomType.Workshop => new[]
        {
            new Piece(FurnitureType.Workbench, 2.4f, 0.8f, 0.9f, true),
            new Piece(FurnitureType.Rack, 2.5f, 0.8f, 2.4f, true),
            new Piece(FurnitureType.Rack, 2.5f, 0.8f, 2.4f, true),
            new Piece(FurnitureType.Crate, 1.2f, 1.0f, 1.0f, false),
            new Piece(FurnitureType.Crate, 1.2f, 1.0f, 1.0f, false),
            new Piece(FurnitureType.Crate, 1.0f, 1.0f, 0.8f, true),
        },
        RoomType.Barn => new[]
        {
            new Piece(FurnitureType.HayBale, 1.2f, 1.0f, 1.0f, true),
            new Piece(FurnitureType.HayBale, 1.2f, 1.0f, 1.0f, true),
            new Piece(FurnitureType.HayBale, 1.2f, 1.0f, 1.0f, true),
            new Piece(FurnitureType.HayBale, 1.2f, 1.0f, 1.0f, false),
            new Piece(FurnitureType.Crate, 1.0f, 0.8f, 0.8f, true),
        },
        RoomType.Garage => new[]
        {
            new Piece(FurnitureType.Car, 1.8f, 4.2f, 1.4f, false),
            new Piece(FurnitureType.Shelf, 1.2f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.Workbench, 1.6f, 0.6f, 0.9f, true),
        },
        RoomType.Hall or RoomType.Lobby => new[]
        {
            new Piece(FurnitureType.Shelf, 0.9f, 0.35f, 1.0f, true),
            new Piece(FurnitureType.Plant, 0.4f, 0.4f, 1.1f, true),
        },
        RoomType.Laundry => new[]
        {
            new Piece(FurnitureType.WashingMachine, 0.6f, 0.6f, 0.85f, true),
            new Piece(FurnitureType.Dryer, 0.6f, 0.6f, 0.85f, true),
            new Piece(FurnitureType.Sink, 0.6f, 0.45f, 0.85f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.8f, true),
            new Piece(FurnitureType.IroningBoard, 1.2f, 0.35f, 0.9f, true),
        },
        RoomType.GuestRoom => new[]
        {
            Math.Min(r.Width, r.Depth) > 2.8f
                ? new Piece(FurnitureType.Bed, 1.4f, 2.0f, 0.55f, true)
                : new Piece(FurnitureType.SingleBed, 0.9f, 2.0f, 0.5f, true),
            new Piece(FurnitureType.Nightstand, 0.45f, 0.4f, 0.5f, true),
            new Piece(FurnitureType.Wardrobe, 1.0f, 0.6f, 2.0f, true),
            new Piece(FurnitureType.Armchair, 0.8f, 0.8f, 0.9f, true),
        },
        RoomType.HomeCinema => new[]
        {
            new Piece(FurnitureType.CinemaScreen, Math.Clamp(Math.Min(r.Width, r.Depth) - 0.8f, 1.6f, 3.0f), 0.12f, 2.1f, true),
            new Piece(FurnitureType.Sofa, 2.0f, 0.9f, 0.8f, false),
            new Piece(FurnitureType.Armchair, 0.85f, 0.85f, 0.9f, true),
            new Piece(FurnitureType.Armchair, 0.85f, 0.85f, 0.9f, true),
            new Piece(FurnitureType.Amplifier, 0.45f, 0.35f, 1.0f, true),
            new Piece(FurnitureType.Amplifier, 0.45f, 0.35f, 1.0f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.35f, 1.0f, true),
        },
        RoomType.Carnotzet => new[]
        {
            new Piece(FurnitureType.WineRack, 1.2f, 0.4f, 1.9f, true),
            new Piece(FurnitureType.WineRack, 1.2f, 0.4f, 1.9f, true),
            new Piece(FurnitureType.Table, 1.6f, 0.85f, 0.75f, false),
            new Piece(FurnitureType.Barrel, 0.7f, 0.7f, 0.9f, true),
            new Piece(FurnitureType.Barrel, 0.7f, 0.7f, 0.9f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.6f, true),
        },
        RoomType.MusicRoom => new[]
        {
            new Piece(FurnitureType.DrumKit, 1.6f, 1.3f, 1.1f, false),
            new Piece(FurnitureType.Piano, 1.5f, 0.6f, 1.25f, true),
            new Piece(FurnitureType.Keyboard, 1.2f, 0.45f, 0.9f, true),
            new Piece(FurnitureType.GuitarStand, 0.5f, 0.4f, 1.1f, true),
            new Piece(FurnitureType.GuitarStand, 0.5f, 0.4f, 1.1f, true),
            new Piece(FurnitureType.Amplifier, 0.6f, 0.35f, 0.6f, true),
            new Piece(FurnitureType.AcousticFoam, 1.5f, 0.08f, 2.0f, true),
            new Piece(FurnitureType.AcousticFoam, 1.5f, 0.08f, 2.0f, true),
            new Piece(FurnitureType.AcousticFoam, 1.5f, 0.08f, 2.0f, true),
        },
        RoomType.Shelter => new[]
        {
            new Piece(FurnitureType.BunkBed, 0.9f, 2.0f, 1.7f, true),
            new Piece(FurnitureType.BunkBed, 0.9f, 2.0f, 1.7f, true),
            new Piece(FurnitureType.WaterTank, 0.6f, 0.6f, 1.2f, true),
            new Piece(FurnitureType.Shelf, 1.2f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.BunkBed, 0.9f, 2.0f, 1.7f, true),
            new Piece(FurnitureType.Crate, 0.8f, 0.6f, 0.6f, true),
        },
        RoomType.Cellar => new[]
        {
            new Piece(FurnitureType.Shelf, 1.2f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.Crate, 0.8f, 0.8f, 0.7f, true),
            new Piece(FurnitureType.WineRack, 1.0f, 0.4f, 1.6f, true),
        },
        RoomType.Playroom => new[]
        {
            new Piece(FurnitureType.ToyBox, 0.8f, 0.5f, 0.5f, true),
            new Piece(FurnitureType.Rug, 1.6f, 1.2f, 0.02f, false),
            new Piece(FurnitureType.Shelf, 1.0f, 0.35f, 1.2f, true),
            new Piece(FurnitureType.Desk, 1.0f, 0.55f, 0.6f, true),
            new Piece(FurnitureType.ToyBox, 0.7f, 0.45f, 0.45f, true),
        },
        RoomType.Study => new[]
        {
            new Piece(FurnitureType.Bookcase, 1.2f, 0.35f, 2.1f, true),
            new Piece(FurnitureType.Desk, 1.4f, 0.7f, 0.75f, true),
            new Piece(FurnitureType.Bookcase, 1.2f, 0.35f, 2.1f, true),
            new Piece(FurnitureType.Armchair, 0.8f, 0.8f, 0.9f, true),
            new Piece(FurnitureType.Plant, 0.4f, 0.4f, 1.2f, true),
        },
        RoomType.Pantry => new[]
        {
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.9f, true),
            new Piece(FurnitureType.Fridge, 0.6f, 0.65f, 1.8f, true),
            new Piece(FurnitureType.Shelf, 0.9f, 0.4f, 1.9f, true),
        },
        RoomType.BankHall => new[]
        {
            new Piece(FurnitureType.TellerDesk, Math.Clamp(Math.Max(r.Width, r.Depth) * 0.45f, 2.0f, 3.6f), 0.8f, 1.15f, true),
            new Piece(FurnitureType.Armchair, 0.8f, 0.8f, 0.9f, true),
            new Piece(FurnitureType.Armchair, 0.8f, 0.8f, 0.9f, true),
            new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true),
            new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true),
            new Piece(FurnitureType.Rug, 2.2f, 1.4f, 0.02f, false),
        },
        _ => Array.Empty<Piece>(),
    };

    private static void Furnish(InteriorLayout l, Random rng)
    {
        var rooms = new List<(int Floor, RoomPlan Room, List<RectPlan> Placed, List<RectPlan> Blocked)>();
        for (int f = 0; f < l.Floors.Count; f++)
        {
            var floor = l.Floors[f];
            for (int ri = 0; ri < floor.Rooms.Count; ri++)
            {
                var r = floor.Rooms[ri];
                var placed = new List<RectPlan>();
                var blocked = new List<RectPlan>();
                rooms.Add((f, r, placed, blocked));
                foreach (var o in r.Openings)
                    if (o.Kind != OpeningKind.Window) blocked.Add(Clearance(r, o));
                if (r.Type == RoomType.Shelter)
                    foreach (var o in r.Openings)
                        if (o.Kind == OpeningKind.Door) blocked.Add(BlastLeaf(r, o));
                // a garage or a barn is driven into: a lane from its door, as wide, kept clear
                if (f == 0 && BuildingFootprint.VehicleDoor(l.Kind))
                    foreach (var o in r.Openings)
                        if (o.Kind == OpeningKind.Entry && o.Side == Side.Front) blocked.Add(Lane(l.Kind, r, o));
                // the stairwell is not somewhere to put a sofa
                bool isCore = ri == 0 && floor.Rooms.Count > 1;
                if (isCore)
                {
                    float zs = FirstStairZ(l);
                    if (zs < r.Z1) blocked.Add(new RectPlan(r.X0, zs - 1.2f, r.X1, r.Z1));
                    blocked.Add(new RectPlan(r.X0, r.Z0, r.X1, r.Z0 + 1.4f)); // just inside the door
                }
                foreach (var h in floor.Holes) blocked.Add(h);

                if (r.Type == RoomType.Nave) { Pews(l, f, r, placed, blocked); continue; }
                if (r.Type == RoomType.Classroom) Desks(l, f, r, placed, blocked);
                if (r.Type == RoomType.Vault) { Vault(l, f, r, placed, blocked, rng); continue; }

                foreach (var p in Pieces(r.Type, r, rng, l.Kind))
                    TryPlace(l, f, r, p, placed, blocked, rng);
            }
        }
        if (l.IsBank) Counter(l, rooms, rng);
        Secure(l, rooms);
    }

    /// <summary>
    /// A bank must have its teller desk, or nowhere takes your cash: when the banking hall's walls
    /// were too short or too cut up for the full one, a shorter desk, then any ground-floor room.
    /// </summary>
    private static void Counter(InteriorLayout l, List<(int Floor, RoomPlan Room, List<RectPlan> Placed, List<RectPlan> Blocked)> rooms, Random rng)
    {
        if (l.Furniture.Any(f => f.Type == FurnitureType.TellerDesk)) return;
        var ground = rooms.Where(x => x.Floor == l.Below)
            .OrderByDescending(x => x.Room.Type == RoomType.BankHall).ThenByDescending(x => x.Room.Area).ToList();
        foreach (float width in new[] { 2.0f, 1.5f, 1.1f })
            foreach (var c in ground)
            {
                int before = l.Furniture.Count;
                TryPlace(l, c.Floor, c.Room, new Piece(FurnitureType.TellerDesk, width, 0.7f, 1.15f, true), c.Placed, c.Blocked, rng);
                if (l.Furniture.Count > before) return;
                TryPlace(l, c.Floor, c.Room, new Piece(FurnitureType.TellerDesk, width, 0.7f, 1.15f, false), c.Placed, c.Blocked, rng);
                if (l.Furniture.Count > before) return;
            }
    }

    private static readonly Piece VaultSafePiece = new(FurnitureType.VaultSafe, 0.9f, 0.75f, 1.7f, true);

    /// <summary>
    /// A bank's vault: steel safes all round the walls, as many as fit (up to six), each cracked
    /// with the dial and then the Simon panel (<c>LootService</c>), and a crate of coin rolls.
    /// </summary>
    private static void Vault(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        for (int i = 0; i < 6; i++)
        {
            int before = l.Furniture.Count;
            TryPlace(l, f, r, VaultSafePiece, placed, blocked, rng);
            if (l.Furniture.Count == before) break;
        }
        TryPlace(l, f, r, new Piece(FurnitureType.Crate, 0.7f, 0.5f, 0.5f, true), placed, blocked, rng);
    }

    private static readonly Piece LockerPiece = new(FurnitureType.GunLocker, 0.6f, 0.45f, 1.8f, true);
    private static readonly Piece SafePiece = new(FurnitureType.Safe, 0.6f, 0.6f, 0.85f, true);

    /// <summary>
    /// Gun lockers and safes (#165): what a kind of building keeps locked away, as (piece, chance,
    /// the rooms it may stand in, best first). Swiss militia and hunters keep a rifle at home, so
    /// houses and farms often have a gun locker; shops, offices and works a safe.
    /// </summary>
    private static IEnumerable<(Piece Piece, double Chance, RoomType[] Rooms)> SecureFor(InteriorLayout l)
    {
        // the army rifle in the shelter or the cellar, else the bedroom wardrobe's neighbour
        var homeLocker = new[] { RoomType.Shelter, RoomType.Cellar, RoomType.Bedroom, RoomType.Storage, RoomType.Study, RoomType.Office, RoomType.Living, RoomType.Hall };

        switch (l.Kind)
        {
            case BuildingKind.House:
            case BuildingKind.Other:
                yield return (LockerPiece, 0.40, homeLocker);
                yield return (SafePiece, 0.15, new[] { RoomType.Office, RoomType.Bedroom, RoomType.Living, RoomType.Storage });
                break;
            case BuildingKind.Apartment:
                for (int i = 0; i < Math.Max(1, l.Floors.Count); i++) yield return (LockerPiece, 0.25, homeLocker);
                yield return (SafePiece, 0.10, new[] { RoomType.Office, RoomType.Bedroom, RoomType.Storage });
                break;
            case BuildingKind.Agricultural:
                yield return (LockerPiece, 0.55, new[] { RoomType.Storage, RoomType.Workshop, RoomType.Bedroom, RoomType.Living, RoomType.Barn });
                break;
            case BuildingKind.Commercial:
                yield return (SafePiece, 0.75, new[] { RoomType.Office, RoomType.Shop, RoomType.Storage, RoomType.Lobby });
                break;
            case BuildingKind.Industrial:
                yield return (SafePiece, 0.50, new[] { RoomType.Office, RoomType.Storage, RoomType.Workshop });
                break;
            case BuildingKind.Civic:
                yield return (SafePiece, 0.30, new[] { RoomType.Office, RoomType.Storage });
                yield return (LockerPiece, 0.20, new[] { RoomType.Office, RoomType.Storage });
                break;
            case BuildingKind.Annex:
                yield return (LockerPiece, 0.12, new[] { RoomType.Storage, RoomType.Workshop, RoomType.Garage, RoomType.Barn });
                break;
        }
    }

    /// <summary>
    /// Places the locked containers after everything else, from their own seed so the rest of the
    /// plan does not depend on them: a random room of the first preferred type that has space,
    /// then the next type. A room gets at most one.
    /// </summary>
    private static void Secure(InteriorLayout l, List<(int Floor, RoomPlan Room, List<RectPlan> Placed, List<RectPlan> Blocked)> rooms)
    {
        var rng = new Random(StableHash(l.Key + "|secure"));
        var used = new HashSet<RoomPlan>();
        foreach (var (piece, chance, types) in SecureFor(l).ToList())
        {
            if (rng.NextDouble() >= chance) continue;
            bool done = false;
            foreach (var type in types)
            {
                var candidates = rooms.Where(x => x.Room.Type == type && !used.Contains(x.Room)).ToList();
                // shuffle, so it is not always the first bedroom
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
                }
                foreach (var c in candidates)
                {
                    int before = l.Furniture.Count;
                    TryPlace(l, c.Floor, c.Room, piece, c.Placed, c.Blocked, rng);
                    if (l.Furniture.Count == before) continue;
                    used.Add(c.Room);
                    done = true;
                    break;
                }
                if (done) break;
            }
        }
    }

    private static float FirstStairZ(InteriorLayout l) =>
        l.Floors.Select(fl => fl.Flight).Where(x => x != null)
            .Select(x => Math.Min(x!.ZBottom, x.ZTop)).DefaultIfEmpty(float.MaxValue).Min()
        is var z && z < float.MaxValue ? z - Landing : float.MaxValue;

    /// <summary>
    /// The lane a vehicle drives in by, from the front door: as wide as it plus a margin, a garage's
    /// to just short of its back wall (room there for a shelf), a barn's two car lengths deep.
    /// </summary>
    private static RectPlan Lane(BuildingKind kind, RoomPlan r, OpeningPlan o)
    {
        float half = o.Width / 2 + 0.3f;
        float deep = kind == BuildingKind.Garage ? r.Z1 - r.Z0 - 0.7f : Math.Min(r.Z1 - r.Z0 - 1.5f, 9f);
        return new RectPlan(o.Center - half, r.Z0, o.Center + half, r.Z0 + Math.Max(deep, 1.1f));
    }

    /// <summary>
    /// The wall strip a shelter's open blast-door leaf stands on, past the doorway's far jamb
    /// (<c>InteriorMeshBuilder.BlastDoor</c> draws it there), kept free of furniture.
    /// </summary>
    private static RectPlan BlastLeaf(RoomPlan r, OpeningPlan o)
    {
        float u0 = o.Center + o.Width / 2, u1 = u0 + o.Width + 0.4f, deep = 0.35f;
        return o.Side switch
        {
            Side.Front => new RectPlan(u0, r.Z0, u1, r.Z0 + deep),
            Side.Back => new RectPlan(u0, r.Z1 - deep, u1, r.Z1),
            Side.Left => new RectPlan(r.X0, u0, r.X0 + deep, u1),
            _ => new RectPlan(r.X1 - deep, u0, r.X1, u1),
        };
    }

    /// <summary>Space that must stay clear in front of a doorway, on this room's side.</summary>

    private static RectPlan Clearance(RoomPlan r, OpeningPlan o)
    {
        float half = o.Width / 2 + 0.25f, deep = 1.1f;
        return o.Side switch
        {
            Side.Front => new RectPlan(o.Center - half, r.Z0, o.Center + half, r.Z0 + deep),
            Side.Back => new RectPlan(o.Center - half, r.Z1 - deep, o.Center + half, r.Z1),
            Side.Left => new RectPlan(r.X0, o.Center - half, r.X0 + deep, o.Center + half),
            _ => new RectPlan(r.X1 - deep, o.Center - half, r.X1, o.Center + half),
        };
    }

    private static bool Free(RoomPlan r, RectPlan p, List<RectPlan> placed, List<RectPlan> blocked, float gap)
    {
        const float inset = WallInset + 0.02f;
        if (p.X0 < r.X0 + inset - 1e-3f || p.X1 > r.X1 - inset + 1e-3f
            || p.Z0 < r.Z0 + inset - 1e-3f || p.Z1 > r.Z1 - inset + 1e-3f) return false;
        var grown = new RectPlan(p.X0 - gap, p.Z0 - gap, p.X1 + gap, p.Z1 + gap);
        return !placed.Any(q => q.Overlaps(grown)) && !blocked.Any(q => q.Overlaps(p));
    }

    private static void TryPlace(InteriorLayout l, int f, RoomPlan r, Piece p,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        const float inset = WallInset + 0.02f;
        if (p.Wall)
        {
            // back wall first, then the sides, the front (door) wall last
            var order = new[] { Side.Back, Side.Left, Side.Right, Side.Front };
            int startShift = rng.Next(0, 2);
            foreach (var side in order)
            {
                bool along = side is Side.Front or Side.Back;
                float a = (along ? r.X0 : r.Z0) + inset, b = (along ? r.X1 : r.Z1) - inset;
                float len = b - a;
                if (len < p.W) continue;
                int steps = Math.Max(1, (int)((len - p.W) / 0.15f));
                for (int k = 0; k <= steps; k++)
                {
                    // alternate from both ends toward the middle, so pieces hug corners
                    int idx = (k + startShift) % 2 == 0 ? k / 2 : steps - k / 2;
                    float c = a + p.W / 2 + (len - p.W) * idx / steps;
                    var rect = side switch
                    {
                        Side.Back => new RectPlan(c - p.W / 2, r.Z1 - inset - p.D, c + p.W / 2, r.Z1 - inset),
                        Side.Front => new RectPlan(c - p.W / 2, r.Z0 + inset, c + p.W / 2, r.Z0 + inset + p.D),
                        Side.Left => new RectPlan(r.X0 + inset, c - p.W / 2, r.X0 + inset + p.D, c + p.W / 2),
                        _ => new RectPlan(r.X1 - inset - p.D, c - p.W / 2, r.X1 - inset, c + p.W / 2),
                    };
                    if (!Free(r, rect, placed, blocked, 0.05f)) continue;
                    // tall pieces stay out of windows
                    if (p.H > 1.0f && r.Openings.Any(o => o.Kind == OpeningKind.Window && o.Side == side
                        && Math.Abs(o.Center - c) < (o.Width + p.W) / 2)) continue;
                    int turns = side switch { Side.Front => 0, Side.Left => 1, Side.Back => 2, _ => 3 };
                    Add(l, f, p, rect, turns, placed);
                    return;
                }
            }
        }
        else
        {
            float cx = (r.X0 + r.X1) / 2, cz = (r.Z0 + r.Z1) / 2;
            bool rotate = r.Width < r.Depth && p.W > p.D;
            float w = rotate ? p.D : p.W, d = rotate ? p.W : p.D;
            for (int ring = 0; ring < 8; ring++)
                for (int dx = -ring; dx <= ring; dx++)
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                        float x = cx + dx * 0.4f, z = cz + dz * 0.4f;
                        var rect = new RectPlan(x - w / 2, z - d / 2, x + w / 2, z + d / 2);
                        bool rug = p.Type == FurnitureType.Rug;
                        if (!Free(r, rect, rug ? new List<RectPlan>() : placed, blocked, rug ? 0 : 0.45f)) continue;
                        Add(l, f, p, rect, rotate ? 1 : 0, rug ? new List<RectPlan>() : placed);
                        return;
                    }
        }
    }

    private static void Add(InteriorLayout l, int f, Piece p, RectPlan rect, int turns, List<RectPlan> placed)
    {
        placed.Add(rect);
        l.Furniture.Add(new FurniturePlan
        {
            Type = p.Type, Floor = f, X = (rect.X0 + rect.X1) / 2, Z = (rect.Z0 + rect.Z1) / 2,
            Turns = turns, W = p.W, D = p.D, H = p.H,
        });
    }

    private static void Pews(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked)
    {
        TryPlace(l, f, r, new Piece(FurnitureType.Altar, Math.Min(2.0f, r.Width * 0.4f), 0.9f, 1.0f, true),
            placed, blocked, new Random(0));
        float aisle = 1.4f;
        float pewW = Math.Min(3.5f, (r.Width - aisle) / 2 - 0.5f);
        if (pewW < 1.2f) return;
        for (float z = r.Z0 + 2.0f; z < r.Z1 - 3.5f; z += 1.0f)
            foreach (float sign in new[] { -1f, 1f })
            {
                float x = (r.X0 + r.X1) / 2 + sign * (aisle / 2 + pewW / 2);
                var rect = new RectPlan(x - pewW / 2, z, x + pewW / 2, z + 0.5f);
                // backrest toward the door, so they face the altar
                if (Free(r, rect, placed, blocked, 0.05f))
                    Add(l, f, new Piece(FurnitureType.Pew, pewW, 0.5f, 0.9f, false), rect, 0, placed);
            }
    }

    private static void Desks(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked)
    {
        for (float z = r.Z0 + 1.6f; z < r.Z1 - 1.8f; z += 1.4f)
            for (float x = r.X0 + 1.0f; x < r.X1 - 1.8f; x += 1.7f)
            {
                var rect = new RectPlan(x, z, x + 1.2f, z + 0.6f);
                if (Free(r, rect, placed, blocked, 0.3f))
                    Add(l, f, new Piece(FurnitureType.Desk, 1.2f, 0.6f, 0.75f, false), rect, 2, placed);
            }
    }

    /// <summary>Clamp that tolerates a range rounding to empty (Math.Clamp throws on min &gt; max).</summary>
    private static float Fit(float v, float lo, float hi) => lo > hi ? (lo + hi) / 2 : Math.Clamp(v, lo, hi);

    /// <summary>FNV-1a: string.GetHashCode is randomised per process, and this seed must not be.</summary>
    public static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return (int)h;
    }
}
