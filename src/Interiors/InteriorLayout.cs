using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

public enum RoomType
{
    Hall, Living, Kitchen, Dining, Bedroom, Bathroom, WC, Office, Shop, Storage,
    Classroom, Nave, Workshop, Barn, Garage, Lobby, Landing,
    // stored plans hold these as numbers: new types go on the end
    Porch, Belfry,
    // #213: more variety, basements, banks
    Laundry, GuestRoom, HomeCinema, Carnotzet, MusicRoom, Shelter, Cellar, Playroom, Study, Pantry,
    BankHall, Vault,
    // #497: industrial sites
    WarehouseHall, ProductionHall, TruckBay, ServiceBay, Showroom,
    ControlRoom, LockerRoom, BreakRoom, PartsStore, Dispatch, PaintBooth,
    /// <summary>The blue box's shop floor: one hall, full height (#501). Numbered, to pin it.</summary>
    IkeaMarket = 42,
    // #557: apartment blocks
    Elevator, CarPark, TechRoom, Corridor,
    // #571: a block of flats' own stair, round a half landing
    Stairwell,
    // #558: the ramp down to an apartment block's car park, behind its garage door
    Ramp,
}

public enum OpeningKind { Door, Window, Entry, Arch }

/// <summary>Which wall of a room: 0 = −Z (front), 1 = +X, 2 = +Z (back), 3 = −X.</summary>
public enum Side { Front = 0, Right = 1, Back = 2, Left = 3 }

/// <summary>
/// A cut in one room's wall. <see cref="Center"/> is the absolute interior-local coordinate
/// along the wall — X for front/back walls, Z for side walls — so the two rooms either side of
/// a doorway carry identical numbers and their cuts line up.
/// </summary>
public sealed class OpeningPlan
{
    public Side Side { get; set; }
    public float Center { get; set; }
    public float Width { get; set; }
    public float Bottom { get; set; }
    public float Top { get; set; }
    public OpeningKind Kind { get; set; }
    /// <summary>Index of the room on the other side, −1 for the outside.</summary>
    public int Other { get; set; } = -1;
}

public sealed class RoomPlan
{
    public float X0 { get; set; }
    public float Z0 { get; set; }
    public float X1 { get; set; }
    public float Z1 { get; set; }
    public RoomType Type { get; set; }
    /// <summary>Rooms sharing a unit id belong to one flat/office; −1 for circulation.</summary>
    public int Unit { get; set; } = -1;
    /// <summary>
    /// Storeys the room is tall: a nave rises through several while the tower beside it has a
    /// floor per storey. The floors it reaches into must leave its rectangle empty.
    /// </summary>
    public int Span { get; set; } = 1;
    /// <summary>
    /// Headroom in metres, when it is not <see cref="Span"/> whole storeys: the goods office and the
    /// mess room built as a low block inside a 9 m works hall (#497). 0 = derived from
    /// <see cref="Span"/> (<see cref="InteriorLayout.ClearOf"/>).
    /// </summary>
    public float Clear { get; set; }
    public List<OpeningPlan> Openings { get; set; } = new();

    public float Width => X1 - X0;
    public float Depth => Z1 - Z0;
    public float Area => Width * Depth;
}

/// <summary>An axis-aligned rectangle in interior-local X/Z.</summary>
public sealed class RectPlan
{
    public float X0 { get; set; }
    public float Z0 { get; set; }
    public float X1 { get; set; }
    public float Z1 { get; set; }
    public RectPlan() { }
    public RectPlan(float x0, float z0, float x1, float z1) { X0 = x0; Z0 = z0; X1 = x1; Z1 = z1; }
    public bool Overlaps(RectPlan o, float eps = 0.01f) =>
        X0 < o.X1 - eps && o.X0 < X1 - eps && Z0 < o.Z1 - eps && o.Z0 < Z1 - eps;
    /// <summary>The same rectangle, <paramref name="by"/> larger on every side.</summary>
    public RectPlan Grow(float by) => new(X0 - by, Z0 - by, X1 + by, Z1 + by);
}

/// <summary>One straight flight from this floor to the next: lane X0..X1, bottom at ZBottom, top at ZTop.</summary>
public sealed class FlightPlan
{
    public float X0 { get; set; }
    public float X1 { get; set; }
    public float ZBottom { get; set; }
    public float ZTop { get; set; }
    /// <summary>
    /// Where it starts and ends, as a fraction of the storey above its floor: a house's flight
    /// climbs a whole storey (0 to 1); a block of flats' stairwell (#571) climbs it in two
    /// flights, 0 to 0.5 to a half landing and 0.5 to 1 from it.
    /// </summary>
    public float From { get; set; }
    public float To { get; set; } = 1f;
    /// <summary>The side a solid parapet with a handrail runs along: -1 its X0 edge, +1 its X1, 0 none.</summary>
    public int Parapet { get; set; }

