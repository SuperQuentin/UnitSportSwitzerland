using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnitSport.Playtest.Recording;

/// <summary>
/// A playtest session's <c>report.md</c> (#844): what Claude reads first. Built from the bundle's
/// own files (events, notes, the perf summaries), so it can be written again at any time, by the
/// game at the end of a session or by hand after a crash (<c>--playtest-report &lt;dir&gt;</c>). No engine.
/// </summary>
public static class SessionReport
{
    public const string EventsFile = "events.jsonl", NotesFile = "notes.jsonl", ReportFile = "report.md";
    /// <summary>Seconds either side of a note whose events the report lists, and the top view shows.</summary>
    public const double NoteWindow = 10;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One line of <c>events.jsonl</c> or <c>notes.jsonl</c>: its time, its type, and the rest.</summary>
    public sealed record Line(double T, string Type, JsonObject Fields)
    {
        public string Str(string key) => Fields[key] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : Fields[key]?.ToJsonString() ?? "";
        public double? Num(string key) => Fields[key] is JsonValue v && v.TryGetValue(out double d) ? d : null;
    }

    public static List<Line> ReadLines(string path)
    {
        var lines = new List<Line>();
        if (!File.Exists(path)) return lines;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(f);
        while (r.ReadLine() is { } text)
        {
            if (text.Length == 0) continue;
            try
            {
                if (JsonNode.Parse(text) is not JsonObject o) continue;
                double t = o["t"]?.GetValue<double>() ?? 0;
                string type = o["type"]?.GetValue<string>() ?? "";
                lines.Add(new Line(t, type, o));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }   // a line cut short by a crash
        }
        return lines;
    }

    public static string Write(string dir)
    {
        string text = Build(dir);
        File.WriteAllText(Path.Combine(dir, ReportFile), text, new UTF8Encoding(false));
        return text;
    }

