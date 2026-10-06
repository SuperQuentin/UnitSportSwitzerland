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

    /// <summary>An entry at least this wide is driven through, and keeps a lane clear behind it.</summary>
    private const float VehicleEntryWidth = 2.5f;

    /// <param name="rural">The building's tile is countryside (<c>Loot.ShopTables.IsRural</c>): only there is a gun shop.</param>
    /// <param name="type">
    /// What <see cref="BuildingTypes"/> made of the building's group, for the types that cannot be
    /// seen in the footprint alone: an IKEA is recognised from where the tile is (#501), so the
    /// tile-aware <see cref="Generate(BuildingTile, int, RoadTile?, ChunkGrid?)"/> has to pass it in.
    /// </param>
    public static InteriorLayout Generate(Footprint fp, Building b, bool rural = false,
        BuildingType type = BuildingType.None)
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
            DoorHeight = fp.Door.Height,
            StoreyHeight = h,
            EntryX = fp.EntryX,
            // a barn's or a garage's hall opens as wide as its door (a vehicle drives through);
            // elsewhere a door leads into a hall or a core
            EntryWidth = BuildingFootprint.VehicleDoor(b.Kind) ? fp.Door.Width : Math.Min(fp.Door.Width, 1.8f),
        };

        bool bank = BuildingFootprint.IsBank(fp);
        if (bank) layout.Type = BuildingType.Bank;
        layout.Shop = BuildingFootprint.ShopOf(fp, rural);

        // An industrial site (#497) or an IKEA (#501) plans its own hall, so none of the house
        // rules below apply. Deliberately not an early return: Generate has one exit and one
        // Furnish, so anything that has to run over every finished plan (#498 cuts the facade
        // doors' doorways here) is written once and cannot miss either path.
        var site = BuildingTypes.SiteFor(fp.Key.ToString(), b.Kind, fp.Width, fp.Depth, b.MaxY - b.MinY);
        bool planned = type == BuildingType.Ikea
            ? TryIkea(layout, fp.Door.Height)
            : site != BuildingType.None && TryIndustrial(layout, site, fp.Door.Height, rng);
        // a block of flats (#557) plans its stairwells and flats itself, and its street doors with them
        var flats = !planned && site == BuildingType.None && type == BuildingType.None
            ? ApartmentTypeFor(fp, b.Kind, n, bank) : BuildingType.None;
        bool flatsPlanned = flats != BuildingType.None && TryApartments(layout, fp, b, b.Kind, n, flats, rng);
        planned |= flatsPlanned;
        if (!planned)
        {
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
        }

        // a doorway for every other facade door (#498), before the furniture, which keeps clear
        // of every opening by itself (Clearance)
        if (fp.Extra.Count > 0 && !flatsPlanned) Entrances(layout, fp);

        Furnish(layout, rng);
        return layout;
    }

    // ---- several ways in (#498) -------------------------------------------------------------

    /// <summary>Which way a room's wall faces, interior-local.</summary>
    private static (float X, float Z) Facing(Side s) => s switch
    {
        Side.Front => (0, -1),
        Side.Back => (0, 1),
        Side.Left => (-1, 0),
        _ => (1, 0),
    };

    /// <summary>
    /// One <see cref="EntrancePlan"/> per facade door, the main one first. A building's extra
    /// doors stand where the facade had room for them; the doorway inside is cut in the
    /// ground-floor room nearest each of them, on the wall facing the same way, clamped into the
    /// plan box. Inside and outside are not the same building — the plan box is a clamped
    /// rectangle over a TIN solid — so the two line up plausibly, not exactly.
    ///
    /// <para>
    /// A door the plan can fit no doorway for gets no entrance and reads as locked
    /// (<see cref="InteriorLayout.EntranceOf"/>): a facade door is worth more than a portal onto
    /// somebody else's doorway.
    /// </para>
    /// </summary>
    private static void Entrances(InteriorLayout l, Footprint fp)
    {
        var ground = l.GroundFloor;
        float clear = l.StoreyHeight - Slab;
        l.Entrances.Clear();
        // the main door, exactly as InteriorLayout.AllEntrances would have synthesised it
        l.Entrances.Add(new EntrancePlan
        {
            Door = new DoorKey(fp.Key).ToString(),
            X = l.EntryX, Z = -l.Depth / 2, InX = 0, InZ = 1, Width = l.EntryWidth,
            DoorX = l.DoorX, DoorY = l.DoorY, DoorZ = l.DoorZ,
            DoorOutX = l.DoorOutX, DoorOutZ = l.DoorOutZ,
            DoorWidth = l.DoorWidth, DoorHeight = fp.Door.Height,
            Hang = fp.Door.Hang, Vehicle = fp.Door.Vehicle,
        });

        var axisV = fp.AxisV;
        foreach (var d in fp.Extra)
        {
            var rel = new Godot.Vector2(d.Position.X - fp.Center.X, d.Position.Z - fp.Center.Y);
            var at = new Godot.Vector2(rel.Dot(fp.AxisU), rel.Dot(axisV));
            var outward = new Godot.Vector2(d.Outward.X, d.Outward.Z);
            var faces = new Godot.Vector2(outward.Dot(fp.AxisU), outward.Dot(axisV));
            // A pedestrian door's doorway is a doorway, whatever the door. A door DRIVEN through
            // keeps its full width, or a 4.5 m loading bay arrives at a 1.8 m hole on the inside
            // and a 1.8 m car wedges in it at the sill — which is exactly what the drive-through
            // found (#531): the bay opened, the portal was crossed, and the car stopped 3 cm in.
            float width = d.Vehicle ? d.Width : Math.Min(d.Width, 1.8f);
            if (width < 0.7f || Math.Min(d.Height, clear - 0.15f) < 1.9f) continue;

            // the wall that faces the same way first, then round the building
            foreach (var side in new[] { Side.Front, Side.Right, Side.Back, Side.Left }
                         .OrderByDescending(x => Facing(x).X * faces.X + Facing(x).Z * faces.Y)
                         .ThenBy(x => (int)x))
            {
                if (!FitEntry(l, ground, side, side is Side.Front or Side.Back ? at.X : at.Y, width, out var room, out float center))
                    continue;
                // under the ceiling of the room it actually opens into, not of the storey: a works
                // hall's service block is a 2.6 m room standing inside a 9 m hall (#497)
                float top = Math.Min(d.Height, l.ClearOf(room) - 0.15f);
                if (top < 1.9f) continue;
                room.Openings.Add(new OpeningPlan
                {
                    Side = side, Center = center, Width = width, Bottom = 0, Top = top, Kind = OpeningKind.Entry,
                });
                var (fx, fz) = Facing(side);
                var line = side switch
                {
                    Side.Front => new Godot.Vector2(center, room.Z0),
                    Side.Back => new Godot.Vector2(center, room.Z1),
                    Side.Left => new Godot.Vector2(room.X0, center),
                    _ => new Godot.Vector2(room.X1, center),
                };
                l.Entrances.Add(new EntrancePlan
                {
                    Door = new DoorKey(fp.Key, d.Slot).ToString(),
                    X = line.X, Z = line.Y, InX = -fx, InZ = -fz, Width = width,
                    DoorX = d.Position.X, DoorY = d.Position.Y, DoorZ = d.Position.Z,
                    DoorOutX = d.Outward.X, DoorOutZ = d.Outward.Z,
                    DoorWidth = d.Width, DoorHeight = d.Height, Hang = d.Hang, Vehicle = d.Vehicle,
                });
                break;
            }
        }
    }

    /// <summary>
    /// Where a doorway <paramref name="width"/> wide fits on one side of the plan box, as near
    /// <paramref name="want"/> along it as the rooms there allow: a ground-floor room whose wall
    /// is on that side of the box, with wall left over beside its other openings and no stair
    /// shaft to step into.
    /// </summary>
    private static bool FitEntry(InteriorLayout l, FloorPlan ground, Side side, float want, float width,
        out RoomPlan room, out float center, Func<RoomPlan, int>? rank = null)
    {
        room = null!;
        center = 0;
        float hw = l.Width / 2, hd = l.Depth / 2;
        bool along = side is Side.Front or Side.Back;
        var onSide = ground.Rooms.Where(r => side switch
        {
            Side.Front => r.Z0 <= -hd + 0.05f,
            Side.Back => r.Z1 >= hd - 0.05f,
            Side.Left => r.X0 <= -hw + 0.05f,
            _ => r.X1 >= hw - 0.05f,
        });
        if (rank != null) onSide = onSide.Where(r => rank(r) >= 0).OrderBy(rank);
        foreach (var r in rank == null
                     ? onSide.OrderBy(r => Math.Abs(Fit(want, along ? r.X0 : r.Z0, along ? r.X1 : r.Z1) - want))
                     : ((IOrderedEnumerable<RoomPlan>)onSide).ThenBy(r => Math.Abs(Fit(want, along ? r.X0 : r.Z0, along ? r.X1 : r.Z1) - want)))
        {
            float s0 = along ? r.X0 : r.Z0, s1 = along ? r.X1 : r.Z1;
            if (s1 - s0 < width + 0.4f) continue;
            float c = Fit(want, s0 + width / 2 + 0.2f, s1 - width / 2 - 0.2f);
            // not over another cut in the same wall, nor over the shaft of a stair from below
            if (r.Openings.Any(o => o.Kind != OpeningKind.Window && o.Side == side
                                    && Math.Abs(o.Center - c) < (o.Width + width) / 2 + 0.3f)) continue;
            var reach = along
                ? new RectPlan(c - width / 2, side == Side.Front ? r.Z0 : r.Z1 - 1.2f, c + width / 2, side == Side.Front ? r.Z0 + 1.2f : r.Z1)
                : new RectPlan(side == Side.Left ? r.X0 : r.X1 - 1.2f, c - width / 2, side == Side.Left ? r.X0 + 1.2f : r.X1, c + width / 2);
            if (ground.Holes.Any(h => h.Overlaps(reach))) continue;
            if (ground.AllFlights().Any(fl => fl.Area().Overlaps(reach)))
                continue;
            room = r;
            center = c;
            return true;
        }
        return false;
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
            // the facade door's own height, whatever the kind, which the footprint already kept
            // under the eave: the hole a player sees from the street and the one they walk
            // through is the same hole (#509). It was only the vehicle kinds that took it, so a
            // shed wore a 2.8 m door outside and a 2.1 m one inside.
            Top = Math.Min(doorHeight, clear - 0.15f),
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
                if (!exterior || r.Type is RoomType.Vault or RoomType.Shelter or RoomType.Elevator) continue;
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
                    // a doorway where the window would go (a ground-floor flat's garden door) moves
                    // the window aside within its own stretch of wall, rather than losing it (#571)
                    float lo = a + len * k / count + width / 2 + 0.2f;
                    float hi = a + len * (k + 1) / count - width / 2 - 0.2f;
                    for (float step = 0.3f; Blocked(r, side, c, width) && step < len; step += 0.3f)
                        foreach (float at in new[] { c - step, c + step })
                            if (at >= lo && at <= hi && !Blocked(r, side, at, width)) { c = at; break; }
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
    private static IEnumerable<Piece> Pieces(RoomType t, RoomPlan r, Random rng, BuildingKind kind, bool vending = false) =>
        BasePieces(t, r, rng).Concat(KindExtras(t, kind)).Concat(vending && VendingRoom(t, kind) ? new[] { VendingPiece } : Array.Empty<Piece>());

    private static readonly Piece VendingPiece = new(FurnitureType.VendingMachine, 0.9f, 0.8f, 1.85f, true);

    /// <summary>
    /// How many buildings of a kind have a PAUSA vending machine (#273): a school's or a hospital's
    /// hall about one in three, an office block's lobby one in five, a works' floor one in seven.
    /// A trade garage only ever has the room for one once it is a site with a mess room (#497).
    /// </summary>
    private static double VendingChance(BuildingKind kind) => kind switch
    {
        BuildingKind.Civic => 0.35,
        BuildingKind.Commercial => 0.20,
        BuildingKind.Industrial => 0.15,
        BuildingKind.Garage => 0.15,
        _ => 0,
    };

    /// <summary>Where it stands: the ground floor's lobby or hall, a works' hall or store.</summary>
    private static bool VendingRoom(RoomType t, BuildingKind kind) =>
        // a site's mess room is where one really stands (#497), whatever the building's cadastre kind
        t == RoomType.BreakRoom
        || (kind == BuildingKind.Industrial
            ? t is RoomType.Workshop or RoomType.Storage
            : t is RoomType.Lobby or RoomType.Hall);

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
        // ---- industrial sites (#497): the small rooms of a site's service block ----------------
        RoomType.Dispatch => new[]
        {
            new Piece(FurnitureType.Desk, 1.4f, 0.7f, 0.75f, true),
            new Piece(FurnitureType.DeskCounter, 1.8f, 0.7f, 1.1f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.9f, true),
            new Piece(FurnitureType.Whiteboard, 1.6f, 0.08f, 1.1f, true),
            new Piece(FurnitureType.Chair, 0.5f, 0.5f, 0.9f, false),
        },
        RoomType.ControlRoom => new[]
        {
            new Piece(FurnitureType.Desk, 1.8f, 0.8f, 0.75f, true),
            new Piece(FurnitureType.Tv, 1.0f, 0.4f, 0.9f, true),
            new Piece(FurnitureType.Whiteboard, 1.6f, 0.08f, 1.1f, true),
            new Piece(FurnitureType.Chair, 0.5f, 0.5f, 0.9f, false),
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.9f, true),
        },
        RoomType.LockerRoom => new[]
        {
            new Piece(FurnitureType.Locker, 1.2f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.Locker, 1.2f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.Locker, 1.2f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.Bench, 1.6f, 0.4f, 0.45f, true),
            new Piece(FurnitureType.HardHatRack, 1.0f, 0.3f, 1.8f, true),
            new Piece(FurnitureType.Sink, 0.6f, 0.45f, 0.85f, true),
            new Piece(FurnitureType.TimeClock, 0.25f, 0.12f, 0.3f, true),
        },
        RoomType.BreakRoom => new[]
        {
            new Piece(FurnitureType.Table, 1.6f, 0.85f, 0.75f, false),
            new Piece(FurnitureType.Chair, 0.5f, 0.5f, 0.9f, false),
            new Piece(FurnitureType.Chair, 0.5f, 0.5f, 0.9f, false),
            new Piece(FurnitureType.Counter, Math.Clamp(Math.Max(r.Width, r.Depth) - 1.6f, 1.0f, 2.4f), 0.6f, 0.9f, true),
            new Piece(FurnitureType.Fridge, 0.6f, 0.65f, 1.8f, true),
            new Piece(FurnitureType.Sink, 0.6f, 0.45f, 0.85f, true),
        },
        RoomType.PartsStore => new[]
        {
            new Piece(FurnitureType.Rack, 2.0f, 0.6f, 2.2f, true),
            new Piece(FurnitureType.Rack, 2.0f, 0.6f, 2.2f, true),
            new Piece(FurnitureType.Shelf, 1.2f, 0.45f, 1.9f, true),
            new Piece(FurnitureType.TyreStack, 0.8f, 0.8f, 1.2f, true),
            new Piece(FurnitureType.Crate, 0.8f, 0.8f, 0.7f, false),
        },
        RoomType.PaintBooth => new[]
        {
            new Piece(FurnitureType.Compressor, 0.9f, 0.6f, 1.1f, true),
            new Piece(FurnitureType.OilDrum, 0.6f, 0.6f, 0.9f, true),
            new Piece(FurnitureType.OilDrum, 0.6f, 0.6f, 0.9f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.9f, true),
            new Piece(FurnitureType.SafetySign, 0.5f, 0.06f, 0.7f, true),
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
        // a vending machine is the building's own dice roll, so it moves nothing else in the plan
        bool vending = !l.IsBank && Core.Fnv.Unit(l.Key + "|vending") < VendingChance(l.Kind);
        bool apt = l.Type is BuildingType.Apartments or BuildingType.MixedUse;
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
                // A door driven through needs a lane behind it, kept clear of furniture. Judged by
                // the OPENING, not by the building's kind: a works is not a `VehicleDoor` kind, yet
                // every one of its loading bays is driven through (#528), and furniture stood in
                // all of them — a car through a bay hit a lift and was shoved back into the yard,
                // which is what the drive-through found (#531). A pedestrian entrance is 1.0-1.8 m
                // and a vehicle one 2.8 m and up, so the width tells them apart with room to spare.
                var lanes = new List<RectPlan>();
                if (f == l.Below)
                    foreach (var o in r.Openings)
                        if (o.Kind == OpeningKind.Entry && o.Side == Side.Front && o.Width >= VehicleEntryWidth)
                            lanes.Add(Lane(l.Kind, r, o));
                // a hall that lays itself out takes its lanes in hand: a workshop's drive-on ramp
                // belongs IN the lane, everything else out of it (HallLayout)
                if (!LaysItselfOut(r.Type)) blocked.AddRange(lanes);
                // the stairwell is not somewhere to put a sofa. A site hall (#497) or a shop
                // floor (#501) is room 0 and holds no stair, so it is not one: the strip the core
                // keeps clear just inside the door would have blocked its whole front bay.
                bool isCore = ri == 0 && floor.Rooms.Count > 1 && !LaysItselfOut(r.Type) && !apt;
                // a block of flats has a stair in every stairwell (#557): each flight and a landing's
                // depth at both ends of it stays clear, wherever it stands
                if (apt)
                    foreach (var fl in floor.AllFlights())
                    {
                        var run = fl.Area(1.2f, 1.2f);
                        if (run.Overlaps(new RectPlan(r.X0, r.Z0, r.X1, r.Z1))) blocked.Add(run);
                    }
                if (isCore)
                {
                    float zs = FirstStairZ(l);
                    if (zs < r.Z1) blocked.Add(new RectPlan(r.X0, zs - 1.2f, r.X1, r.Z1));
                    blocked.Add(new RectPlan(r.X0, r.Z0, r.X1, r.Z0 + 1.4f)); // just inside the door
                }
                foreach (var h in floor.Holes) blocked.Add(h);

                // a site hall or a shop floor is aisles or rows, not pieces scattered round its
                // walls (#497, #501)
                if (LaysItselfOut(r.Type))
                {
                    HallLayout(l, f, r, placed, blocked, lanes, rng);
                    foreach (var p in HallDressing(r.Type))
                        TryPlace(l, f, r, p, placed, blocked, rng);
                    continue;
                }
                if (r.Type == RoomType.Nave) { Pews(l, f, r, placed, blocked); continue; }
                if (r.Type == RoomType.Classroom) Desks(l, f, r, placed, blocked);
                if (r.Type == RoomType.Vault) { Vault(l, f, r, placed, blocked, rng); continue; }
                if (r.Type == RoomType.CarPark) { CarPark(l, f, r, placed, blocked, rng); continue; }
                if (r.Type == RoomType.Cellar && apt) { Compartments(l, f, r, placed, blocked, rng); continue; }
                if (apt && AptPieces(r.Type) is { } own)
                {
                    foreach (var p in own) TryPlace(l, f, r, p, placed, blocked, rng);
                    continue;
                }

                foreach (var p in Pieces(r.Type, r, rng, l.Kind, vending && f == l.Below))
                    TryPlace(l, f, r, p, placed, blocked, rng);
                if (vending && l.Furniture.Count > 0 && l.Furniture[^1].Type == FurnitureType.VendingMachine) vending = false;
            }
        }
        if (l.IsBank) Counter(l, rooms, rng);
        if (l.Shop != Loot.ShopType.None) ShopCounter(l, rooms, rng);
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

    /// <summary>
    /// A shop must have its counter (#273), or its door sign promises nothing: when no shop room
    /// placed one (a garage has none of its own), a shorter one, then any ground-floor room.
    /// </summary>
    private static void ShopCounter(InteriorLayout l, List<(int Floor, RoomPlan Room, List<RectPlan> Placed, List<RectPlan> Blocked)> rooms, Random rng)
    {
        if (l.Furniture.Any(f => f.Type == FurnitureType.ShopCounter && f.Floor == l.Below)) return;
        var ground = rooms.Where(x => x.Floor == l.Below)
            .OrderByDescending(x => x.Room.Type is RoomType.Shop or RoomType.Garage).ThenByDescending(x => x.Room.Area).ToList();
        foreach (float width in new[] { 2.0f, 1.5f, 1.1f })
            foreach (var c in ground)
                foreach (bool wall in new[] { true, false })
                {
                    int before = l.Furniture.Count;
                    TryPlace(l, c.Floor, c.Room, new Piece(FurnitureType.ShopCounter, width, 0.6f, 1.0f, wall), c.Placed, c.Blocked, rng);
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

    /// <summary>The pastor rat (#241): robed, mitred, arms spread, by the altar of every church.</summary>
    private static readonly Piece RatPiece = new(FurnitureType.PastorRat, 0.9f, 0.45f, 1.35f, false);

    /// <summary>The church radio (#370): a boombox on a stand, by the rat, loaded with the chess type beat.</summary>
    private static readonly Piece RadioPiece = new(FurnitureType.ChurchRadio, 0.5f, 0.4f, 1.05f, false);

    /// <summary>
    /// Stands the church radio beside the pastor rat on floor <paramref name="f"/>, facing the way it
    /// faces: either side of it first, else in front, else behind. On the chancel step with the rat or
    /// clear of the step, never half on it. <paramref name="others"/> is what it must not overlap
    /// (the step left out when the rat stands on it); the radio's rect also goes into
    /// <paramref name="placed"/>, so the pews keep clear of it.
    /// </summary>
    private static void RadioByRat(InteriorLayout l, int f, RoomPlan room, List<RectPlan> placed, List<RectPlan> others,
        List<RectPlan> blocked, RectPlan? dais)
    {
        var rat = l.Furniture.LastOrDefault(p => p.Floor == f && p.Type == FurnitureType.PastorRat);
        if (rat == null) return;
        bool along = rat.Turns % 2 == 0;
        float rw = along ? rat.W : rat.D, rd = along ? rat.D : rat.W;
        float w = along ? RadioPiece.W : RadioPiece.D, d = along ? RadioPiece.D : RadioPiece.W;
        // the side of the rat it faces: +Z for 0 turns, -X for 1, -Z for 2, +X for 3
        var (fx, fz) = (rat.Turns & 3) switch { 0 => (0f, 1f), 1 => (-1f, 0f), 2 => (0f, -1f), _ => (1f, 0f) };
        var spots = new (float X, float Z)[]
        {
            (rat.X + (rw + w) / 2 + 0.15f, rat.Z), (rat.X - (rw + w) / 2 - 0.15f, rat.Z),
            (rat.X + fx * ((rw + w) / 2 + 0.35f), rat.Z + fz * ((rd + d) / 2 + 0.35f)),
            (rat.X - fx * ((rw + w) / 2 + 0.15f), rat.Z - fz * ((rd + d) / 2 + 0.15f)),
        };
        // a radio at the side of a rat facing ±X stands along Z
        if (!along) for (int i = 0; i < 2; i++) spots[i] = (rat.X, rat.Z + (i == 0 ? 1 : -1) * ((rd + d) / 2 + 0.15f));
        bool up = rat.Lift > 0;
        foreach (var (x, z) in spots)
        {
            var rect = new RectPlan(x - w / 2, z - d / 2, x + w / 2, z + d / 2);
            if (dais != null && (up ? !(rect.X0 >= dais.X0 && rect.X1 <= dais.X1 && rect.Z0 >= dais.Z0 && rect.Z1 <= dais.Z1) : rect.Overlaps(dais))) continue;
            if (!Free(room, rect, others, blocked, 0.05f)) continue;
            Add(l, f, RadioPiece, rect, rat.Turns, others);
            if (others != placed) placed.Add(rect);
            l.Furniture[^1].Lift = rat.Lift;
            return;
        }
    }

    private static void Pews(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked)
    {
        TryPlace(l, f, r, new Piece(FurnitureType.Altar, Math.Min(2.0f, r.Width * 0.4f), 0.9f, 1.0f, true),
            placed, blocked, new Random(0));
        TryPlace(l, f, r, RatPiece, placed, blocked, new Random(1));
        RadioByRat(l, f, r, placed, placed, blocked, null);
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
        FillFrontPews(l, f);
    }

    /// <summary>The row nearest the altar is full: the pastor rat's congregation (#241).</summary>
    private static void FillFrontPews(InteriorLayout l, int f)
    {
        var pews = l.Furniture.Where(p => p.Floor == f && p.Type == FurnitureType.Pew).ToList();
        if (pews.Count == 0) return;
        float front = pews.Max(p => p.Z);
        foreach (var p in pews)
            if (p.Z > front - 0.05f) p.Type = FurnitureType.FrontPew;
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
    public static int StableHash(string s) => Core.Fnv.Hash(s);
}
