using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Terrain.Format;

/// <summary>What stands at a stand (#422): the aircraft the game parks there.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StandUse
{
    /// <summary>An A320 with airstairs docked at L1 and L2.</summary>
    Airliner,
    /// <summary>The AN-124 on the cargo apron.</summary>
    Heavy,
    /// <summary>The military cargo plane (#420) on the cargo apron.</summary>
    Military,
}

/// <summary>
/// A stand an aircraft is parked at (#422): the LV95 of the aircraft's origin (the node's, on the
/// ground under it), the nose's heading (degrees clockwise from grid north), and the OSM
/// <c>aeroway=parking_position</c> it was taken from (its <c>ref</c>, e.g. "E14").
/// </summary>
public sealed class AirportStand
{
    public string Id { get; set; } = "";
    public string Ref { get; set; } = "";
    public StandUse Use { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    public double Heading { get; set; }
    /// <summary>The ground there, m (the built tile's height at the stand; NaN unknown).</summary>
    public double Ground { get; set; }
}

/// <summary>
/// A runway (OSM <c>aeroway=runway</c> centreline on swissTLM3D's <c>Hartbelagpiste</c>) and its
/// profile on the built tiles: length, width, the steepest 100 m and the worst bump (the ground's
/// distance from a straight 200 m chord), and whether an airliner can use it.
/// </summary>
public sealed class AirportRunway
{
    public string Ref { get; set; } = "";
    public double E1 { get; set; }
    public double N1 { get; set; }
    public double E2 { get; set; }
    public double N2 { get; set; }
    public double Width { get; set; }
    public double Length { get; set; }
    /// <summary>Steepest grade over 100 m along the centreline, % (NaN: no tiles there).</summary>
    public double Grade { get; set; }
    /// <summary>Largest departure of the ground (centreline and both edges) from a 200 m chord, m.</summary>
    public double Bump { get; set; }
    public bool Usable { get; set; }
}

/// <summary>An airport (swissTLM3D <c>Flughafenareal</c>) with its runways and the stands chosen for parked aircraft.</summary>
public sealed class Airport
{
    public string Name { get; set; } = "";
    /// <summary>ICAO code (OSM <c>aeroway=aerodrome</c> <c>icao</c>), or the TLM name.</summary>
    public string Code { get; set; } = "";
    public double E { get; set; }
    public double N { get; set; }
    public List<AirportRunway> Runways { get; set; } = new();
    public List<AirportStand> Stands { get; set; } = new();
}

/// <summary>
/// The region's airports (#422), <c>airports.json</c> beside the manifest, written by the
/// preprocessor's <c>--airports</c> pass (<see cref="AirportPlanner"/>). Only the deciding peer (the
/// server, or a client offline) reads it: the parked aircraft it places are ordinary replicated
/// vehicles, so it is not streamed. See <c>docs/notes/world/airports.md</c>.
/// </summary>
public sealed class AirportIndex
{
    public const string FileName = "airports.json";

    public int Version { get; set; } = 1;
    public List<Airport> Airports { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static AirportIndex FromJson(string json) =>
        JsonSerializer.Deserialize<AirportIndex>(json, Options) ?? new AirportIndex();

    /// <summary>Every stand with its airport.</summary>
    public IEnumerable<(Airport Airport, AirportStand Stand)> Stands()
    {
        foreach (var a in Airports)
            foreach (var s in a.Stands) yield return (a, s);
    }
}
