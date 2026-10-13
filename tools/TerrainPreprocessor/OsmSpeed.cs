using System.Globalization;

namespace UnitSport.Tools.Preprocessor;

/// <summary>OSM <c>maxspeed</c> values for the overlay (#711). Pure: linked into the unit tests.</summary>
public static class OsmSpeed
{
    /// <summary>
    /// A <c>maxspeed</c> as km/h text: a number, <c>N mph</c>, or Switzerland's implicit limits (<c>CH:urban</c> 50,
    /// <c>CH:rural</c> 80, <c>CH:trunk</c> 100, <c>CH:motorway</c> 120, <c>walk</c> 10); empty for anything else
    /// (<c>none</c>, <c>signals</c>, unknown) and for values outside 1..199.
    /// </summary>
    public static string Kmh(string? v)
    {
        v = v?.Trim();
        if (string.IsNullOrEmpty(v)) return "";
        int? kmh = v switch
        {
            "CH:urban" => 50, "CH:rural" => 80, "CH:trunk" => 100, "CH:motorway" => 120, "walk" or "CH:walk" => 10,
            _ when v.EndsWith(" mph", StringComparison.Ordinal) && double.TryParse(v[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out double mph)
                => (int)Math.Round(mph * 1.609344),
            _ when double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) => (int)Math.Round(n),
            _ => null,
        };
        return kmh is > 0 and < 200 ? kmh.Value.ToString(CultureInfo.InvariantCulture) : "";
    }
}
