using System.Buffers.Binary;
using System.IO.Compression;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Ground cover class per terrain vertex, gathered from every swissTLM3D area layer:
/// natural cover (<c>tlm_bb_bodenbedeckung</c>), land use (<c>tlm_areale_nutzungsareal</c>),
/// leisure (<c>tlm_areale_freizeitareal</c>), sports and airfield structures
/// (<c>tlm_bauten_sportbaute_ply</c>, <c>tlm_bauten_verkehrsbaute_ply</c>) and traffic
/// areas (<c>tlm_areale_verkehrsareal</c>).
///
/// TLM maps no arable parcels at all — farmland, meadow and the ground between village
/// houses are simply absent from every layer, which is why <see cref="Open"/> is the zero
/// default and falls back to altitude banding.
/// </summary>
public enum CoverClass : byte
{
    Open = 0,
    Forest = 1,       // Wald
    OpenForest = 2,   // Wald offen
    Woodland = 3,     // Gehoelzflaeche — hedges, copses, orchard strips
    Shrub = 4,        // Gebueschwald
    Rock = 5,         // Fels
    LooseRock = 6,    // Fels locker
    Scree = 7,        // Lockergestein
    LooseScree = 8,   // Lockergestein locker
    Water = 9,        // Stehende / Fliessende Gewaesser
    Wetland = 10,     // Feuchtgebiet
    Glacier = 11,     // Gletscher
    Vineyard = 12,    // Reben
    ParkingPublic = 13,   // Oeffentliches Parkplatzareal
    ParkingPrivate = 14,  // Privates Parkplatzareal
    RestArea = 15,        // Rastplatzareal
    PavedArea = 16,       // Verkehrsflaeche / Fahrareal

    // --- natural cover TLM maps but we used to drop ---
    Snowfield = 17,   // Schneefeld Toteis — firn and dead ice below the glacier tongue
    Boulders = 18,    // Felsbloecke / Felsbloecke locker

    // --- land use, tlm_areale_nutzungsareal ---
    Orchard = 19,     // Obstanlage
    Nursery = 20,     // Baumschule
    Allotment = 21,   // Schrebergartenareal
    Cemetery = 22,    // Friedhof
    Park = 23,        // Oeffentliches Parkareal
    Institution = 24, // Schul-, Spital-, Kloster-, Messe-, historisches Areal
    Quarry = 25,      // Abbauareal
    Landfill = 26,    // Deponieareal
    Industrial = 27,  // Kraftwerk-, Unterwerk-, Abwasserreinigungs-, Kehrichtareal
    Clearcut = 28,    // Wald nicht bestockt — forest land currently carrying no trees
    Military = 29,    // Truppenuebungsplatz

    // --- leisure, tlm_areale_freizeitareal + tlm_bauten_sportbaute_ply ---
    SportsField = 30, // Sportplatzareal / Sportplatz
    Golf = 31,        // Golfplatzareal
    Pool = 32,        // Schwimmbadareal
    Campsite = 33,    // Campingplatzareal / Standplatzareal
    Leisure = 34,     // Zoo-, Freizeitanlagen-, Pferderennbahnareal

    // --- transport structures, tlm_bauten_verkehrsbaute_ply ---
    Runway = 35,      // Hartbelagpiste / Rollfeld Hartbelag
    GrassStrip = 36,  // Graspiste / Rollfeld Gras
    Platform = 37,    // Perron
}

/// <summary>
/// Surface pattern the terrain shader draws on top of the base colour, carried in the
/// vertex colour's alpha channel so no second attribute stream is needed.
/// </summary>
public enum SurfacePattern : byte
{
    None = 0,
    ParkingBays = 1,   // 2.5 x 5.0 m bay grid
    VineRows = 2,      // 2 m planting rows
    PitchStripes = 3,  // mown stripes on turf
}

/// <summary>
/// Per-tile cover raster on the same 501x501 lattice as the height grid, so a vertex and
/// its cover share an index. Payload is deflate-compressed: these rasters are large
/// stretches of a single class and shrink by roughly 100x.
/// </summary>
public static class CoverFormat
{
    /// <summary>"USCV" little-endian.</summary>
    public const uint Magic = 0x56435355;

    public const ushort Version = 1;
    public const int HeaderSize = 20;
    public const int Size = ChunkFormat.GridSize;

    public static string FileName(TileId id) => $"cover_{id.E}_{id.N}.cover";

    /// <summary>Traffic areas from tlm_areale_verkehrsareal (a different layer).</summary>
    public static CoverClass ParseTrafficArea(string? objektart) => objektart switch
    {
        "Oeffentliches Parkplatzareal" => CoverClass.ParkingPublic,
        "Privates Parkplatzareal" => CoverClass.ParkingPrivate,
        "Rastplatzareal" => CoverClass.RestArea,
        "Verkehrsflaeche" or "Privates Fahrareal" or "Gleisareal" => CoverClass.PavedArea,
        _ => CoverClass.Open,
    };

    /// <summary>True for surfaces that should be painted with parking bays.</summary>
    public static bool IsParking(CoverClass c) =>
        c is CoverClass.ParkingPublic or CoverClass.ParkingPrivate or CoverClass.RestArea;

