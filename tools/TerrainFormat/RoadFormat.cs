namespace UnitSport.Terrain.Format;

/// <summary>
/// Road width/importance class, derived from swissTLM3D <c>objektart</c>.
/// Ordered from largest to smallest so rendering can cheaply cull by class.
/// </summary>
public enum RoadClass : byte
{
    Motorway = 0,   // Autobahn
    Expressway = 1, // Autostrasse
    Ramp = 2,       // Ein-/Ausfahrt, Autobahnzubringer
    Major = 3,      // 10m / 8m Strasse
    Road = 4,       // 6m Strasse
    Minor = 5,      // 4m Strasse
    Lane = 6,       // 3m Strasse
    Track = 7,      // 2m Weg  — typically farm/forest tracks
    Path = 8,       // 1m Weg  — footpaths
    Link = 9,       // Verbindung, Markierte Spur
    Square = 10,    // Platz
    Railway = 11,   // from tlm_oev_eisenbahn
    Unknown = 12,

    // ---------------------------------------------------------------------------------
    // Everything from here is a separate family, NOT part of the width ordering above.
    // Code that culls or styles by class compares against Track/Minor/Major; those tests
    // must route these out first rather than relying on where they sit in the enum.
    // ---------------------------------------------------------------------------------

    /// <summary>Flowing water, draped. From tlm_gewaesser_fliessgewaesser.</summary>
    Watercourse = 13,

    /// <summary>A Trockenrinne — a gully that only runs in spate. Draped, drawn dry.</summary>
    DryChannel = 14,

    /// <summary>Suone / bisse: the Valais irrigation channels, contour-hugging and walkable.</summary>
    Bisse = 15,

    /// <summary>Luftseilbahn / Gondelbahn — aerial, cabins.</summary>
    Cableway = 16,

    /// <summary>Sesselbahn — chairlift.</summary>
    Chairlift = 17,

    /// <summary>Skilift — surface tow.</summary>
    SkiLift = 18,

    /// <summary>Transportseil — material ropeway, thin and often derelict-looking.</summary>
    RopeTow = 19,

    /// <summary>Schutzverbauung — avalanche and rockfall defences, in rows across a slope.</summary>
    AvalancheBarrier = 20,

    /// <summary>Gewaesserverbauung — check dams and bank protection in a torrent bed.</summary>
    TorrentWorks = 21,

    /// <summary>Trockenmauer — dry-stone walling, the Valais terracing.</summary>
    DryStoneWall = 22,

    /// <summary>A built wall from tlm_bauten_mauer: retaining, boundary, flood.</summary>
    Wall = 23,
}

/// <summary>Surface material, from swissTLM3D <c>belagsart</c>.</summary>
public enum RoadSurface : byte
{
    Unknown = 0,
    Paved = 1,   // "Hart"  — asphalt/concrete
    Natural = 2, // "Natur" — gravel, dirt, grass
}

[Flags]
public enum RoadFlags : ushort
{
    None = 0,
    Hiking = 1 << 0,      // wanderwege = Wanderweg
    MountainHiking = 1 << 1, // wanderwege = Bergwanderweg / Alpinwanderweg
    Cycle = 1 << 2,       // in the Veloland national network
    MountainBike = 1 << 3,// in the Mountainbikeland network
    Bridge = 1 << 4,      // kunstbaute = Bruecke / Galerie
    Tunnel = 1 << 5,      // kunstbaute = Tunnel / Unterfuehrung
    Stairs = 1 << 6,      // kunstbaute = Treppe
    Ford = 1 << 7,        // kunstbaute = Furt
    Restricted = 1 << 8,  // verkehrsbeschraenkung != Keine
    Divided = 1 << 9,     // richtungsgetrennt
    Tramway = 1 << 10,    // railway subtype
    NarrowGauge = 1 << 11,// Schmalspur (metre gauge — common in the Alps)
    DoubleTrack = 1 << 12,// anzahl_spuren >= 2
    RackRailway = 1 << 13,// zahnradbahn — cog railway
    Funicular = 1 << 14,  // standseilbahn
    Disused = 1 << 15,    // ausser_betrieb
}

