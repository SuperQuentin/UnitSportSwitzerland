using System.Globalization;
using System.Text.RegularExpressions;

namespace UnitSport.World.Locate;

/// <summary>A point written in a chat line as <c>@E,N</c> (#1277): where it starts and ends in the text.</summary>
public readonly record struct CoordMatch(double E, double N, int Start, int Length);

/// <summary>
/// The chat's coordinate syntax (#1277): <c>@2533450,1152300</c>, LV95 metres, east then north. Any line
/// carrying one gets Teleport / Track / Map / Copy under it (<c>Core/ChatUi</c>), whoever wrote it, so
/// players can send each other places by typing them. Pure C#, tier 0 (<c>LocateTests</c>).
/// </summary>
public static partial class LocateCoords
{
    /// <summary>LV95 bounds round Switzerland, with room for the border regions: anything else is not a place.</summary>
    public const double MinE = 2_400_000, MaxE = 2_900_000, MinN = 1_000_000, MaxN = 1_350_000;

    [GeneratedRegex(@"@\s?(\d{7}(?:\.\d+)?)\s?[,/ ]\s?(\d{7}(?:\.\d+)?)")]
    private static partial Regex Pattern();

    /// <summary>"@2533450,1152300": whole metres, no spaces, the form every reply writes.</summary>
    public static string Format(double e, double n) =>
        "@" + Math.Round(e).ToString("0", CultureInfo.InvariantCulture) + "," + Math.Round(n).ToString("0", CultureInfo.InvariantCulture);

    public static bool InRange(double e, double n) => e is >= MinE and <= MaxE && n is >= MinN and <= MaxN;

    /// <summary>Every coordinate in a line, in order (in range only).</summary>
    public static List<CoordMatch> FindAll(string text)
    {
        var found = new List<CoordMatch>();
        if (text.IndexOf('@') < 0) return found;
        foreach (Match m in Pattern().Matches(text))
        {
            double e = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double n = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (InRange(e, n)) found.Add(new CoordMatch(e, n, m.Index, m.Length));
        }
        return found;
    }

    /// <summary>
    /// A command's argument as a point: <c>@E,N</c>, <c>E,N</c> or <c>E N</c>. What the chat's buttons send
    /// (<c>/go</c>, <c>/track</c>) and what a player types.
    /// </summary>
    public static bool TryParse(string text, out double e, out double n)
    {
        e = n = 0;
        string t = text.Trim();
        if (!t.StartsWith('@')) t = "@" + t;
        var all = FindAll(t);
        if (all is not [var c, ..]) return false;
        (e, n) = (c.E, c.N);
        return true;
    }

    /// <summary>"640 m", "4.2 km", "38 km".</summary>
    public static string Distance(double metres) =>
        metres < 1000 ? $"{Math.Round(metres / 10) * 10:0} m"
        : metres < 20_000 ? (metres / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km"
        : (metres / 1000).ToString("0", CultureInfo.InvariantCulture) + " km";

    private static readonly string[] Points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>Which way <paramref name="toE"/>, <paramref name="toN"/> lies from the first point: one of eight, or "here".</summary>
    public static string Compass(double fromE, double fromN, double toE, double toN)
    {
        double de = toE - fromE, dn = toN - fromN;
        if (de * de + dn * dn < 25 * 25) return "here";
        double bearing = Math.Atan2(de, dn) * 180 / Math.PI;
        if (bearing < 0) bearing += 360;
        return Points[(int)Math.Round(bearing / 45) % 8];
    }
}