    /// <summary>Whether this flight is half of a stairwell's turn rather than a whole storey's stair.</summary>
    public bool Half => From > 0 || To < 1;

    /// <summary>
    /// A flight running along X rather than Z (#577: a wing of a building turned a quarter from
    /// the plan's frame). Then <see cref="X0"/>/<see cref="X1"/> are its lane's extent in Z and
    /// <see cref="ZBottom"/>/<see cref="ZTop"/> are X positions; read it through
    /// <see cref="Point"/> and <see cref="Area"/>.
    /// </summary>
    public bool AlongX { get; set; }

    /// <summary>
    /// A garage's ramp (#558), not a stair: a smooth wedge a car drives down (<see cref="RampProfile"/>),
    /// from <see cref="ZTop"/> (the end of the flat apron, at the storey above's floor level) to
    /// <see cref="ZBottom"/> (its foot on this floor). It may run on past the room it starts in,
    /// into the car park.
    /// </summary>
    public bool Ramp { get; set; }

    /// <summary>A point of the flight's own frame (across its lane, along its run) in the plan's (X, Z).</summary>
    public (float X, float Z) Point(float across, float along) => AlongX ? (along, across) : (across, along);

    /// <summary>The middle of its bottom and top ends, plan (X, Z).</summary>
    public (float X, float Z) Bottom => Point((X0 + X1) / 2, ZBottom);
    public (float X, float Z) TopEnd => Point((X0 + X1) / 2, ZTop);

    /// <summary>Which way a ramp's run goes along its axis: +1 from <see cref="ZTop"/> toward larger values (always, for a ramp square to the front wall), -1 the other way.</summary>
    public int RunDir => ZBottom >= ZTop ? 1 : -1;

    /// <summary>
    /// The ground round the end of a ramp's run, plan frame: from <paramref name="before"/> metres up the run from the foot to
    /// <paramref name="after"/> metres past it, and <paramref name="side"/> metres beyond the lane each side.
    /// </summary>
    public RectPlan FootZone(float before, float after, float side)
    {
        float a0 = ZBottom - RunDir * before, a1 = ZBottom + RunDir * after;
        float lo = Math.Min(a0, a1), hi = Math.Max(a0, a1);
        return AlongX ? new RectPlan(lo, X0 - side, hi, X1 + side) : new RectPlan(X0 - side, lo, X1 + side, hi);
    }

    /// <summary>The rectangle it stands on, plan frame.</summary>
    public RectPlan Area(float before = 0, float after = 0)
    {
        float r0 = Math.Min(ZBottom, ZTop) - before, r1 = Math.Max(ZBottom, ZTop) + after;
        return AlongX ? new RectPlan(r0, X0, r1, X1) : new RectPlan(X0, r0, X1, r1);
    }
}

/// <summary>A landing partway up a storey (#571): the half landing a stairwell's flights turn on.</summary>
public sealed class LandingPlan
{
    public float X0 { get; set; }
    public float Z0 { get; set; }
    public float X1 { get; set; }
    public float Z1 { get; set; }
    /// <summary>Its height above its floor, as a fraction of the storey.</summary>
    public float Level { get; set; }
}

public sealed class FloorPlan
{
    public List<RoomPlan> Rooms { get; set; } = new();
    /// <summary>Openings in this floor's slab: the stair shaft from the floor below.</summary>
    public List<RectPlan> Holes { get; set; } = new();
    /// <summary>Flight rising from this floor to the next, if any.</summary>
    public FlightPlan? Flight { get; set; }
    /// <summary>
    /// More flights from this floor, one per stairwell of a block with several (#557). Kept
    /// apart from <see cref="Flight"/> so every plan with one stair reads as it always did.
    /// </summary>
    public List<FlightPlan> Flights { get; set; } = new();
    /// <summary>Every flight rising from this floor.</summary>
    public IEnumerable<FlightPlan> AllFlights() => Flight == null ? Flights : Flights.Prepend(Flight);
    /// <summary>Guard rails along hole edges, as (x0,z0)-(x1,z1) segments stored in a rect.</summary>
    public List<RectPlan> Rails { get; set; } = new();
    /// <summary>Half landings between this floor and the next (#571).</summary>
    public List<LandingPlan> Landings { get; set; } = new();
}