/// <summary>
/// One polyline clipped to a single km tile. Positions are metres relative to the
/// tile's NW corner: X east, Y altitude (already draped onto the terrain by the
/// preprocessor), Z south — i.e. the same local frame the terrain chunk mesh uses.
/// </summary>
public sealed class RoadSegment
{
    public RoadClass Class { get; init; }
    public RoadSurface Surface { get; init; }
    public RoadFlags Flags { get; init; }
    public float Width { get; init; }
    public required float[] Points { get; init; } // xyz triples

    /// <summary>v3 per-segment attributes. All zero (the default) in v1/v2 files.</summary>
    public RoadAttributes Attributes { get; init; }

    public int PointCount => Points.Length / 3;
}

/// <summary>v3 attribute flags: the second flags word <see cref="RoadFlags"/> had no room for.</summary>
[Flags]
public enum RoadAttrFlags : ushort
{
    None = 0,
    Urban = 1 << 0,        // a street: kerbs and sidewalks (#119)
    Roundabout = 1 << 1,   // part of a roundabout ring (TLM kreisel, OSM junction=roundabout)
    Osm = 1 << 2,          // some attribute came from the OpenStreetMap overlay (ODbL)
    Tram = 1 << 3,         // tram rails run along the carriageway (OSM)
    YieldAtStart = 1 << 4, // traffic leaving this segment at its first point gives way (#121)
    YieldAtEnd = 1 << 5,   // ... at its last point
    OwnerFederal = 1 << 6, // TLM eigentuemer = Bund
    OwnerCanton = 1 << 7,  // TLM eigentuemer = Kanton
    OnStreet = 1 << 8,     // railway: TLM auf_strasse, the track runs in a street (#124)
    Embedded = 1 << 9,     // railway piece inside a carriageway: no ballast, no raised rails, RailGroove paint (#124)
    PavedBed = 1 << 10,    // tram track in a town, outside any carriageway: a paved bed, no ballast, flush rails as RailGroove paint (#119)
}

/// <summary>Bike provision on one side of a carriageway (#120).</summary>
public enum BikeKind : byte
{
    None = 0,
    Lane = 1,    // painted lane on the carriageway (yellow dashes)
    Track = 2,   // separated path beside it
    Shared = 3,  // shared lane, symbol only
}

/// <summary>
/// Cross-section of one side of a carriageway, outward from its edge. Left and right are in the
/// segment's drawing direction. Widths in decimetres, kerb height in centimetres; 0 = none.
/// </summary>
public readonly record struct RoadSide(
    byte SidewalkDm = 0, BikeKind Bike = BikeKind.None, byte BikeDm = 0, byte KerbCm = 0, byte VergeDm = 0);

/// <summary>
/// The v3 per-segment attribute record (24 bytes on disk). Everything zero means "not decided":
/// two-way (or unknown), lanes and width from the class, no sidewalk, rural.
/// </summary>
public readonly record struct RoadAttributes(
    RoadAttrFlags Flags = RoadAttrFlags.None,
    // +1 traffic only in drawing order, -1 only against it, 0 both ways (or unknown).
    sbyte OneWay = 0,
    // Grade level: 0 ground, positive above (bridges), negative below. TLM <c>stufe</c>.
    sbyte Layer = 0,
    // Lanes in drawing direction / against it; 0 = unknown, the class decides.
    byte LanesForward = 0,
    byte LanesBackward = 0,
    // higher wins at a junction, see RoadFormat.PriorityFor
    byte Priority = 0,
    // Carriageway width in centimetres (TLM nominal class width or OSM width); 0 = unknown.
    ushort WidthCm = 0,
    RoadSide Left = default,
    RoadSide Right = default)
{
    public const int RecordSize = 24;
    public bool Has(RoadAttrFlags f) => (Flags & f) != 0;
}

/// <summary>Header flags word (0 in v1/v2).</summary>
[Flags]
public enum RoadTileFlags : ushort
{
    None = 0,
    /// <summary>Built with the OpenStreetMap overlay: the tile is an ODbL derived database.</summary>
    Osm = 1 << 0,
    /// <summary>
    /// Written by the road network stage (RoadGen). Its absence on a v3 tile marks raw extractor
    /// output, the only safe input for the stage: a second pass would trim trimmed roads.
    /// </summary>
    Network = 1 << 1,
}

