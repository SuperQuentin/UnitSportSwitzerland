using System.Globalization;

namespace UnitSport.Audio.Cd;

/// <summary>
/// <c>/cd [analysis | reanalyse [stale | all | &lt;id&gt;]]</c> (#1153): pure text in, what to do out.
/// <see cref="CdLibrary.Command"/> applies it where the library lives (the server, or offline here).
/// </summary>
public static class CdCommand
{
    public enum Op { Report, Stale, All, One }

    public const string Usage = "/cd [analysis | reanalyse [stale | all | <id>]]";

    /// <summary>Nothing or "analysis" reports; "reanalyse" alone means the stale ones.</summary>
    public static bool TryParse(string text, out Op op, out int id, out string error)
    {
        op = Op.Report;
        id = 0;
        error = string.Empty;
        string[] p = text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0 || p is ["analysis" or "versions" or "status"]) return true;
        if (p[0] is "reanalyse" or "reanalyze" or "analyse" or "analyze")
        {
            switch (p.Length == 1 ? "stale" : p[1])
            {
                case "stale" when p.Length <= 2: op = Op.Stale; return true;
                case "all" when p.Length == 2: op = Op.All; return true;
                case var s when p.Length == 2 && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id):
                    op = Op.One;
                    return true;
            }
        }
        error = $"'{text.Trim()}' is not a /cd command. Usage: {Usage}";
        return false;
    }

    /// <summary>"12 CDs: v3 x9 (vocal model x7, centre fallback x2), v2 x3; 1 analysing, 2 queued."</summary>
    public static string Describe(IEnumerable<(int Version, string Vocal)> cds, int current, bool busy, int queued)
    {
        var list = cds.ToList();
        if (list.Count == 0) return "No CDs in this library.";
        var parts = list.GroupBy(c => c.Version).OrderByDescending(g => g.Key).Select(g =>
        {
            string head = g.Key == 0 ? $"not analysed x{g.Count()}" : $"v{g.Key} x{g.Count()}";
            var vocal = g.Where(c => c.Vocal.Length > 0).GroupBy(c => c.Vocal).Select(v => $"{v.Key} x{v.Count()}").ToList();
            return vocal.Count > 0 ? $"{head} ({string.Join(", ", vocal)})" : head;
        });
        string queue = busy || queued > 0 ? $"; {(busy ? 1 : 0)} analysing, {queued} queued" : "";
        return $"{list.Count} CD(s), analyser v{current}: {string.Join(", ", parts)}{queue}.";
    }
}
