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
}

public sealed class FloorPlan
{
    public List<RoomPlan> Rooms { get; set; } = new();
    /// <summary>Openings in this floor's slab: the stair shaft from the floor below.</summary>
    public List<RectPlan> Holes { get; set; } = new();
    /// <summary>Flight rising from this floor to the next, if any.</summary>
    public FlightPlan? Flight { get; set; }
    /// <summary>Guard rails along hole edges, as (x0,z0)-(x1,z1) segments stored in a rect.</summary>
    public List<RectPlan> Rails { get; set; } = new();
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
    public const int CurrentVersion = 16; // 16: a door driven through keeps its full width inside, so a loading bay is not a 1.8 m hole (#531); 15: every main door kept under its own eave, and the opening inside it the same hole (#509); 14: a doorway per facade door, so big buildings have several (#498); 13: industrial sites — warehouses, works, depots, body shops and dealerships (#497); 12: the church radio by the rat (#370); 11: shops (a counter guaranteed, garages' too) and PAUSA vending machines (#273); 10: the rat's congregation in the front pews; 9: the pastor rat by every altar (#241); 8: room variety, basements with shelters, banks (#213); 7: room/kind-aware furnishing, gun lockers and safes (#165); 2: doors on the wall cross-section, not the triangle extent; 3: Garage kind; 4: big barn doors; 5: barn doors nearly wall-sized; 6: garages driven into

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