/// <summary>Road paint (#116). Colour is stored separately, so a type does not fix it.</summary>
public enum PaintType : byte
{
    None = 0,
    WhiteSolid = 1,
    WhiteDashed = 2,
    YellowDashed = 3,
    YellowSolid = 4,
    SharkTooth = 5,
    Arrow = 6,        // Variant: PaintArrow bits
    BikeSymbol = 7,
    StopLine = 8,
    GiveWayLine = 9,
    RailGroove = 10,
    Hatch = 11,
}

/// <summary><see cref="PaintType.Arrow"/> variant bits; combine for a combined arrow.</summary>
[Flags]
public enum PaintArrow : byte { None = 0, Left = 1, Straight = 2, Right = 4 }

public enum PaintShape : byte
{
    /// <summary>A centreline, ribboned at runtime to <see cref="RoadPaint.Width"/>, dashed by Dash/Gap.</summary>
    Polyline = 0,
    /// <summary>A triangle list (teeth, arrows, symbols, hatching), drawn as given.</summary>
    Triangles = 1,
}

/// <summary>One paint primitive, tile-local, heights already on the surface it is painted on.</summary>
public sealed class RoadPaint
{
    public PaintShape Shape { get; init; }
    public PaintType Type { get; init; }
    public byte Variant { get; init; }
    /// <summary>RGBA8, R in the high byte.</summary>
    public uint Rgba { get; init; }
    /// <summary>Line width in metres (polyline only).</summary>
    public float Width { get; init; }
    /// <summary>Dash and gap length in metres; Dash 0 = solid (polyline only).</summary>
    public float Dash { get; init; }
    public float Gap { get; init; }
    /// <summary>xyz triples, same frame as <see cref="RoadSegment.Points"/>.</summary>
    public required float[] Vertices { get; init; }

    /// <summary>
    /// A polyline that lies along a segment of its tile: then <see cref="Vertices"/> are
    /// <see cref="RoadPaintGeometry.Along"/>(Segment, Offset, From, To), and the file stores only
    /// this reference (#116b), the decoder rebuilds the vertices. Null: the file stores the vertices.
    /// Build one with <see cref="AlongSegment"/>, which rounds the numbers to what the file keeps.
    /// </summary>
    public RoadSegment? Segment { get; init; }
    /// <summary>Metres right of the segment's drawing direction (mm in the file).</summary>
    public float Offset { get; init; }
    /// <summary>Horizontal metres along the offset line (cm in the file); To past the end (infinity) = to the end.</summary>
    public float From { get; init; }
    public float To { get; init; } = float.PositiveInfinity;

    /// <summary>An offset rounded to the millimetre the file keeps.</summary>
    public static float FileOffset(double offset) => MathF.Round((float)offset * 1000f) / 1000f;

    /// <summary>A line along <paramref name="seg"/>, its numbers rounded as the file stores them.</summary>
    public static RoadPaint AlongSegment(RoadSegment seg, PaintType type, uint rgba, float width, float dash, float gap,
        double offset, double from = 0, double to = double.PositiveInfinity, byte variant = 0)
    {
        float o = FileOffset(offset);
        float f = (float)(Math.Round(Math.Max(0, from) * 100) / 100);
        float t = double.IsPositiveInfinity(to) ? float.PositiveInfinity : (float)(Math.Max(1, Math.Round(to * 100)) / 100);
        return new RoadPaint
        {
            Shape = PaintShape.Polyline, Type = type, Variant = variant, Rgba = rgba, Width = width, Dash = dash, Gap = gap,
            Segment = seg, Offset = o, From = f, To = t, Vertices = RoadPaintGeometry.Along(seg, o, f, t),
        };
    }
    /// <summary>Triangle list for <see cref="PaintShape.Triangles"/>; empty for a polyline.</summary>
    public ushort[] Indices { get; init; } = [];
}

public enum PointPropType : byte
{
    None = 0,
    YieldSign = 1,       // Swiss "Kein Vortritt", inverted triangle (#121)
    RoundaboutSign = 2,  // Swiss 2.41.1 (#122)
    MainRoadSign = 3,    // Swiss 3.03 "Hauptstrasse", yellow diamond (#121)
}