public enum FurnitureType
{
    Bed, SingleBed, Wardrobe, Nightstand, Sofa, CoffeeTable, Table, Chair, Counter, Fridge, Stove,
    Toilet, Sink, Bathtub, Desk, Shelf, ShopCounter, Rack, Crate, HayBale, Pew, Altar, Car,
    Blackboard, Tv, Rug, Workbench, Plant,
    Bell, Lectern, Cross, Dais,
    // stored plans hold these as numbers: new types go on the end
    GunLocker, Safe, PastorRat, FrontPew,
    // #213
    WashingMachine, Dryer, BunkBed, WineRack, Barrel, DrumKit, Piano, Keyboard, GuitarStand,
    AcousticFoam, CinemaScreen, Armchair, Bookcase, ToyBox, TellerDesk, VaultSafe, WaterTank,
    IroningBoard, Amplifier,
    // #273: a PAUSA vending machine (Loot/ShopService)
    VendingMachine,
    // #370: the church radio by the pastor rat
    ChurchRadio,
    // #497: industrial sites
    PalletRack, Pallet, BarrelStack, SackStack, Conveyor, Machine, Gantry, ToolChest, CarLift,
    TyreStack, OilDrum, Compressor, JerryCan, SafetySign, HardHatRack, FireExtinguisher, Locker,
    Forklift, ShowroomPlinth, TruckProp, DeskCounter, Whiteboard, TimeClock, Banner, FloorMarking,
    Bench,
    /// <summary>A bin of Blåhajs on the shop floor (#501): a wire basket heaped with plush sharks.</summary>
    BlahajBin = 83,
    // #557: apartment blocks
    Pillar, StorageCage, Mailboxes, BikeRack,
}

public sealed class FurniturePlan
{
    public FurnitureType Type { get; set; }
    public int Floor { get; set; }
    public float X { get; set; }
    public float Z { get; set; }
    /// <summary>Quarter turns; 0 means the piece's back faces −Z.</summary>
    public int Turns { get; set; }
    public float W { get; set; }
    public float D { get; set; }
    public float H { get; set; }
    /// <summary>Height of its base above the floor: an altar on the chancel step, a bell in its frame.</summary>
    public float Lift { get; set; }
}

/// <summary>
/// An elevator (#557): one cabin running in a shaft through floors <see cref="Bottom"/> to
/// <see cref="Top"/>. On each of them the cabin's rectangle is a <see cref="RoomType.Elevator"/>
/// room whose sliding doors open on side <see cref="DoorSide"/>. The ride is a teleport: the
/// cabin is at one floor at a time (server state), and everyone in it is moved together.
/// </summary>
public sealed class LiftPlan
{
    public float X0 { get; set; }
    public float Z0 { get; set; }
    public float X1 { get; set; }
    public float Z1 { get; set; }
    public int Bottom { get; set; }
    public int Top { get; set; }
    public Side DoorSide { get; set; }
    /// <summary>Middle of the doorway along its wall, interior-local, as <see cref="OpeningPlan.Center"/>.</summary>
    public float DoorCenter { get; set; }
    public float DoorWidth { get; set; }
    public float DoorTop { get; set; }

    public bool Serves(int floor) => floor >= Bottom && floor <= Top;

    /// <summary>Height of the call button and of the middle of the cabin's panel above the floor.</summary>
    public const float ButtonHeight = 1.15f;

    /// <summary>Unit vector out of the cabin through its doors, onto the landing (interior-local X, Z).</summary>
    public (float X, float Z) Outward => DoorSide switch
    {
        Side.Front => (0, -1),
        Side.Back => (0, 1),
        Side.Left => (-1, 0),
        _ => (1, 0),
    };

    /// <summary>
    /// A point on the door wall: <paramref name="along"/> metres from the doorway's middle (toward
    /// +X or +Z), <paramref name="y"/> up, <paramref name="off"/> out onto the landing from the
    /// cabin's rectangle edge.
    /// </summary>
    public Godot.Vector3 WallPoint(float along, float y, float off) => DoorSide switch
    {
        Side.Front => new(DoorCenter + along, y, Z0 - off),
        Side.Back => new(DoorCenter + along, y, Z1 + off),
        Side.Left => new(X0 - off, y, DoorCenter + along),
        _ => new(X1 + off, y, DoorCenter + along),
    };

