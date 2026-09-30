using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

public enum RoomType
{
    Hall, Living, Kitchen, Dining, Bedroom, Bathroom, WC, Office, Shop, Storage,
    Classroom, Nave, Workshop, Barn, Garage, Lobby, Landing,
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
}

/// <summary>
/// Everything needed to rebuild one building's interior and to walk back out of it: the plan,
/// where it sits under the building, and where the real front door is. It is what the server
/// stores and sends, so it must never depend on anything the client computes for itself.
/// </summary>
public sealed class InteriorLayout
{
    /// <summary>Bumped whenever the generator changes enough that old plans should be regenerated.</summary>
    public const int CurrentVersion = 3; // 2: doors on the wall cross-section, not the triangle extent; 3: Garage kind

    public int Version { get; set; } = CurrentVersion;
    public string Key { get; set; } = "";
    public BuildingKind Kind { get; set; }

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

    public float StoreyHeight { get; set; }
    /// <summary>Interior X of the entry door on the front wall.</summary>
    public float EntryX { get; set; }
    public float EntryWidth { get; set; }

    public List<FloorPlan> Floors { get; set; } = new();
    public List<FurniturePlan> Furniture { get; set; } = new();

    public bool Matches(Building b) =>
        Version == CurrentVersion && TriangleCount == b.TriangleCount
        && Math.Abs(MinY - b.MinY) < 0.01f && Math.Abs(MaxY - b.MaxY) < 0.01f;

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