/// <summary>A prop at one point: a sign on a pole. Y is its foot on the ground.</summary>
public readonly record struct RoadPointProp(
    PointPropType Type, byte Variant, PropFlags Flags, float X, float Y, float Z,
    // Radians about +Y; 0 faces -Z (north), matching Godot's forward.
    float Heading,
    // Overall height in metres (pole plus sign).
    float Height)
{
    public const int RecordSize = 24;
}

public enum LinearPropType : byte
{
    None = 0,
    RetainingWallFill = 1, // supports the road on the downhill side (#125)
    RetainingWallCut = 2,  // holds the slope back on the uphill side (#125)
    Guardrail = 3,         // steel W-beam on posts (#126)
    Fence = 4,             // simple rail, on walls and in towns (#126)
    MedianDouble = 5,      // double guardrail between two carriageways (#126)
}

/// <summary>Bits of <see cref="RoadLinearProp.Flags"/> and <see cref="RoadAreaProp.Flags"/>.</summary>
[Flags]
public enum PropFlags : ushort
{
    None = 0,
    Solid = 1 << 0,  // gets collision
}

/// <summary>
/// A prop along a line: a wall run or a railing. <see cref="Points"/> is its foot line and each
/// point carries its own height, extruded straight up.
/// </summary>
public sealed class RoadLinearProp
{
    public LinearPropType Type { get; init; }
    public byte Variant { get; init; }
    public PropFlags Flags { get; init; }
    /// <summary>Thickness in metres.</summary>
    public float Thickness { get; init; }
    /// <summary>Type-specific parameter (e.g. post spacing for a railing); 0 = type default.</summary>
    public float Param { get; init; }
    /// <summary>x, y, z, height quadruples, tile-local.</summary>
    public required float[] Points { get; init; }
    public int PointCount => Points.Length / 4;
}

public enum AreaPropType : byte
{
    None = 0,
    Island = 1,       // roundabout centre island (#122)
    SplitterIsland = 2, // raised island at a roundabout entry (#122)
    Sidewalk = 3,     // a sidewalk patch not carried by a segment, e.g. a junction corner (#119)
    Pavement = 4,     // flush carriageway beside a segment: a turn lane's widening (#123); Height 0
}

/// <summary>A raised surface: a triangulated polygon lifted by <see cref="Height"/> with a kerb face.</summary>
public sealed class RoadAreaProp
{
    public AreaPropType Type { get; init; }
    public byte Variant { get; init; }
    public PropFlags Flags { get; init; }
    /// <summary>Metres above <see cref="Vertices"/> (the carriageway surface), e.g. a 12 cm kerb.</summary>
    public float Height { get; init; }
    public required float[] Vertices { get; init; }
    public required ushort[] Indices { get; init; }
}

/// <summary>
/// The paved area where several roads meet, as its own triangulated surface.
///
/// <para>
/// A junction has to be a real object rather than the accidental overlap of the ribbons that
/// arrive at it. Drawing every centreline to full length paints four carriageways on top of
/// each other at every intersection, which no depth bias turns into a junction — it only stops
/// the flicker. Roads are trimmed back to this polygon's edge and it fills the middle.
/// ASAM OpenDRIVE reaches the same conclusion: junction connecting-roads are singled out as the
/// only roads in that standard whose surfaces may overlap.
/// </para>
/// </summary>
public sealed class RoadJunction
{
    /// <summary>Class of the dominant arm, so the cap is tinted like the road it belongs to.</summary>
    public RoadClass Class { get; init; }

    /// <summary>0 ground, 1 bridge deck, -1 tunnel — matching the arms that meet here.</summary>
    public sbyte Layer { get; init; }

    /// <summary>xyz triples, tile-local, same frame as <see cref="RoadSegment.Points"/>.</summary>
    public required float[] Vertices { get; init; }

    /// <summary>Triangle list indexing <see cref="Vertices"/>.</summary>
    public required ushort[] Indices { get; init; }

    public int VertexCount => Vertices.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

public sealed class RoadTile
{
    public TileId Id { get; init; }
    public required List<RoadSegment> Segments { get; init; }

    /// <summary>Empty in v1 files, which stay readable.</summary>
    public List<RoadJunction> Junctions { get; init; } = new();

    /// <summary>Version the tile was decoded from (the current one for a new tile).</summary>
    public ushort Version { get; init; } = RoadFormat.Version;