    public static string Build(string dir)
    {
        var events = ReadLines(Path.Combine(dir, EventsFile));
        var notes = ReadLines(Path.Combine(dir, NotesFile));
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        string T(double t) => t.ToString("0.0", Inv) + " s";

        L($"# Playtest session {Path.GetFileName(dir.TrimEnd('/', '\\'))}");
        L();
        var starts = events.Where(e => e.Type == "session" && e.Str("what") == "start").ToList();
        double end = events.Count > 0 ? events.Max(e => e.T) : 0;
        if (starts.FirstOrDefault() is { } first)
        {
            L($"- {first.Str("role")}, started {first.Str("wall")}, commit `{first.Str("commit")}`, {T(end)} long"
                + (starts.Count > 1 ? $", {starts.Count} launches (relaunched at {string.Join(", ", starts.Skip(1).Select(s => T(s.T)))})" : ""));
            L($"- args: `{first.Str("args")}`");
        }
        int errors = events.Count(e => e.Type == "log" && e.Str("level") == "error");
        int warnings = events.Count(e => e.Type == "log" && e.Str("level") == "warn");
        var verdicts = events.Where(e => e.Type == "scenario" && e.Str("what") == "verdict").ToList();
        L($"- {notes.Count} notes, {verdicts.Count(v => v.Str("verdict") == "failed")} failed and {verdicts.Count(v => v.Str("verdict") == "validated")} validated scenarios, {errors} errors, {warnings} warnings logged");
        L("- files: `trace.bin` (players and objects, 10 Hz), `events.jsonl`, `notes/`, `perf/`; top view: "
            + $"`--topview {dir.Replace('\\', '/')} --from A --to B --out test_output/topview.png` (docs/notes/general/topview.md)");
        L();

        L("## Scenarios");
        L();
        var scenario = events.Where(e => e.Type == "scenario").ToList();
        if (scenario.Count == 0) L("None played (free play).");
        else
        {
            L("| t | scenario | what | note |");
            L("|---|---|---|---|");
            foreach (var e in scenario)
                L($"| {T(e.T)} | {e.Str("id")} | {(e.Str("what") == "verdict" ? e.Str("verdict").ToUpperInvariant() : e.Str("what"))} | {Cell(e.Str("note"))} |");
        }
        L();

        L("## Notes");
        L();
        if (notes.Count == 0) L("None.");
        foreach (var n in notes)
        {
            int id = (int)(n.Num("id") ?? 0);
            L($"### Note {id}: {T(n.T)}{(n.Str("scenario").Length > 0 ? $", scenario `{n.Str("scenario")}`" : ", free play")}");
            L();
            L($"> {n.Str("text").Replace("\n", " ")}");
            L();
            L($"- from {n.Str("source")}, at E {n.Num("e")?.ToString("0.0", Inv)} N {n.Num("n")?.ToString("0.0", Inv)} alt {n.Num("alt")?.ToString("0.0", Inv)}, {n.Str("ride")}");
            var pics = new List<string>();
            if (n.Str("shot").Length > 0) pics.Add($"screenshot `{n.Str("shot")}`");
            if (n.Str("top").Length > 0) pics.Add($"top view ±{NoteWindow:0} s `{n.Str("top")}`");
            if (pics.Count > 0) L("- " + string.Join(", ", pics));
            L($"- top view again: `--topview {dir.Replace('\\', '/')} --from {(n.T - NoteWindow).ToString("0.#", Inv)} --to {(n.T + NoteWindow).ToString("0.#", Inv)} --at {n.T.ToString("0.#", Inv)} --out test_output/note{id}.png`");
            var near = events.Where(e => Math.Abs(e.T - n.T) <= NoteWindow && e.Type != "note").ToList();
            var happenings = near.Where(e => e.Type != "log").Take(15).ToList();
            var bad = near.Where(e => e.Type == "log" && e.Str("level") is "error" or "warn").Take(10).ToList();
            if (happenings.Count > 0)
            {
                L("- around it:");
                foreach (var e in happenings) L($"  - {T(e.T)} {e.Type}: {Summary(e)}");
            }
            if (bad.Count > 0)
            {
                L("- errors and warnings around it:");
                foreach (var e in bad) L($"  - {T(e.T)} `{Clip(e.Str("text"), 160)}`");
            }
            L();
        }

        L("## Errors and warnings");
        L();
        var grouped = events.Where(e => e.Type == "log" && e.Str("level") is "error" or "warn")
            .GroupBy(e => e.Str("text"))
            .OrderByDescending(g => g.Count())
            .Take(20)
            .ToList();
        if (grouped.Count == 0) L("None.");
        foreach (var g in grouped)
            L($"- {g.Count()}× from {T(g.First().T)} ({g.First().Str("level")}): `{Clip(g.Key, 200)}`");
        L();

        L("## Performance");
        L();
        var perfDirs = Directory.Exists(dir) ? Directory.GetDirectories(dir, "perf*").OrderBy(d => d, StringComparer.Ordinal).ToList() : [];
        bool any = false;
        foreach (string p in perfDirs)
        {
            string summary = Path.Combine(p, "summary.txt");
            if (!File.Exists(summary)) continue;
            any = true;
            var lines = File.ReadAllLines(summary);
            int at = Array.FindIndex(lines, l => l.Contains("DIAGNOSIS", StringComparison.Ordinal));
            L($"`{Path.GetFileName(p)}/summary.txt`:");
            foreach (string line in at >= 0 ? lines.Skip(at + 1) : lines.Take(8))
                if (line.Trim().Length > 0) L(line);
        }
        if (!any) L("No perf recording (a server, or the session is still running).");
        L();

        var movies = events.Where(e => e.Type == "movie").ToList();
        if (movies.Count > 0)
        {
            L("## 3D replay");
            L();
            foreach (var m in movies) L($"- {Summary(m)}");
            L();
        }
        return sb.ToString();
    }

    /// <summary>An event's fields but its time and type, as <c>key=value</c>.</summary>
    public static string Summary(Line e) => string.Join(", ", e.Fields
        .Where(kv => kv.Key is not ("t" or "type"))
        .Select(kv => $"{kv.Key}={(kv.Value is JsonValue v && v.TryGetValue(out string? s) ? s : kv.Value?.ToJsonString())}"));

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
