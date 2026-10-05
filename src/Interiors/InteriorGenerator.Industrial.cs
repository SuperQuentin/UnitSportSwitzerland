using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Industrial sites (#497): the inside of a warehouse, a works, a haulier's depot, a body shop and a
/// car dealership, instead of the one bare <see cref="RoomType.Workshop"/> every industrial solid
/// used to get.
///
/// <para>
/// A site is one <b>hall</b> rising through the building's full height — `.bldg` industrial solids
/// always plan as a single tall storey (<see cref="InteriorGenerator.Storeys"/>) — plus, where there
/// is room, a low <b>service block</b> of small rooms built against the back or a side wall: the
/// goods office, the mess room, the locker room, the parts store. The block is a real room with its
/// own 2.6 m ceiling standing inside the tall hall (<see cref="RoomPlan.Clear"/>), which is how
/// these buildings are actually built, and it costs no stairs.
/// </para>
///
/// <para>
/// The hall itself is not furnished by the generic wall-hugging placer: a warehouse is <i>aisles</i>
/// and a works is <i>lines</i>, and both read as wrong if the pieces are scattered. Each site type
/// lays its hall out itself (<see cref="Aisles"/>, <see cref="Line"/>, <see cref="Bays"/>,
/// <see cref="ShowroomFloor"/>), leaving the entry lane clear, and then the generic placer dresses
/// the walls with the safety kit every Swiss works has by law.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>Headroom of the service block built inside a tall hall.</summary>
    private const float BlockClear = 2.6f;
    /// <summary>Depth of that block, front to back.</summary>
    private const float BlockDepth = 3.4f;
    /// <summary>A hall needs at least this much of its own depth left once the block is taken out.</summary>
    private const float HallMinDepth = 7f;
    /// <summary>And this much width, or there is no room for an aisle beside the entry lane.</summary>
    private const float HallMinWidth = 7f;
    /// <summary>Every service room is at least this wide, or it is dropped from the program.</summary>
    private const float ServiceMinWidth = 2.4f;

    /// <summary>
    /// Plans a site, or returns false to leave the building to the ordinary rules. Fails only on a
    /// footprint too small to hold a hall worth walking into, which <see cref="BuildingTypes.SiteFor"/>
    /// has mostly ruled out already.
    /// </summary>
    internal static bool TryIndustrial(InteriorLayout l, BuildingType site, float doorHeight, Random rng)
    {
        float W = l.Width, D = l.Depth;
        if (W < HallMinWidth || D < HallMinDepth) return false;

        float clear = l.StoreyHeight - Slab;
        float hw = W / 2, hd = D / 2;
        l.Type = site;
        l.Below = 0;
        l.Floors.Clear();
        var floor = new FloorPlan();
        l.Floors.Add(floor);

        // ---- the hall, and the low service block behind it -----------------------------------
        var program = ServiceProgram(site, rng).ToList();
        // the block only goes in when the hall keeps its own depth and the rooms are usable
        bool block = program.Count > 0 && D - BlockDepth >= HallMinDepth
            && W >= ServiceMinWidth * 2 && clear >= BlockClear + 0.8f;
        float hallZ1 = block ? hd - BlockDepth : hd;

        var hall = new RoomPlan
        {
            X0 = -hw, Z0 = -hd, X1 = hw, Z1 = hallZ1,
            Type = HallRoom(site),
        };
        floor.Rooms.Add(hall);

        // the entry: as wide and tall as the facade door for the halls a vehicle drives into, so the
        // opening inside matches the roll-up door outside
        bool driven = BuildingTypes.DrivenInto(site);
        l.EntryWidth = Math.Min(driven ? Math.Max(l.DoorWidth, 2.8f) : l.DoorWidth, W - 1.2f);
        l.EntryX = Fit(l.EntryX, -hw + l.EntryWidth / 2 + 0.3f, hw - l.EntryWidth / 2 - 0.3f);
        float entryTop = Math.Min(driven ? Math.Max(doorHeight, 3.2f) : 2.4f, clear - 0.3f);
        hall.Openings.Add(new OpeningPlan
        {
            Side = Side.Front, Center = l.EntryX, Width = l.EntryWidth, Bottom = 0,
            Top = entryTop, Kind = OpeningKind.Entry,
        });

        if (block) ServiceBlock(l, floor, hall, program, hd);
        AddWindows(l, floor, 0);
        return true;
    }

    /// <summary>The hall's room type, which is what decides its floor, its light and its layout.</summary>
    private static RoomType HallRoom(BuildingType site) => site switch
    {
        BuildingType.Warehouse => RoomType.WarehouseHall,
        BuildingType.Factory => RoomType.ProductionHall,
        BuildingType.Depot => RoomType.TruckBay,
        BuildingType.Mechanic => RoomType.ServiceBay,
        _ => RoomType.Showroom,
    };

    /// <summary>
    /// The small rooms along the back wall, street end first as the rest of the generator orders
    /// them: what the site needs most, so a short wall still gets the office. A works has a control
    /// room looking over the floor; a depot a dispatch office; a dealership a sales office and no
    /// locker room.
    /// </summary>
    private static IEnumerable<RoomType> ServiceProgram(BuildingType site, Random rng)
    {
        switch (site)
        {
            case BuildingType.Warehouse:
                yield return RoomType.Dispatch;
                yield return RoomType.PartsStore;
                yield return RoomType.BreakRoom;
                yield return RoomType.WC;
                break;
            case BuildingType.Factory:
                yield return RoomType.ControlRoom;
                yield return RoomType.LockerRoom;
                yield return RoomType.BreakRoom;
                yield return RoomType.WC;
                if (rng.NextDouble() < 0.35) yield return RoomType.PaintBooth;
                break;
            case BuildingType.Depot:
                yield return RoomType.Dispatch;
                yield return RoomType.PartsStore;
                yield return RoomType.BreakRoom;
                yield return RoomType.WC;
                break;
            case BuildingType.Mechanic:
                yield return RoomType.Office;
                yield return RoomType.PartsStore;
                yield return RoomType.WC;
                break;
            default: // dealership
                yield return RoomType.Office;
                yield return RoomType.BreakRoom;
                yield return RoomType.WC;
                break;
        }
    }

    /// <summary>
    /// Cuts the block behind the hall into as many of <paramref name="program"/>'s rooms as fit, each
    /// with a doorway into the hall. Rooms are the same depth and share the block's back wall, so
    /// from the hall it reads as one low building inside the big one.
    /// </summary>
    private static void ServiceBlock(InteriorLayout l, FloorPlan floor, RoomPlan hall,
        List<RoomType> program, float hd)
    {
        float hw = l.Width / 2;
        float z0 = hall.Z1, z1 = hd;
        int count = Math.Clamp((int)(l.Width / 3.6f), 1, program.Count);
        // a WC is the one room allowed to be narrow; everything else wants its share
        float each = l.Width / count;
        if (each < ServiceMinWidth) count = Math.Max(1, (int)(l.Width / ServiceMinWidth));
        each = l.Width / count;

        for (int i = 0; i < count; i++)
        {
            var room = new RoomPlan
            {
                X0 = -hw + each * i, Z0 = z0, X1 = -hw + each * (i + 1), Z1 = z1,
                Type = program[i],
                Clear = BlockClear,
            };
            floor.Rooms.Add(room);
            // the doorway into the hall, on the room's own front wall
            float center = (room.X0 + room.X1) / 2;
            float top = Math.Min(2.05f, BlockClear - 0.25f);
            room.Openings.Add(new OpeningPlan
            {
                Side = Side.Front, Center = center, Width = InnerDoor, Top = top,
                Kind = OpeningKind.Door, Other = 0,
            });
            hall.Openings.Add(new OpeningPlan
            {
                Side = Side.Back, Center = center, Width = InnerDoor, Top = top,
                Kind = OpeningKind.Door, Other = floor.Rooms.Count - 1,
            });
        }
    }

    // ---- hall layouts ---------------------------------------------------------------------------

    /// <summary>
    /// Whether this room lays itself out rather than being filled by the generic placer, and does so
    /// instead of the room's own <see cref="BasePieces"/>.
    /// </summary>
    private static bool LaysItselfOut(RoomType t) =>
        t is RoomType.WarehouseHall or RoomType.ProductionHall or RoomType.TruckBay
            or RoomType.ServiceBay or RoomType.Showroom
            or RoomType.IkeaMarket;        // #501, laid out in InteriorGenerator.Ikea

    /// <summary>Lays out one site hall; the wall dressing is added afterwards by the generic placer.</summary>
    private static void HallLayout(InteriorLayout l, int f, RoomPlan r,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        switch (r.Type)
        {
            case RoomType.WarehouseHall: Aisles(l, f, r, placed, blocked, rng); break;
            case RoomType.ProductionHall: Line(l, f, r, placed, blocked, rng); break;
            case RoomType.TruckBay: Bays(l, f, r, placed, blocked, rng, truck: true); break;
            case RoomType.ServiceBay: Bays(l, f, r, placed, blocked, rng, truck: false); break;
            case RoomType.IkeaMarket: BlahajBins(l, f, r, placed, blocked); break;   // #501
            default: ShowroomFloor(l, f, r, placed, blocked, rng); break;
        }
    }

    /// <summary>The lane in from the hall's entry, which nothing may stand in.</summary>
    private static RectPlan EntryLane(InteriorLayout l, RoomPlan r)
    {
        var entry = r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Entry);
        float half = (entry?.Width ?? l.EntryWidth) / 2 + 0.6f;
        float center = entry?.Center ?? l.EntryX;
        return new RectPlan(center - half, r.Z0, center + half, r.Z1);
    }

    private static void Put(InteriorLayout l, int f, FurnitureType type, float x, float z, int turns,
        float w, float d, float h, List<RectPlan> placed, float lift = 0f)
    {
        l.Furniture.Add(new FurniturePlan
        {
            Type = type, Floor = f, X = x, Z = z, Turns = turns, W = w, D = d, H = h, Lift = lift,
        });
        // paint on the floor and a beam under the roof take no room: a lift stands on its marked-out
        // bay, and a rack row runs under the crane
        if (type is FurnitureType.FloorMarking or FurnitureType.Gantry) return;
        // the footprint a turned piece really takes, so the next row keeps off it
        float ew = turns % 2 == 0 ? w : d, ed = turns % 2 == 0 ? d : w;
        placed.Add(new RectPlan(x - ew / 2, z - ed / 2, x + ew / 2, z + ed / 2));
    }

    /// <summary>
    /// How many pieces a hall may hold: one per this many square metres, and never more than
    /// <see cref="MaxHallPieces"/>. A racking bay is the most expensive piece in the game (uprights,
    /// beams, pallets and their goods, ~1,200 vertices), and a clamped 120 x 120 m warehouse would
    /// otherwise plan 1,500 of them.
    /// </summary>
    private static int PieceBudget(RoomPlan r) => Math.Clamp((int)(r.Area / 9f), 8, MaxHallPieces);

    /// <summary>Pieces in one hall, whatever its size: ~130k vertices of interior at the top end.</summary>
    private const int MaxHallPieces = 110;

    private const float RackBay = 2.7f, RackDepth = 1.1f, Aisle = 2.9f;

    /// <summary>
    /// A warehouse: back-to-back runs of pallet racking down the hall with aisles between them, each
    /// run broken into bays, and pallets and drums standing out in the aisle ends. The runs lie
    /// across the hall's short axis so the aisles point at the doors, as a forklift needs.
    /// </summary>
    private static void Aisles(InteriorLayout l, int f, RoomPlan r,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        blocked.Add(EntryLane(l, r));
        float h = Math.Min(l.ClearOf(r) - 0.8f, 6.0f);
        int budget = PieceBudget(r);

        // pairs of runs, back to back, with an aisle either side: 2·depth + aisle per pitch
        float pitch = 2 * RackDepth + Aisle;
        int rows = Math.Max(1, (int)((r.Depth - 1.6f - Aisle) / pitch));
        float used = rows * pitch + Aisle;
        float z = r.Z0 + (r.Depth - used) / 2 + Aisle;
        for (int row = 0; row < rows && budget > 0; row++, z += pitch)
        {
            foreach (float zr in new[] { z, z + RackDepth })
            {
                for (float x = r.X0 + 0.4f; x + RackBay < r.X1 - 0.4f && budget > 0; x += RackBay)
                {
                    var rect = new RectPlan(x, zr, x + RackBay, zr + RackDepth);
                    // no gap: a run is continuous and its two halves stand back to back, which is
                    // the whole point of pallet racking
                    if (!Free(r, rect, placed, blocked, 0f)) continue;
                    Put(l, f, FurnitureType.PalletRack, x + RackBay / 2, zr + RackDepth / 2, 0,
                        RackBay, RackDepth, h, placed);
                    budget--;
                }
            }
            // the aisle floor line, and a pallet or a drum stack left out in it
            float lane = z - Aisle / 2;
            Put(l, f, FurnitureType.FloorMarking, (r.X0 + r.X1) / 2, lane, 0,
                r.Width - 0.6f, 0.12f, 0.02f, placed);
            for (int k = 0; k < 2 && budget > 0; k++)
            {
                float x = r.X0 + r.Width * (0.25f + 0.5f * (float)rng.NextDouble());
                var type = rng.NextDouble() < 0.6 ? FurnitureType.Pallet : FurnitureType.BarrelStack;
                float w = type == FurnitureType.Pallet ? 1.2f : 1.0f;
                var rect = new RectPlan(x - w / 2, lane - w / 2, x + w / 2, lane + w / 2);
                if (!Free(r, rect, placed, blocked, 0.2f)) continue;
                Put(l, f, type, x, lane, 0, w, w, type == FurnitureType.Pallet ? 1.1f : 1.0f, placed);
                budget--;
            }
        }
        Forklift(l, f, r, placed, blocked, rng);
    }

    /// <summary>
    /// A works: one to three conveyor runs down the long axis with machines beside them, pallets of
    /// stock at the feeding end and a gantry beam overhead.
    /// </summary>
    private static void Line(InteriorLayout l, int f, RoomPlan r,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        blocked.Add(EntryLane(l, r));
        int budget = PieceBudget(r);
        bool alongX = r.Width >= r.Depth;
        float span = alongX ? r.Width : r.Depth;      // the run's length
        float across = alongX ? r.Depth : r.Width;    // what the runs are stacked across
        int lines = Math.Clamp((int)((across - 3.0f) / 4.6f), 1, 3);

        for (int i = 0; i < lines && budget > 0; i++)
        {
            float t = (alongX ? r.Z0 : r.X0) + across * (i + 1) / (lines + 1);
            // the belt, in sections so a blocked stretch only loses that stretch
            float a0 = (alongX ? r.X0 : r.Z0) + 1.2f;
            for (float a = a0; a + 2.4f < a0 + span - 2.4f && budget > 0; a += 2.4f)
            {
                float x = alongX ? a + 1.2f : t, z = alongX ? t : a + 1.2f;
                var rect = alongX
                    ? new RectPlan(a, t - 0.45f, a + 2.4f, t + 0.45f)
                    : new RectPlan(t - 0.45f, a, t + 0.45f, a + 2.4f);
                if (!Free(r, rect, placed, blocked, 0f)) continue;   // sections lie end to end
                // turns 0 lays the piece's width along X; a run down Z needs the quarter turn
                Put(l, f, FurnitureType.Conveyor, x, z, alongX ? 0 : 1, 2.4f, 0.9f, 0.85f, placed);
                budget--;
            }
            // machinery along the line, alternating sides
            for (int k = 0; k < 4 && budget > 0; k++)
            {
                float a = a0 + span * (0.18f + 0.21f * k);
                float off = (k % 2 == 0 ? -1 : 1) * 1.9f;
                float x = alongX ? a : t + off, z = alongX ? t + off : a;
                var rect = new RectPlan(x - 0.8f, z - 0.7f, x + 0.8f, z + 0.7f);
                if (!Free(r, rect, placed, blocked, 0.2f)) continue;
                // facing the line it serves: back to the belt's far side
                int face = alongX ? (off < 0 ? 0 : 2) : (off < 0 ? 3 : 1);
                Put(l, f, FurnitureType.Machine, x, z, face, 1.6f, 1.4f,
                    1.4f + 0.5f * (float)rng.NextDouble(), placed);
                budget--;
            }
            // a gantry beam over the line, up out of the way, and a marked walkway beside it
            float lift = Math.Min(l.ClearOf(r) - 1.2f, 5.2f);
            if (lift > 3.2f)
                Put(l, f, FurnitureType.Gantry,
                    alongX ? (r.X0 + r.X1) / 2 : t, alongX ? t : (r.Z0 + r.Z1) / 2,
                    alongX ? 0 : 1, span - 2f, 0.5f, 0.9f, placed, lift);
            // the yellow pedestrian walkway down the side of the line, which every works has
            float walk = t + (i == 0 ? -1 : 1) * 2.9f;
            if (alongX && walk > r.Z0 + 0.4f && walk < r.Z1 - 0.4f)
                Put(l, f, FurnitureType.FloorMarking, (r.X0 + r.X1) / 2, walk, 0,
                    r.Width - 0.8f, 0.12f, 0.02f, placed);
            else if (!alongX && walk > r.X0 + 0.4f && walk < r.X1 - 0.4f)
                Put(l, f, FurnitureType.FloorMarking, walk, (r.Z0 + r.Z1) / 2, 0,
                    0.12f, r.Depth - 0.8f, 0.02f, placed);
        }

        // raw stock and finished pallets at the ends
        for (int k = 0; k < 6 && budget > 0; k++)
        {
            float x = r.X0 + r.Width * (0.12f + 0.76f * (float)rng.NextDouble());
            float z = r.Z0 + r.Depth * (k < 3 ? 0.1f : 0.9f);
            var type = k % 3 == 0 ? FurnitureType.SackStack : FurnitureType.Pallet;
            var rect = new RectPlan(x - 0.7f, z - 0.7f, x + 0.7f, z + 0.7f);
            if (!Free(r, rect, placed, blocked, 0.2f)) continue;
            Put(l, f, type, x, z, 0, 1.2f, 1.0f, type == FurnitureType.SackStack ? 1.0f : 1.1f, placed);
            budget--;
        }
        Forklift(l, f, r, placed, blocked, rng);
    }

    /// <summary>
    /// A workshop: deep bays off the entry wall, each with a lift (a truck depot's over a pit, a body
    /// shop's a two-post ramp) and a tool chest at its head, tyres and drums along the walls, and a
    /// vehicle standing in one of them.
    /// </summary>
    private static void Bays(InteriorLayout l, int f, RoomPlan r,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng, bool truck)
    {
        float bayW = truck ? 4.2f : 3.4f;
        float bayD = Math.Min(r.Depth - 1.2f, truck ? 14f : 7.5f);
        int bays = Math.Clamp((int)(r.Width / bayW), 1, truck ? 4 : 5);
        float lane = r.Width / bays;
        int budget = PieceBudget(r);
        int withVehicle = rng.Next(bays);

        for (int i = 0; i < bays && budget > 0; i++)
        {
            float x = r.X0 + lane * (i + 0.5f);
            float z0 = r.Z0 + 0.6f;
            // the bay's own floor, marked out
            Put(l, f, FurnitureType.FloorMarking, x, z0 + bayD / 2, 0,
                Math.Min(bayW, lane - 0.3f), bayD, 0.02f, placed);

            float liftZ = z0 + bayD * 0.45f, liftH = truck ? 0.5f : 0.35f;
            var liftRect = new RectPlan(x - 1.3f, liftZ - 2.2f, x + 1.3f, liftZ + 2.2f);
            bool lift = Free(r, liftRect, placed, blocked, 0.1f);
            if (lift)
            {
                Put(l, f, FurnitureType.CarLift, x, liftZ, 0, 2.6f, 4.4f, liftH, placed);
                budget--;
            }
            // the one being worked on, up on the ramp: a lift is stood on, like a showroom plinth,
            // so the two are allowed to share their ground (InteriorValidator)
            if (i == withVehicle && lift)
            {
                float vw = truck ? 2.5f : 1.8f, vd = truck ? 6.5f : 4.2f;
                var rect = new RectPlan(x - vw / 2, liftZ - vd / 2, x + vw / 2, liftZ + vd / 2);
                // only its own bay: it may overlap the lift it stands on, nothing else
                if (rect.X0 > r.X0 + 0.2f && rect.X1 < r.X1 - 0.2f
                    && rect.Z0 > r.Z0 + 0.2f && rect.Z1 < r.Z1 - 0.2f
                    && !blocked.Any(q => q.Overlaps(rect))
                    && !placed.Any(q => q.Overlaps(rect) && !q.Overlaps(liftRect, -0.5f)))
                {
                    Put(l, f, truck ? FurnitureType.TruckProp : FurnitureType.Car, x, liftZ, 0,
                        vw, vd, truck ? 3.4f : 1.4f, placed, liftH);
                    budget--;
                }
            }
            // the chest at the head of the bay, against the back of the hall
            var chest = new RectPlan(x - 0.6f, r.Z1 - 0.9f, x + 0.6f, r.Z1 - 0.3f);
            if (Free(r, chest, placed, blocked, 0.1f))
            {
                Put(l, f, FurnitureType.ToolChest, x, r.Z1 - 0.6f, 0, 1.2f, 0.6f, 1.0f, placed);
                budget--;
            }
        }
    }

    /// <summary>
    /// A showroom: cars on low plinths in a grid with room to walk between them, a sales counter near
    /// the door and banners on the walls.
    /// </summary>
    private static void ShowroomFloor(InteriorLayout l, int f, RoomPlan r,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        const float cellW = 3.6f, cellD = 6.2f;
        int cols = Math.Max(1, (int)(r.Width / cellW));
        int rows = Math.Max(1, (int)(r.Depth / cellD));
        int budget = Math.Min(PieceBudget(r), cols * rows);

        for (int cz = 0; cz < rows; cz++)
            for (int cx = 0; cx < cols && budget > 0; cx++)
            {
                float x = r.X0 + r.Width * (cx + 0.5f) / cols;
                float z = r.Z0 + r.Depth * (cz + 0.5f) / rows;
                var rect = new RectPlan(x - 1.3f, z - 2.5f, x + 1.3f, z + 2.5f);
                if (!Free(r, rect, placed, blocked, 0.2f)) continue;
                Put(l, f, FurnitureType.ShowroomPlinth, x, z, 0, 2.6f, 5.0f, 0.18f, placed);
                // the car on it, turned a little so the row is not a car park
                l.Furniture.Add(new FurniturePlan
                {
                    Type = FurnitureType.Car, Floor = f, X = x, Z = z,
                    Turns = rng.NextDouble() < 0.5 ? 0 : 2, W = 1.8f, D = 4.3f, H = 1.4f, Lift = 0.18f,
                });
                budget--;
            }
    }

    /// <summary>A forklift parked out of the way: against a wall, nose in, where one still fits.</summary>
    private static void Forklift(InteriorLayout l, int f, RoomPlan r,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        for (int k = 0; k < 6; k++)
        {
            float x = r.X0 + r.Width * (0.1f + 0.8f * (float)rng.NextDouble());
            float z = r.Z0 + r.Depth * (k < 3 ? 0.08f : 0.92f);
            var rect = new RectPlan(x - 0.7f, z - 1.1f, x + 0.7f, z + 1.1f);
            if (!Free(r, rect, placed, blocked, 0.2f)) continue;
            Put(l, f, FurnitureType.Forklift, x, z, k < 3 ? 2 : 0, 1.3f, 2.1f, 2.0f, placed);
            return;
        }
    }

    // ---- wall dressing --------------------------------------------------------------------------

    /// <summary>
    /// What hangs on a site hall's walls once its floor is laid out: the safety kit a Swiss works has
    /// by law, and the signs that say which works it is. Placed by the generic wall placer, so they
    /// only take space the layout left.
    /// </summary>
    private static IEnumerable<Piece> HallDressing(RoomType t) => t switch
    {
        RoomType.WarehouseHall => new[]
        {
            new Piece(FurnitureType.SafetySign, 0.5f, 0.06f, 0.7f, true),
            new Piece(FurnitureType.SafetySign, 0.5f, 0.06f, 0.7f, true),
            new Piece(FurnitureType.FireExtinguisher, 0.25f, 0.25f, 0.7f, true),
            new Piece(FurnitureType.HardHatRack, 1.0f, 0.3f, 1.8f, true),
            new Piece(FurnitureType.Pallet, 1.2f, 1.0f, 1.1f, true),
            new Piece(FurnitureType.OilDrum, 0.6f, 0.6f, 0.9f, true),
        },
        RoomType.ProductionHall => new[]
        {
            new Piece(FurnitureType.SafetySign, 0.5f, 0.06f, 0.7f, true),
            new Piece(FurnitureType.SafetySign, 0.5f, 0.06f, 0.7f, true),
            new Piece(FurnitureType.FireExtinguisher, 0.25f, 0.25f, 0.7f, true),
            new Piece(FurnitureType.HardHatRack, 1.0f, 0.3f, 1.8f, true),
            new Piece(FurnitureType.Compressor, 0.9f, 0.6f, 1.1f, true),
            new Piece(FurnitureType.Workbench, 2.0f, 0.75f, 0.9f, true),
            new Piece(FurnitureType.Rack, 2.0f, 0.6f, 2.2f, true),
        },
        RoomType.TruckBay or RoomType.ServiceBay => new[]
        {
            new Piece(FurnitureType.Workbench, 2.2f, 0.75f, 0.9f, true),
            new Piece(FurnitureType.Rack, 2.0f, 0.6f, 2.2f, true),
            new Piece(FurnitureType.TyreStack, 0.8f, 0.8f, 1.2f, true),
            new Piece(FurnitureType.TyreStack, 0.8f, 0.8f, 0.9f, true),
            new Piece(FurnitureType.OilDrum, 0.6f, 0.6f, 0.9f, true),
            new Piece(FurnitureType.JerryCan, 0.3f, 0.22f, 0.45f, true),
            new Piece(FurnitureType.Compressor, 0.9f, 0.6f, 1.1f, true),
            new Piece(FurnitureType.FireExtinguisher, 0.25f, 0.25f, 0.7f, true),
            new Piece(FurnitureType.SafetySign, 0.5f, 0.06f, 0.7f, true),
        },
        RoomType.Showroom => new[]
        {
            new Piece(FurnitureType.DeskCounter, 2.4f, 0.8f, 1.1f, true),
            new Piece(FurnitureType.Banner, 1.2f, 0.08f, 2.4f, true),
            new Piece(FurnitureType.Banner, 1.2f, 0.08f, 2.4f, true),
            new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true),
            new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true),
            new Piece(FurnitureType.Armchair, 0.8f, 0.8f, 0.9f, true),
            new Piece(FurnitureType.Armchair, 0.8f, 0.8f, 0.9f, true),
        },
        _ => Array.Empty<Piece>(),
    };
}