    public RoadTileFlags Flags { get; set; }

    // v3 layers, empty in v1/v2 files
    public List<RoadPaint> Paint { get; init; } = new();
    public List<RoadPointProp> PointProps { get; init; } = new();
    public List<RoadLinearProp> LinearProps { get; init; } = new();
    public List<RoadAreaProp> AreaProps { get; init; } = new();
}

public static class RoadFormat
{
    /// <summary>"USRD" little-endian.</summary>
    public const uint Magic = 0x44525355;

    /// <summary>
    /// 2 adds junction polygons, appended after the segments. The count went into the header's
    /// previously reserved word, so the header size and every v1 offset are unchanged and
    /// <see cref="RoadCodec.Decode"/> still reads v1 files — an already-built region keeps
    /// working until it is rewritten.
    ///
    /// 3 keeps every v2 byte and appends tagged sections after the junctions (see
    /// <see cref="RoadCodec"/>): per-segment attributes, paint, point/linear/area props. Unknown
    /// section tags are skipped, so later issues add sections without another version bump.
    /// Layout: docs/notes/tools/road-format-v3.md.
    /// </summary>
    public const ushort Version = 3;
    public const ushort MinReadableVersion = 1;
    public const int HeaderSize = 24;

    public static string FileName(TileId id) => $"roads_{id.E}_{id.N}.road";

    /// <summary>Render width in metres. Structural widths come straight from the TLM class.</summary>
    public static float DefaultWidth(RoadClass c) => c switch
    {
        RoadClass.Motorway => 11f,
        RoadClass.Expressway => 9f,
        RoadClass.Ramp => 6f,
        RoadClass.Major => 9f,
        RoadClass.Road => 6f,
        RoadClass.Minor => 4f,
        RoadClass.Lane => 3f,
        RoadClass.Track => 2.2f,
        RoadClass.Path => 1.1f,
        RoadClass.Link => 3f,
        RoadClass.Square => 6f,
        RoadClass.Railway => 4.5f,

        // TLM records no channel width, so these are typical values for the class of feature:
        // a mapped alpine stream, a spate gully, and a hand-cut irrigation channel.
        RoadClass.Watercourse => 2.5f,
        RoadClass.DryChannel => 2.0f,
        RoadClass.Bisse => 1.2f,

        // For aerial lines the "width" is the cable, not a corridor. These are deliberately
        // several times a real haul rope: the renderer runs at 0.35x internal resolution, so a
        // true 5 cm cable is sub-pixel at any distance and the line simply vanishes.
        RoadClass.Cableway => 0.28f,
        RoadClass.Chairlift => 0.20f,
        RoadClass.SkiLift => 0.14f,
        RoadClass.RopeTow => 0.12f,

        // for a wall the "width" is its thickness
        RoadClass.AvalancheBarrier => 0.25f,
        RoadClass.TorrentWorks => 0.90f,
        RoadClass.DryStoneWall => 0.60f,
        RoadClass.Wall => 0.40f,

        _ => 3f,
    };

    /// <summary>
    /// Aerial ropeways: their surveyed Z is the <i>cable</i>, not the ground. Measured against
    /// our own heightfield around Riddes: chairlifts run a median 11.9 m up, gondolas 14.6 m,
    /// and an aerial tramway reaches 73 m where it crosses a gorge. So these keep their own Z
    /// exactly like a bridge deck does, and the towers are grown up from the terrain to meet it.
    /// </summary>
    public static bool IsAerial(RoadClass c) =>
        c is RoadClass.Cableway or RoadClass.Chairlift or RoadClass.SkiLift or RoadClass.RopeTow;

    /// <summary>Draped water channels — rendered with the water material, not the road one.</summary>
    public static bool IsWatercourse(RoadClass c) =>
        c is RoadClass.Watercourse or RoadClass.DryChannel or RoadClass.Bisse;