    /// <summary>Where the call button is along the door wall, from the doorway's middle.</summary>
    public float CallAlong => DoorWidth / 2 + 0.28f;

    /// <summary>The call button on the landing of the floor standing at <paramref name="y0"/>, interior-local.</summary>
    public Godot.Vector3 CallPoint(float y0) => WallPoint(CallAlong, y0 + ButtonHeight, InteriorGenerator.WallInset + 0.03f);

    /// <summary>
    /// The wall the cabin's button panel hangs on, beside the doors, as a unit normal pointing
    /// into the cabin.
    /// </summary>
    public (float X, float Z) PanelNormal => DoorSide switch
    {
        Side.Front or Side.Back => (-1, 0),
        _ => (0, -1),
    };

    /// <summary>The middle of the cabin's panel at height <paramref name="y"/>, on its wall's face, near the doors.</summary>
    public Godot.Vector3 PanelPoint(float y)
    {
        float w = InteriorGenerator.WallInset;
        return DoorSide switch
        {
            Side.Front => new(X1 - w, y, Z0 + 0.45f),
            Side.Back => new(X1 - w, y, Z1 - 0.45f),
            Side.Left => new(X0 + 0.45f, y, Z1 - w),
            _ => new(X1 - 0.45f, y, Z1 - w),
        };
    }

    /// <summary>The panel's height for <paramref name="buttons"/> buttons, one above the other.</summary>
    public static float PanelHeight(int buttons) => Math.Max(0.4f, 0.08f * buttons + 0.12f);

    /// <summary>
    /// Button <paramref name="index"/> (0 = the bottom floor, <see cref="Bottom"/>) on the panel of
    /// the cabin standing on the floor at height <paramref name="y0"/>, interior-local.
    /// </summary>
    public Godot.Vector3 ButtonPoint(float y0, int index)
    {
        float h = PanelHeight(Top - Bottom + 1);
        var (nx, nz) = PanelNormal;
        return PanelPoint(y0 + ButtonHeight) + new Godot.Vector3(nx * 0.02f, -h / 2 + 0.1f + 0.08f * index, nz * 0.02f);
    }
    public bool Contains(float x, float z, float margin = 0) =>
        x > X0 - margin && x < X1 + margin && z > Z0 - margin && z < Z1 + margin;
}

/// <summary>
/// A door with a real leaf inside the building (#557): a flat's front door off the stairwell.
/// It hangs in the doorway of room <see cref="Room"/> (the flat's side) on floor
/// <see cref="Floor"/>, and swings into that room. Its open state is server state, like a
/// street door's; a <see cref="Locked"/> one is cracked with the dial first.
/// </summary>
public sealed class InnerDoorPlan
{
    public int Floor { get; set; }
    public int Room { get; set; }
    public Side Side { get; set; }
    public float Center { get; set; }
    public float Width { get; set; }
    public float Top { get; set; }
    /// <summary>The flat it closes (<see cref="RoomPlan.Unit"/>).</summary>
    public int Unit { get; set; }
    public bool Locked { get; set; }
}

/// <summary>
/// One way in: a real door outside and the doorway it arrives at inside. A church has one per
/// solid (nave, tower), a long block or a warehouse one per facade door (#498), a house one.
/// </summary>
public sealed class EntrancePlan
{
    /// <summary>Key of the door this is (<see cref="DoorKey"/> text, what <see cref="DoorIndex"/> hands out).</summary>
    public string Door { get; set; } = "";
    /// <summary>Middle of the doorway on the inside wall line, interior-local, ground floor.</summary>
    public float X { get; set; }
    public float Z { get; set; }
    /// <summary>Unit direction into the room, interior-local.</summary>
    public float InX { get; set; }
    public float InZ { get; set; }
    public float Width { get; set; }

    // the real door, tile-local
    public float DoorX { get; set; }
    public float DoorY { get; set; }
    public float DoorZ { get; set; }
    public float DoorOutX { get; set; }
    public float DoorOutZ { get; set; }
    public float DoorWidth { get; set; }
    public float DoorHeight { get; set; }

    /// <summary>How this door's leaf moves (<see cref="DoorSpot.Hang"/>, #498).</summary>
    public DoorHang Hang { get; set; }

    /// <summary>Whether a ground vehicle is driven through it (<see cref="DoorSpot.Vehicle"/>).</summary>
    public bool Vehicle { get; set; }
}

