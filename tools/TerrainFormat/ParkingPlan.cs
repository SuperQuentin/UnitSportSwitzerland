namespace UnitSport.Terrain.Format;

/// <summary>What a bay is marked for (#499). Append only: the byte is on the wire and in the file.</summary>
public enum ParkingBayKind : byte
{
    Car = 0,
    Motorcycle = 1,
    /// <summary>A long bay for a van, truck or coach, at a rest area or a works.</summary>
    Truck = 2,
    Bus = 3,
    /// <summary>A marked loading bay at a shop: nobody parks in it, so it stays empty.</summary>
    Loading = 4,
}

/// <summary>Bits of <see cref="ParkingBay.Flags"/>.</summary>
[Flags]
public enum ParkingBayFlags : byte
{
    None = 0,
    /// <summary>Wider (3.5 m) and painted with the wheelchair symbol; nearest the entrance or the door.</summary>
    Disabled = 1 << 0,
    /// <summary>Under a tree island or a canopy: a cue for where a dormant car is shaded.</summary>
    Shaded = 1 << 1,
    /// <summary>Angled (45°) rather than square to the aisle; <see cref="ParkingBay.Heading"/> already says so.</summary>
    Angled = 1 << 2,
    /// <summary>Parallel to the aisle, along a kerb.</summary>
    Parallel = 1 << 3,
    /// <summary>Closest to the entrance throat: left free so an arriving player has somewhere to put their car.</summary>
    NearEntrance = 1 << 4,
    /// <summary>Something stands in it — a trolley shelter, a kiosk — so it is painted but never filled.</summary>
    Blocked = 1 << 5,
}

/// <summary>
/// One marked bay of a laid-out car park (#499), tile-local like every other <c>.road</c> record.
/// Written by <c>ParkingPlanner</c> into the <c>PARK</c> section in a **stable order** (row index,
/// then along the row): a dormant vehicle's slot ordinal is an index into this list, so reordering
/// bays would move every parked car in the region. Rules: docs/notes/tools/parking-lots.md.
/// </summary>
public readonly record struct ParkingBay(
    // Tile-local centre of the bay, X east and Z south, Y on the surface it is painted on.
    float X, float Y, float Z,
    // Radians about +Y, 0 = -Z (north): the direction a car parked in it faces, nose first.
    float Heading,
    ParkingBayKind Kind,
    ParkingBayFlags Flags)
{
    /// <summary>Swiss standard bay (VSS 640 291a), metres.</summary>
    public const float StandardWidth = 2.5f, StandardDepth = 5.0f;

    /// <summary>A disabled bay is wider so a door can open fully beside it.</summary>
    public const float DisabledWidth = 3.5f;

    public float Width => Kind switch
    {
        ParkingBayKind.Motorcycle => 1.2f,
        ParkingBayKind.Truck or ParkingBayKind.Bus or ParkingBayKind.Loading => 3.5f,
        _ => (Flags & ParkingBayFlags.Disabled) != 0 ? DisabledWidth : StandardWidth,
    };

    public float Depth => Kind switch
    {
        ParkingBayKind.Motorcycle => 2.5f,
        ParkingBayKind.Truck or ParkingBayKind.Loading => 12.0f,
        ParkingBayKind.Bus => 14.0f,
        _ => StandardDepth,
    };

    /// <summary>True where a vehicle may be left standing: a loading bay is kept clear.</summary>
    public bool Occupiable => Kind != ParkingBayKind.Loading
        && (Flags & (ParkingBayFlags.NearEntrance | ParkingBayFlags.Blocked)) == 0;

    /// <summary>1 is the only version so far.</summary>
    public const byte SectionVersion = 1;

    public static void Write(BinaryWriter w, IReadOnlyList<ParkingBay> bays)
    {
        w.Write((uint)bays.Count);
        w.Write(SectionVersion);
        foreach (var b in bays)
        {
            w.Write(b.X); w.Write(b.Y); w.Write(b.Z);
            w.Write(b.Heading);
            w.Write((byte)b.Kind);
            w.Write((byte)b.Flags);
            w.Write((ushort)0);   // pad, so a record stays 4-byte aligned at 20 B
        }
    }

    /// <summary>Null when the section is of a newer version this reader does not know (the caller skips it).</summary>
    public static List<ParkingBay>? Read(BinaryReader r)
    {
        uint n = r.ReadUInt32();
        byte version = r.ReadByte();
        if (version is < 1 or > SectionVersion) return null;

        var bays = new List<ParkingBay>((int)Math.Min(n, 4096));
        for (uint i = 0; i < n; i++)
        {
            float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), heading = r.ReadSingle();
            var kind = (ParkingBayKind)r.ReadByte();
            var flags = (ParkingBayFlags)r.ReadByte();
            r.ReadUInt16();
            bays.Add(new ParkingBay(x, y, z, heading, kind, flags));
        }
        return bays;
    }
}
