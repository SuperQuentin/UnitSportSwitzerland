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
            // a barn's hall opens as wide as its door; elsewhere a door leads into a hall or a core
            EntryWidth = b.Kind == BuildingKind.Agricultural ? fp.Door.Width : Math.Min(fp.Door.Width, 1.8f),
        };

        bool single = b.Kind is BuildingKind.Industrial or BuildingKind.Agricultural or BuildingKind.Annex
            or BuildingKind.Garage
            or BuildingKind.UnderConstruction or BuildingKind.Sacral
            || fp.Width < 4.5f || fp.Depth < 4.5f || fp.Width * fp.Depth < 25f;

        if (single || !TryCored(layout, fp, b.Kind, n, rng))
            SingleRoom(layout, b.Kind, rng);

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

    // ---- single room -------------------------------------------------------------------

    private static void SingleRoom(InteriorLayout l, BuildingKind kind, Random rng)
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
            Top = kind == BuildingKind.Agricultural ? BuildingFootprint.DoorHeightFor(kind, clear)
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

    private static bool TryCored(InteriorLayout l, Footprint fp, BuildingKind kind, int floors, Random rng)
    {
        float W = l.Width, D = l.Depth, h = l.StoreyHeight;
        float hw = W / 2, hd = D / 2;

        // stair geometry; steepen before giving up, and give up by dropping to one floor
        float run = 0, zs0 = 0;
        int steps = (int)MathF.Ceiling(h / 0.19f);
        if (floors > 1)
        {
            float room = D - 1.6f - 2 * Landing;
            float tread = Math.Min(0.27f, room / steps);
            if (tread < 0.2f) floors = 1;
            else
            {
                run = tread * steps;
                zs0 = hd - (run + 2 * Landing);
            }
        }

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
            var floor = new FloorPlan();
            var core = new RoomPlan
            {
                X0 = c0, Z0 = -hd, X1 = c1, Z1 = hd,
                Type = f == 0 ? (apartment || kind is BuildingKind.Commercial or BuildingKind.Civic ? RoomType.Lobby : RoomType.Hall)
                    : RoomType.Landing,
            };
            floor.Rooms.Add(core);
            if (f == 0)
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

            if (residential)
            {
                var program = HouseProgram(f, floors, sideArea, rng);
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
                for (int s = 0; s < sides.Count; s++)
                {
                    if (shares[s].Count == 0) shares[s].Add(new Item(f == 0 ? RoomType.Living : RoomType.Bedroom, 1));
                    foreach (var r in Treemap(sides[s], shares[s])) { r.Unit = -1; floor.Rooms.Add(r); }
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
                        var program = UnitProgram(kind, apartment, f, area, rng);
                        foreach (var r in Treemap(unit, program)) { r.Unit = unitId; floor.Rooms.Add(r); }
                        unitId++;
                    }
                }
            }

            if (!Connect(floor, walkLeft, walkRight, c0, c1, clear)) return false;
            AddWindows(l, floor, f);
            l.Floors.Add(floor);
        }
        return true;
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
            if (area > 75) p.Add(new Item(RoomType.Office, 1.5f));
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
            for (int i = 1; i < beds; i++) p.Add(new Item(RoomType.Bedroom, 2.2f));
            p.Add(new Item(RoomType.Bathroom, 1.3f));
            if (area > 90) p.Add(new Item(RoomType.Office, 1.5f));
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
        RoomType.Bathroom or RoomType.Storage => 1.6f,
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
                if (!exterior) continue;
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

    private static IEnumerable<Piece> Pieces(RoomType t, RoomPlan r, Random rng) => t switch
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
        _ => Array.Empty<Piece>(),
    };

    private static void Furnish(InteriorLayout l, Random rng)
    {
        for (int f = 0; f < l.Floors.Count; f++)
        {
            var floor = l.Floors[f];
            for (int ri = 0; ri < floor.Rooms.Count; ri++)
            {
                var r = floor.Rooms[ri];
                var placed = new List<RectPlan>();
                var blocked = new List<RectPlan>();
                foreach (var o in r.Openings)
                    if (o.Kind != OpeningKind.Window) blocked.Add(Clearance(r, o));
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

                foreach (var p in Pieces(r.Type, r, rng))
                    TryPlace(l, f, r, p, placed, blocked, rng);
            }
        }
    }

    private static float FirstStairZ(InteriorLayout l) =>
        l.Floors.Select(fl => fl.Flight).Where(x => x != null)
            .Select(x => Math.Min(x!.ZBottom, x.ZTop)).DefaultIfEmpty(float.MaxValue).Min()
        is var z && z < float.MaxValue ? z - Landing : float.MaxValue;

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