/// <summary>
/// Everything needed to rebuild one building's interior and to walk back out of it: the plan,
/// where it sits under the building, and where the real front door is. It is what the server
/// stores and sends, so it must never depend on anything the client computes for itself.
/// </summary>
public sealed class InteriorLayout
{
    /// <summary>Bumped whenever the generator changes enough that old plans should be regenerated.</summary>
    // one number, so whichever of #497/#498 rebases onto the other takes the NEXT one, never a
    // lower one: a version going backwards regenerates the plans saved under the higher one and
    // then collides when it is reissued.
    public const int CurrentVersion = 25; // 25: a studio under 5.7 m deep keeps its kitchenette at least 1.2 m (#694); 24: ramp-first garages, a ramp square to the front wall or along the facade beside a single stairwell (#694); 23: a block of flats' underground garage has its ramp down to the car park (#558); 22: a hall's forklift is the size of the real, drivable machine (#630); 21: apartment blocks follow the building's outline, wing by wing (#577); 20: a bedroom, bathroom or WC has one door, wider corridors (#576); 19: a door driven through keeps its full width inside, so a loading bay is not a 1.8 m hole (#531); 18: flats' living rooms and bedrooms on a facade, stairwells with half landings (#571); 17: apartment blocks, a stairwell and elevator per entrance, flats, a shared basement (#557); 16: IKEA stores at their nine real locations, with bins of Blåhajs (#501); 15: every main door kept under its own eave, and the opening inside it the same hole (#509); 14: a doorway per facade door, so big buildings have several (#498); 13: industrial sites — warehouses, works, depots, body shops and dealerships (#497); 12: the church radio by the rat (#370); 11: shops (a counter guaranteed, garages' too) and PAUSA vending machines (#273); 10: the rat's congregation in the front pews; 9: the pastor rat by every altar (#241); 8: room variety, basements with shelters, banks (#213); 7: room/kind-aware furnishing, gun lockers and safes (#165); 2: doors on the wall cross-section, not the triangle extent; 3: Garage kind; 4: big barn doors; 5: barn doors nearly wall-sized; 6: garages driven into

    public int Version { get; set; } = CurrentVersion;
    public string Key { get; set; } = "";
    public BuildingKind Kind { get; set; }
    public BuildingType Type { get; set; }
    /// <summary>What the building sells, if it is a shop (#273, <c>Loot.ShopTables.TypeFor</c>); its counter is the shop.</summary>
    public Loot.ShopType Shop { get; set; }
    /// <summary><see cref="BuildingGroup.Fingerprint"/> of the group planned together, "" for one solid.</summary>
    public string Group { get; set; } = "";

    // fingerprint of the source building: a rebuilt .bldg invalidates stored plans
    public int TriangleCount { get; set; }
    public float MinY { get; set; }
    public float MaxY { get; set; }

    // footprint, tile-local
    public float CenterX { get; set; }
    public float CenterZ { get; set; }
    public float Yaw { get; set; }
    public float Width { get; set; }
    public float Depth { get; set; }

    // the real door, tile-local
    public float DoorX { get; set; }
    public float DoorY { get; set; }
    public float DoorZ { get; set; }
    public float DoorOutX { get; set; }
    public float DoorOutZ { get; set; }
    public float DoorWidth { get; set; }
    /// <summary>
    /// The facade door's height as the footprint settled it, under the eave
    /// (<see cref="BuildingFootprint.FitUnderEave"/>). Carried so a single-door plan's entrance
    /// knows it too and the opening outside matches the one inside (#509).
    /// </summary>
    public float DoorHeight { get; set; }

    public float StoreyHeight { get; set; }
    /// <summary>Interior X of the entry door on the front wall.</summary>
    public float EntryX { get; set; }
    public float EntryWidth { get; set; }

    public List<FloorPlan> Floors { get; set; } = new();
    /// <summary>
    /// How many of <see cref="Floors"/> are below ground (a cellar): floor <c>i</c> stands at
    /// <see cref="FloorY"/>(i), and the entrances are on floor <see cref="Below"/>.
    /// </summary>
    public int Below { get; set; }
    public List<FurniturePlan> Furniture { get; set; } = new();
    /// <summary>Every way in. Empty on single-door plans, which use the fields above instead.</summary>
    public List<EntrancePlan> Entrances { get; set; } = new();
    /// <summary>Elevators (#557).</summary>
    public List<LiftPlan> Lifts { get; set; } = new();
    /// <summary>Doors with a leaf inside the building: flats' front doors (#557).</summary>
    public List<InnerDoorPlan> InnerDoors { get; set; } = new();

