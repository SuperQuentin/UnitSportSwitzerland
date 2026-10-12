using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UnitSport.Playtest;

/// <summary>Where a scenario stands: never played, played but its code changed since, or the player's verdict.</summary>
public enum PlaytestStatus { Pending, Stale, Validated, Failed }

/// <summary>A note left on a scenario: a failure's reason, or what the player said when validating.</summary>
public sealed record PlaytestNote(string Date, string By, string Verdict, string Text);

/// <summary>
/// One scenario's committed result, <c>tests/playtests/&lt;id&gt;.json</c>: the verdict, who gave it,
/// when, on which commit, and the hash of the files the scenario covers at that moment.
/// </summary>
public sealed record PlaytestRecord
{
    public required string Id { get; init; }
    public string Title { get; init; } = "";
    /// <summary>"validated" or "failed".</summary>
    public required string Verdict { get; init; }
    public string By { get; init; } = "";
    public string Date { get; init; } = "";
    public string Commit { get; init; } = "";
    public required string CoverHash { get; init; }
    public string[] Covers { get; init; } = [];
    /// <summary>Every verdict ever given, newest last: the history survives a re-test.</summary>
    public List<PlaytestNote> Notes { get; init; } = [];
}

/// <summary>
/// The playtest ledger (#751, docs/notes/general/playtest.md): one JSON file per scenario under
/// <c>tests/playtests/</c>, committed, so a validation one person did counts for everyone, and two
/// people validating different scenarios never conflict.
///
/// <para>
/// A verdict stays good while the code it judged is the same: a scenario names the files it covers
/// (<see cref="PlaytestScenario.Covers"/>, globs relative to the repo), the ledger keeps a hash of their
/// content, and when that hash no longer matches the scenario is <see cref="PlaytestStatus.Stale"/>:
/// due again. Line endings are folded before hashing, so a Windows and a Linux checkout agree.
/// </para>
/// Pure .NET (files in, files out), so tier 0 tests it (PlaytestLedgerTests).
/// </summary>
public static class PlaytestLedger
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Directory names never searched for covered files (build output, data, other worktrees' copies).</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".godot", "bin", "obj", "test_output", "terrain_chunks", "node_modules" };

    public static PlaytestStatus StatusOf(PlaytestRecord? record, string currentHash) => record switch
    {
        null => PlaytestStatus.Pending,
        _ when record.CoverHash != currentHash => PlaytestStatus.Stale,
        _ when record.Verdict == "validated" => PlaytestStatus.Validated,
        _ => PlaytestStatus.Failed,
    };

    /// <summary>Whether a repo-relative path (forward slashes) matches a glob: <c>*</c> within a folder, <c>**</c> across folders, <c>?</c> one character.</summary>
    public static bool GlobMatch(string glob, string path) => GlobRegex(glob).IsMatch(path.Replace('\\', '/'));

    private static readonly Dictionary<string, Regex> Globs = new();

    private static Regex GlobRegex(string glob)
    {
        lock (Globs)
        {
            if (Globs.TryGetValue(glob, out var r)) return r;
            var sb = new StringBuilder("^");
            string g = glob.Replace('\\', '/');
            for (int i = 0; i < g.Length; i++)
            {
                char c = g[i];
                if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
                {
                    // "**/" also matches no folder at all: src/**/X.cs takes src/X.cs
                    bool slash = i + 2 < g.Length && g[i + 2] == '/';
                    sb.Append(slash ? "(?:.*/)?" : ".*");
                    i += slash ? 2 : 1;
                }
                else if (c == '*') sb.Append("[^/]*");
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            // a folder alone covers everything under it
            if (!g.Contains('*') && !g.Contains('?') && !g.Contains('.')) sb.Append("(?:/.*)?");
            return Globs[glob] = new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
        }
    }

    /// <summary>
    /// The repo-relative files under <paramref name="root"/> that any glob matches, sorted ordinally.
    /// A glob starting with <c>!</c> takes its matches out again (<c>!**/*Check.cs</c>); <c>.uid</c>
    /// files and the CLAUDE.md indexes never count, they say nothing about behaviour.
    /// </summary>
    public static List<string> Expand(string root, IEnumerable<string> globs)
    {
        var list = globs.ToList();
        var excludes = list.Where(g => g.StartsWith('!')).Select(g => g[1..]).ToList();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string glob in list.Where(g => !g.StartsWith('!')))
        {
            // only walk the folder the glob is anchored in (src/Player for src/Player/Car*.cs)
            string g = glob.Replace('\\', '/');
            int wild = g.IndexOfAny(['*', '?']);
            string fixedPart = wild < 0 ? g : g[..wild];
            string baseDir = fixedPart.Contains('/') ? fixedPart[..fixedPart.LastIndexOf('/')] : "";
            string start = Path.Combine(root, baseDir);
            if (wild < 0 && File.Exists(Path.Combine(root, g))) { found.Add(g); continue; }
            if (!Directory.Exists(start)) continue;
            foreach (string file in Walk(start))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (GlobMatch(g, rel)) found.Add(rel);
            }
        }
        found.RemoveWhere(f => f.EndsWith(".uid", StringComparison.Ordinal) || f.EndsWith("/CLAUDE.md", StringComparison.Ordinal)
            || excludes.Any(x => GlobMatch(x, f)));
        return found.ToList();
    }

    private static IEnumerable<string> Walk(string dir)
    {
        foreach (string f in Directory.EnumerateFiles(dir)) yield return f;
        foreach (string d in Directory.EnumerateDirectories(dir))
            if (!Skipped.Contains(Path.GetFileName(d)))
                foreach (string f in Walk(d)) yield return f;
    }

    /// <summary>SHA-256 of the files' paths and contents (CRLF folded to LF), short hex. No files: "none".</summary>
    public static string Hash(IEnumerable<(string Path, byte[] Content)> files)
    {
        using var sha = SHA256.Create();
        int count = 0;
        foreach (var (path, content) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            count++;
            byte[] name = Encoding.UTF8.GetBytes(path.Replace('\\', '/') + "\n");
            sha.TransformBlock(name, 0, name.Length, null, 0);
            byte[] body = FoldLineEndings(content);
            sha.TransformBlock(body, 0, body.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return count == 0 ? "none" : Convert.ToHexString(sha.Hash!)[..16].ToLowerInvariant();
    }

    /// <summary>The hash of what <paramref name="covers"/> matches under <paramref name="root"/> right now.</summary>
    public static string CurrentHash(string root, IEnumerable<string> covers) =>
        Hash(Expand(root, covers).Select(rel => (rel, File.ReadAllBytes(Path.Combine(root, rel)))));

    private static byte[] FoldLineEndings(byte[] b)
    {
        if (Array.IndexOf(b, (byte)'\r') < 0) return b;
        var o = new List<byte>(b.Length);
        for (int i = 0; i < b.Length; i++)
            if (!(b[i] == '\r' && i + 1 < b.Length && b[i + 1] == '\n')) o.Add(b[i]);
        return o.ToArray();
    }

    public static string FileOf(string dir, string id) => Path.Combine(dir, id + ".json");

    public static PlaytestRecord? Read(string dir, string id)
    {
        string file = FileOf(dir, id);
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<PlaytestRecord>(File.ReadAllText(file), Json); }
        catch (JsonException) { return null; }   // a hand-broken file counts as never played
    }

    /// <summary>Records a verdict, keeping the earlier notes. Returns the record written.</summary>
    public static PlaytestRecord Write(string dir, string id, string title, bool validated, string coverHash, string[] covers,
        string by, string commit, string note, DateTime now)
    {
        Directory.CreateDirectory(dir);
        var notes = Read(dir, id)?.Notes ?? [];
        string verdict = validated ? "validated" : "failed";
        string date = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", System.Globalization.CultureInfo.InvariantCulture);
        notes.Add(new PlaytestNote(date, by, verdict, note));
        var record = new PlaytestRecord
        {
            Id = id, Title = title, Verdict = verdict, By = by, Date = date, Commit = commit,
            CoverHash = coverHash, Covers = covers, Notes = notes,
        };
        File.WriteAllText(FileOf(dir, id), JsonSerializer.Serialize(record, Json).Replace("\r\n", "\n") + "\n");
        return record;
    }
}
