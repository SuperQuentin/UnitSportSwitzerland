using System.Text;
using System.Text.RegularExpressions;

namespace UnitSport.Core;

/// <summary>
/// GitHub release notes (the markdown <c>tools/release.sh</c> writes) as BBCode for a <c>RichTextLabel</c>
/// (#1413): headings, bullets and bold are kept; image lines, links' URLs and the "Full diff" line are
/// dropped; brackets are escaped. No Godot, so tier-0 tests cover it.
/// </summary>
public static class ReleaseNotes
{
    private static readonly Regex Heading = new(@"^\s{0,3}(#{1,6})\s+(.*)$");
    private static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(.*)$");
    private static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)");
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*");

    /// <summary>The notes of <paramref name="releases"/> in the order given, each under its tag.</summary>
    public static string Format(IEnumerable<UpdateInfo.Release> releases)
    {
        var sb = new StringBuilder();
        foreach (var r in releases)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("[font_size=20][b]").Append(Escape(r.Tag)).Append("[/b][/font_size]\n").Append(ToBbcode(r.Body));
        }
        return sb.ToString();
    }

    public static string ToBbcode(string? markdown)
    {
        var lines = new List<string>();
        var pending = new List<string>(); // a heading and its blank lines, kept only once content follows
        foreach (string raw in (markdown ?? "").Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            string t = line.TrimStart();
            if (t.StartsWith("![") || t.StartsWith("[![") || t.StartsWith("<img", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("**Full diff", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length == 0)
            {
                var sink = pending.Count > 0 ? pending : lines;
                if (sink.Count > 0 && sink[^1].Length > 0) sink.Add("");
                continue;
            }
            if (Heading.Match(line) is { Success: true } h)
            {
                int size = h.Groups[1].Length <= 2 ? 18 : 16;
                pending.Clear();
                pending.Add($"[font_size={size}][b]{Inline(h.Groups[2].Value)}[/b][/font_size]");
                continue;
            }
            lines.AddRange(pending);
            pending.Clear();
            if (Bullet.Match(line) is { Success: true } b)
                lines.Add(new string(' ', b.Groups[1].Length) + "• " + Inline(b.Groups[2].Value));
            else lines.Add(Inline(t));
        }
        return string.Join("\n", lines).Trim('\n');
    }

    /// <summary>One line's text: links keep their label, <c>**bold**</c> turns bold, backticks go.</summary>
    private static string Inline(string text) =>
        Bold.Replace(Escape(Link.Replace(text, "$1").Replace("`", "")), "[b]$1[/b]");

    private static string Escape(string text) => Regex.Replace(text, @"[\[\]]", m => m.Value == "[" ? "[lb]" : "[rb]");
}