    public bool Matches(Building b, string group) =>
        Version == CurrentVersion && TriangleCount == b.TriangleCount && Group == group
        && Math.Abs(MinY - b.MinY) < 0.01f && Math.Abs(MaxY - b.MaxY) < 0.01f;

    /// <summary>Height of a floor's surface in the interior frame: the ground floor is 0, a cellar below it.</summary>
    public float FloorY(int floor) => (floor - Below) * StoreyHeight;

    /// <summary>The floor the entrances are on.</summary>
    public FloorPlan GroundFloor => Floors[Math.Min(Below, Floors.Count - 1)];

    public bool IsBank => Type == BuildingType.Bank;

    /// <summary>The kind its facade is dressed as (BuildingMeshBuilder.KindOf): a church as a church.</summary>
    public BuildingKind DressedKind() => Type == BuildingType.Church ? BuildingKind.Sacral : Kind;

    /// <summary>The ways in; a single-door plan's one entrance is built from its front-door fields.</summary>
    public IReadOnlyList<EntrancePlan> AllEntrances() => Entrances.Count > 0 ? Entrances : new[]
    {
        new EntrancePlan
        {
            Door = Key, X = EntryX, Z = -Depth / 2, InX = 0, InZ = 1, Width = EntryWidth,
            DoorX = DoorX, DoorY = DoorY, DoorZ = DoorZ, DoorOutX = DoorOutX, DoorOutZ = DoorOutZ,
            DoorWidth = DoorWidth, DoorHeight = DoorHeight,
            Hang = DoorBudget.HangFor(DressedKind()),
            Vehicle = DoorBudget.VehicleFor(DressedKind()),
        },
    };

    /// <summary>
    /// The entrance behind exactly this door, or null. A facade door whose doorway the plan could
    /// not fit (#498) has none, and reads as locked: better than opening a portal onto another
    /// door's doorway.
    /// </summary>
    public EntrancePlan? EntranceOf(string door) => AllEntrances().FirstOrDefault(e => e.Door == door);

    /// <summary>The entrance behind a given door, or the main one.</summary>
    public EntrancePlan EntranceFor(string door) => EntranceOf(door) ?? AllEntrances()[0];

    /// <summary>
    /// Width and head height of the doorway an entrance arrives at: the ground-floor entry cut
    /// nearest to it. The door leaf and the door portal are sized from it.
    /// </summary>
    public (float Width, float Top) OpeningOf(EntrancePlan e)
    {
        var at = new Godot.Vector2(e.X, e.Z);
        (float Width, float Top) best = (e.Width, 2.1f);
        float bestD = float.MaxValue;
        if (Floors.Count == 0) return best;
        foreach (var r in GroundFloor.Rooms)
            foreach (var o in r.Openings)
            {
                if (o.Kind != OpeningKind.Entry) continue;
                var p = o.Side switch
                {
                    Side.Front => new Godot.Vector2(o.Center, r.Z0),
                    Side.Back => new Godot.Vector2(o.Center, r.Z1),
                    Side.Left => new Godot.Vector2(r.X0, o.Center),
                    _ => new Godot.Vector2(r.X1, o.Center),
                };
                float d = p.DistanceTo(at);
                if (d < bestD) { bestD = d; best = (o.Width, o.Top - o.Bottom); }
            }
        return best;
    }

    /// <summary>
    /// The room a piece of furniture stands in: the room of its floor holding its centre, or a
    /// taller room from a floor below reaching up into it (a nave). Null if none does.
    /// </summary>
    public RoomPlan? RoomOf(FurniturePlan f)
    {
        for (int fl = Math.Min(f.Floor, Floors.Count - 1); fl >= 0; fl--)
            foreach (var r in Floors[fl].Rooms)
                if (fl + r.Span > f.Floor && f.X >= r.X0 && f.X <= r.X1 && f.Z >= r.Z0 && f.Z <= r.Z1)
                    return r;
        return null;
    }

    /// <summary>Clear height of a room: its storeys less the slab under the floor above.</summary>
    public float ClearOf(RoomPlan r) =>
        r.Clear > 0 ? r.Clear : r.Span * StoreyHeight - InteriorGenerator.Slab;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static InteriorLayout? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<InteriorLayout>(json, Json); }
        catch (JsonException) { return null; }
    }

    public byte[] ToCompressed()
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(ToJson());
            z.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    public static InteriorLayout? FromCompressed(byte[] data)
    {
        try
        {
            using var z = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.UTF8);
            return FromJson(r.ReadToEnd());
        }
        catch (InvalidDataException) { return null; }
    }
}
