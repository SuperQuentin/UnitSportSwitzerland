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

    public int PointCount => Points.Length / 3;
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
    /// </summary>
    public const ushort Version = 2;
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