    /// <summary>
    /// Standing structures built along a line: barriers and walls, extruded upward rather than
    /// laid flat.
    ///
    /// <para>
    /// Like ropeways these keep their surveyed Z, because for the defences that Z is genuinely
    /// the <i>top</i> of the structure — avalanche barriers around Riddes measured a median
    /// 2.80 m above our heightfield with a 90th percentile of 5.81 m, which is the real height
    /// range of snow bridges. Walls and torrent works sit much closer to the ground (+0.15 to
    /// +0.95 m), so the height is clamped per class to keep those from rendering as kerbs.
    /// </para>
    /// </summary>
    public static bool IsWall(RoadClass c) =>
        c is RoadClass.AvalancheBarrier or RoadClass.TorrentWorks
          or RoadClass.DryStoneWall or RoadClass.Wall;

    /// <summary>Least and greatest height above ground, in metres.</summary>
    public static (float Min, float Max) WallHeight(RoadClass c) => c switch
    {
        RoadClass.AvalancheBarrier => (2.0f, 7.0f),
        RoadClass.TorrentWorks => (0.8f, 3.0f),
        RoadClass.DryStoneWall => (0.8f, 2.5f),
        _ => (1.0f, 5.0f),
    };

    /// <summary>Thickness in metres. A steel snow bridge is a fence; a dry-stone wall is not.</summary>
    public static float WallThickness(RoadClass c) => c switch
    {
        RoadClass.AvalancheBarrier => 0.25f,
        RoadClass.TorrentWorks => 0.90f,
        RoadClass.DryStoneWall => 0.60f,
        _ => 0.40f,
    };

    /// <summary>Maps tlm_bauten_verbauung objektart.</summary>
    public static RoadClass? ParseDefence(string? objektart) => objektart switch
    {
        "Schutzverbauung" => RoadClass.AvalancheBarrier,
        "Gewaesserverbauung" => RoadClass.TorrentWorks,
        "Trockenmauer" => RoadClass.DryStoneWall,
        _ => null,
    };

    /// <summary>Height of the towers' tops above the cable, so the cable hangs below the sheave.</summary>
    public static float PylonHeadroom(RoadClass c) => c switch
    {
        RoadClass.Cableway => 2.5f,
        RoadClass.Chairlift => 1.8f,
        _ => 1.0f,
    };

    /// <summary>Half-width of a tower leg in metres.</summary>
    public static float PylonRadius(RoadClass c) => c switch
    {
        RoadClass.Cableway => 0.9f,
        RoadClass.Chairlift => 0.55f,
        _ => 0.35f,
    };

    /// <summary>
    /// Maps tlm_oev_uebrige_bahn objektart. Foerderband (a ground-level conveyor) and Lift
    /// (a building elevator) are not ropeways and are deliberately dropped — drawing them as
    /// cables strung across the landscape would be pure invention.
    /// </summary>
    public static RoadClass? ParseAerial(string? objektart) => objektart switch
    {
        "Luftseilbahn" or "Gondelbahn" => RoadClass.Cableway,
        "Sesselbahn" => RoadClass.Chairlift,
        "Skilift" => RoadClass.SkiLift,
        "Transportseil" => RoadClass.RopeTow,
        _ => null,
    };

    /// <summary>
    /// Maps tlm_gewaesser_fliessgewaesser objektart.
    ///
    /// <para>
    /// The exclusions are the point. <c>Druckstollen</c> is a pressure tunnel — measured 232 m
    /// <i>below</i> the surface — and <c>Druckleitung</c> a penstock pipe sitting about 5 m above
    /// it; both are hydro plumbing, not watercourses, and drawing them as streams would run
    /// rivers through the inside of a mountain. <c>Seeachse</c> is a lake's centre axis, a
    /// cartographic construction line with no channel at all.
    /// </para>
    /// </summary>
    public static RoadClass? ParseWatercourse(string? objektart) => objektart switch
    {
        "Fliessgewaesser" => RoadClass.Watercourse,
        "Trockenrinne" => RoadClass.DryChannel,
        "Bisse Suone" => RoadClass.Bisse,
        _ => null,
    };

    /// <summary>
    /// Width of ONE carriageway of a direction-separated road.
    ///
    /// <para>
    /// swissTLM3D draws a <c>richtungsgetrennt</c> road as two centrelines, one per carriageway,
    /// but <see cref="DefaultWidth"/> describes the whole road — so applying it to each line
    /// draws both halves at full width and paints them over each other. Measured on the A9 and
    /// its neighbours: the two motorway centrelines run a median <b>8.1 m</b> apart while each
    /// was being drawn 11 m wide, which is a 3 m overlap for the entire length of every
    /// motorway in the country.
    /// </para>
    ///
    /// <para>
    /// The factor is set so a carriageway fits inside that measured separation. It does not
    /// eliminate the overlap entirely, and should not: at an interchange the two carriageways
    /// genuinely converge — the 25th percentile of separation is 3.8 m — and there they really
    /// do share tarmac.
    /// </para>
    /// </summary>
    public const float DividedCarriagewayFactor = 0.55f;

