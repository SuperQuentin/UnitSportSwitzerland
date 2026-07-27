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

public sealed class RoadTile
{
    public TileId Id { get; init; }
    public required List<RoadSegment> Segments { get; init; }
}

public static class RoadFormat
{
    /// <summary>"USRD" little-endian.</summary>
    public const uint Magic = 0x44525355;

    public const ushort Version = 1;
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
        _ => 3f,
    };

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
