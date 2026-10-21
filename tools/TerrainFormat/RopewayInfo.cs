using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Terrain.Format;

/// <summary>
/// One OSM aerialway (#915): its two ends in LV95 and what OSM says of it. The game matches it to a
/// swissTLM3D ropeway line by those ends; TLM has the cable's real heights, OSM the rest.
/// </summary>
/// <param name="Type">OSM <c>aerialway</c>: cable_car, gondola, mixed_lift, chair_lift, drag_lift, t-bar, j-bar, platter, rope_tow, magic_carpet.</param>
/// <param name="Minutes"><c>aerialway:duration</c>, the ride end to end; 0 when not mapped.</param>
/// <param name="Seats"><c>aerialway:occupancy</c>; 0 when not mapped.</param>
/// <param name="Detachable"><c>aerialway:detachable</c>: null when not mapped (a chairlift is taken as detachable).</param>
public sealed record RopewayInfo(double E0, double N0, double E1, double N1, string Type, float Minutes, int Seats,
    bool? Detachable, string Name);

/// <summary>
/// <c>ropeways.json</c>, the region's OSM aerialways (#915): written with the ski slopes
/// (<c>TerrainPreprocessor --pistes</c>, ODbL like them), read by every peer, streamed to clients like
/// the landings. Absent: every lift keeps what swissTLM3D gives it.
/// </summary>
public sealed class RopewayInfoIndex
{
    public const string FileName = "ropeways.json";

    public List<RopewayInfo> Ropeways { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static RopewayInfoIndex FromJson(string json) => JsonSerializer.Deserialize<RopewayInfoIndex>(json, Options) ?? new();

    /// <summary>The aerialway whose ends match the line's (either way round), both within <paramref name="reach"/> m; the nearest such.</summary>
    public RopewayInfo? Match(double e0, double n0, double e1, double n1, double reach = 60)
    {
        RopewayInfo? best = null;
        double bestD = double.MaxValue;
        foreach (var r in Ropeways)
        {
            double same = Math.Max(D(r.E0, r.N0, e0, n0), D(r.E1, r.N1, e1, n1));
            double swapped = Math.Max(D(r.E0, r.N0, e1, n1), D(r.E1, r.N1, e0, n0));
            double d = Math.Min(same, swapped);
            if (d <= reach && d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    private static double D(double ea, double na, double eb, double nb) => Math.Sqrt((ea - eb) * (ea - eb) + (na - nb) * (na - nb));
}