    public static float WidthFor(RoadClass c, RoadFlags flags)
    {
        float width = DefaultWidth(c);
        return (flags & RoadFlags.Divided) != 0 ? width * DividedCarriagewayFactor : width;
    }

    /// <summary>Track gauge in metres: standard 1.435 m, Swiss metre gauge 1.0 m.</summary>
    public static float RailGauge(RoadFlags flags) =>
        (flags & RoadFlags.NarrowGauge) != 0 ? 1.0f : 1.435f;

    /// <summary>Lateral offset of each track centre from the formation centre.</summary>
    public static float TrackOffset(RoadFlags flags) =>
        (flags & RoadFlags.DoubleTrack) != 0 ? ((flags & RoadFlags.NarrowGauge) != 0 ? 1.8f : 2.2f) : 0f;

    /// <summary>Clear width of a tunnel bore in metres (wider than the carriageway).</summary>
    public static float TunnelWidth(RoadClass c) => Math.Max(DefaultWidth(c) + 2.0f, 4.0f);

    /// <summary>Clear height from road surface to the crown of the bore, in metres.</summary>
    public static float TunnelHeight(RoadClass c) => c switch
    {
        RoadClass.Motorway or RoadClass.Expressway or RoadClass.Major => 6.0f,
        RoadClass.Track or RoadClass.Path => 3.0f,
        _ => 4.8f,
    };

    /// <summary>Maps swissTLM3D objektart strings onto <see cref="RoadClass"/>.</summary>
    public static RoadClass ParseClass(string? objektart) => objektart switch
    {
        "Autobahn" => RoadClass.Motorway,
        "Autostrasse" => RoadClass.Expressway,
        "Einfahrt" or "Ausfahrt" or "Autobahnzubringer" => RoadClass.Ramp,
        "10m Strasse" or "8m Strasse" => RoadClass.Major,
        "6m Strasse" => RoadClass.Road,
        "4m Strasse" => RoadClass.Minor,
        // service roads around a motorway junction: without these the interchange has
        // holes where the rest area and maintenance accesses should tie in
        "3m Strasse" or "Dienstzufahrt" or "Zufahrt" => RoadClass.Lane,
        "Raststaette" => RoadClass.Minor,
        "2m Weg" or "2m Wegfragment" => RoadClass.Track,
        "1m Weg" or "1m Wegfragment" or "Klettersteig" => RoadClass.Path,
        "Verbindung" or "Markierte Spur" => RoadClass.Link,
        "Platz" => RoadClass.Square,
        _ => RoadClass.Unknown,
    };

    /// <summary>
    /// True for objektart values that are a *route*, not a carriageway. A ferry crossing
    /// and a car-train shuttle are drawn by TLM as a line over water or through a tunnel;
    /// rendering them as road ribbons lays tarmac across the lake.
    /// </summary>
    public static bool IsNotDrivableSurface(string? objektart) =>
        objektart is "Faehre" or "Autozug";

    public static RoadSurface ParseSurface(string? belagsart) => belagsart switch
    {
        "Hart" => RoadSurface.Paved,
        "Natur" => RoadSurface.Natural,
        _ => RoadSurface.Unknown,
    };

    /// <summary>
    /// Junction priority: high nibble the TLM <c>verkehrsbedeutung</c> rank (0 k_W, 1
    /// Verbindungsstrasse, 2 Durchgangsstrasse, 3 Hochleistungsstrasse), low nibble 12 - class for
    /// the ordered road classes (0 for the rest). Sorts importance first, then width class.
    /// </summary>
    public static byte PriorityFor(RoadClass c, string? verkehrsbedeutung)
    {
        int importance = verkehrsbedeutung switch
        {
            "Hochleistungsstrasse" => 3,
            "Durchgangsstrasse" => 2,
            "Verbindungsstrasse" => 1,
            _ => 0,
        };
        int rank = c <= RoadClass.Unknown ? 12 - (int)c : 0;
        return (byte)(importance << 4 | rank);
    }