    /// <summary>Natural ground cover, tlm_bb_bodenbedeckung.</summary>
    public static CoverClass Parse(string? objektart) => objektart switch
    {
        "Wald" => CoverClass.Forest,
        "Wald offen" => CoverClass.OpenForest,
        "Gehoelzflaeche" => CoverClass.Woodland,
        "Gebueschwald" => CoverClass.Shrub,
        "Fels" => CoverClass.Rock,
        "Fels locker" => CoverClass.LooseRock,
        "Lockergestein" => CoverClass.Scree,
        "Lockergestein locker" => CoverClass.LooseScree,
        "Felsbloecke" or "Felsbloecke locker" => CoverClass.Boulders,
        "Schneefeld Toteis" => CoverClass.Snowfield,
        "Stehende Gewaesser" or "Fliessgewaesser" or "Fliessendes Gewaesser" => CoverClass.Water,
        "Feuchtgebiet" => CoverClass.Wetland,
        "Gletscher" => CoverClass.Glacier,
        _ => CoverClass.Open,
    };

    /// <summary>
    /// Land use, tlm_areale_nutzungsareal. This is where the cultivated ground lives —
    /// vineyards are here, NOT in bodenbedeckung, which is why the Valais slopes stayed
    /// generic pasture for so long.
    /// </summary>
    public static CoverClass ParseLandUse(string? objektart) => objektart switch
    {
        "Reben" => CoverClass.Vineyard,
        "Obstanlage" => CoverClass.Orchard,
        "Baumschule" => CoverClass.Nursery,
        "Schrebergartenareal" => CoverClass.Allotment,
        "Friedhof" => CoverClass.Cemetery,
        "Oeffentliches Parkareal" => CoverClass.Park,
        "Wald nicht bestockt" => CoverClass.Clearcut,
        "Truppenuebungsplatz" => CoverClass.Military,
        "Abbauareal" => CoverClass.Quarry,
        "Deponieareal" => CoverClass.Landfill,
        "Kraftwerkareal" or "Unterwerkareal" or "Abwasserreinigungsareal"
            or "Kehrichtverbrennungsareal" or "Antennenareal" => CoverClass.Industrial,
        "Schul- und Hochschulareal" or "Spitalareal" or "Klosterareal"
            or "Historisches Areal" or "Messeareal"
            or "Massnahmenvollzugsanstaltsareal" => CoverClass.Institution,
        _ => CoverClass.Open,
    };

    /// <summary>Leisure grounds, tlm_areale_freizeitareal.</summary>
    public static CoverClass ParseLeisure(string? objektart) => objektart switch
    {
        "Sportplatzareal" => CoverClass.SportsField,
        "Golfplatzareal" => CoverClass.Golf,
        "Schwimmbadareal" => CoverClass.Pool,
        "Campingplatzareal" or "Standplatzareal" => CoverClass.Campsite,
        "Zooareal" or "Freizeitanlagenareal" or "Pferderennbahnareal" => CoverClass.Leisure,
        _ => CoverClass.Open,
    };

    /// <summary>
    /// Built surfaces mapped as areas: tlm_bauten_sportbaute_ply (the pitch itself, more
    /// precise than the surrounding Sportplatzareal) and tlm_bauten_verkehrsbaute_ply.
    /// </summary>
    public static CoverClass ParseStructureArea(string? objektart) => objektart switch
    {
        "Sportplatz" => CoverClass.SportsField,
        "Hartbelagpiste" or "Rollfeld Hartbelag" => CoverClass.Runway,
        "Graspiste" or "Rollfeld Gras" => CoverClass.GrassStrip,
        "Perron" => CoverClass.Platform,
        _ => CoverClass.Open,
    };

    /// <summary>True for classes that should be populated with randomly scattered trees.</summary>
    public static bool IsWooded(CoverClass c) =>
        c is CoverClass.Forest or CoverClass.OpenForest or CoverClass.Woodland or CoverClass.Shrub;

    /// <summary>
    /// True for classes planted on a regular grid rather than scattered. Orchards and
    /// nurseries are laid out in rows; a random scatter reads as scrub instead.
    /// </summary>
    public static bool IsPlanted(CoverClass c) =>
        c is CoverClass.Orchard or CoverClass.Nursery;

    /// <summary>Row and in-row spacing for a planted class, in metres.</summary>
    public static float PlantingSpacing(CoverClass c) => c switch
    {
        CoverClass.Orchard => 6f,
        CoverClass.Nursery => 4f,
        _ => 0f,
    };

    /// <summary>Trees per hectare, by class.</summary>
    public static float TreeDensity(CoverClass c) => c switch
    {
        CoverClass.Forest => 220f,
        CoverClass.OpenForest => 90f,
        CoverClass.Woodland => 120f,
        CoverClass.Shrub => 70f,
        _ => 0f,
    };

    /// <summary>Which pattern, if any, the terrain shader draws over this class.</summary>
    public static SurfacePattern PatternFor(CoverClass c) => c switch
    {
        CoverClass.ParkingPublic or CoverClass.ParkingPrivate or CoverClass.RestArea
            => SurfacePattern.ParkingBays,
        CoverClass.Vineyard => SurfacePattern.VineRows,
        CoverClass.SportsField or CoverClass.Golf => SurfacePattern.PitchStripes,
        _ => SurfacePattern.None,
    };

    public static void Encode(TileId id, byte[] cells, Stream output)
    {
        if (cells.Length != Size * Size)
            throw new ArgumentException($"Expected {Size}^2 cover cells, got {cells.Length}");

        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 1); // flags: deflate
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], id.E);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], id.N);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], Size);
        output.Write(header);

        using var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(cells);
    }

    public static byte[] Decode(Stream input)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        input.ReadExactly(header);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
        if (magic != Magic)
            throw new InvalidDataException($"Bad cover magic 0x{magic:X8}");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != Version)
            throw new InvalidDataException($"Unsupported cover version {version}");
        int size = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        if (size != Size)
            throw new InvalidDataException($"Unsupported cover size {size}");

        var cells = new byte[Size * Size];
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        deflate.ReadExactly(cells);
        return cells;
    }
}