    /// <summary>
    /// Nominal carriageway width in centimetres from the TLM <c>objektart</c> width class, which
    /// <see cref="ParseClass"/> collapses (10m and 8m are both Major). 0 where TLM names none.
    /// </summary>
    public static ushort NominalWidthCm(string? objektart) => objektart switch
    {
        "10m Strasse" => 1000,
        "8m Strasse" => 800,
        "6m Strasse" => 600,
        "4m Strasse" => 400,
        "3m Strasse" => 300,
        "2m Weg" or "2m Wegfragment" => 200,
        "1m Weg" or "1m Wegfragment" => 100,
        _ => 0,
    };

    /// <summary>TLM <c>stufe</c> (grade level, "k_W" when unknown) or, failing that, the structure flags.</summary>
    public static sbyte LayerFor(string? stufe, RoadFlags flags)
    {
        if (int.TryParse(stufe, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out int level))
            return (sbyte)Math.Clamp(level, sbyte.MinValue, sbyte.MaxValue);
        return (flags & RoadFlags.Bridge) != 0 ? (sbyte)1 : (flags & RoadFlags.Tunnel) != 0 ? (sbyte)-1 : (sbyte)0;
    }

    /// <summary>TLM attributes that go into <see cref="RoadAttributes"/> as flags.</summary>
    public static RoadAttrFlags ParseAttrFlags(string? kreisel, string? eigentuemer)
    {
        var f = RoadAttrFlags.None;
        if (kreisel is "Wahr") f |= RoadAttrFlags.Roundabout;
        if (eigentuemer is "Bund") f |= RoadAttrFlags.OwnerFederal;
        else if (eigentuemer is "Kanton") f |= RoadAttrFlags.OwnerCanton;
        return f;
    }

    public static RoadFlags ParseFlags(string? wanderwege, string? kunstbaute,
        string? verkehrsbeschraenkung, string? richtungsgetrennt)
    {
        var f = RoadFlags.None;

        f |= wanderwege switch
        {
            "Wanderweg" => RoadFlags.Hiking,
            "Bergwanderweg" or "Alpinwanderweg" => RoadFlags.MountainHiking,
            _ => RoadFlags.None,
        };

        // kunstbaute is a COMPOUND field: "Bruecke mit Treppe", "Gedeckte Bruecke",
        // "Unterfuehrung mit Treppe", "Bruecke mit Galerie". Matching it with equality
        // drops ~2,000 structures, and a bridge that loses its Bridge flag is draped onto
        // the terrain instead of keeping its deck height — which is exactly how a viaduct
        // ends up with a deep notch where a "Gedeckte Bruecke" span meets a "Bruecke" one.
        if (!string.IsNullOrEmpty(kunstbaute))
        {
            // Bruecke wins over Galerie in "Bruecke mit Galerie": the deck is carrying the
            // road over the gap, the gallery is only a roof over part of it.
            if (kunstbaute.Contains("Bruecke", StringComparison.Ordinal)
                || kunstbaute.Contains("Steg", StringComparison.Ordinal))
                f |= RoadFlags.Bridge;
            // a Galerie is a roofed gallery cut into a cliff — geometrically a tunnel
            else if (kunstbaute.Contains("Tunnel", StringComparison.Ordinal)
                     || kunstbaute.Contains("Unterfuehrung", StringComparison.Ordinal)
                     || kunstbaute.Contains("Galerie", StringComparison.Ordinal))
                f |= RoadFlags.Tunnel;

            if (kunstbaute.Contains("Treppe", StringComparison.Ordinal)) f |= RoadFlags.Stairs;
            if (kunstbaute.Contains("Furt", StringComparison.Ordinal)) f |= RoadFlags.Ford;
        }

        if (!string.IsNullOrEmpty(verkehrsbeschraenkung)
            && verkehrsbeschraenkung != "Keine" && verkehrsbeschraenkung != "k_W")
            f |= RoadFlags.Restricted;

        if (richtungsgetrennt is "Wahr" or "true" or "Ja")
            f |= RoadFlags.Divided;

        return f;
    }
}
