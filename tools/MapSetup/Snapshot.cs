using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UnitSport.Map;

namespace UnitSport.Tools.MapSetup;

/// <summary>
/// <c>--snapshot out.html [--size 140x45] [--keys "..."]</c>: renders the map screen without a
/// terminal, after replaying a key script, and writes it as HTML. The only way to look at the
/// screen from a script or a screenshot tool, since the real one needs a console to read keys.
/// </summary>
public static partial class Snapshot
{
    /// <summary>
    /// Space-separated keys: <c>left right up down</c> (<c>shift+left</c>, <c>right*5</c>),
    /// single characters (<c>R + - / [</c>), <c>space enter esc backspace tab</c>, and
    /// <c>text:Zermatt</c> to type a string.
    /// </summary>
    public static IEnumerable<ConsoleKeyInfo> ParseKeys(string script)
    {
        foreach (var raw in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("text:", StringComparison.Ordinal))
            {
                foreach (char ch in raw[5..].Replace('_', ' '))
                    yield return Key(ch);
                continue;
            }
            var token = raw;
            int repeat = 1;
            if (token.Length > 1 && token.LastIndexOf('*') is var star and > 0 && int.TryParse(token[(star + 1)..], out int r))
            {
                repeat = r;
                token = token[..star];
            }
            bool shift = token.StartsWith("shift+", StringComparison.OrdinalIgnoreCase);
            if (shift) token = token[6..];
            var info = token.ToLowerInvariant() switch
            {
                "left" => Special(ConsoleKey.LeftArrow, shift),
                "right" => Special(ConsoleKey.RightArrow, shift),
                "up" => Special(ConsoleKey.UpArrow, shift),
                "down" => Special(ConsoleKey.DownArrow, shift),
                "space" => new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift, false, false),
                "enter" => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
                "esc" => new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false),
                "backspace" => new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
                "pageup" => Special(ConsoleKey.PageUp, false),
                "pagedown" => Special(ConsoleKey.PageDown, false),
                _ when token.Length == 1 => Key(token[0]),
                _ => throw new ArgumentException($"unknown key '{raw}' in --keys"),
            };
            for (int i = 0; i < repeat; i++) yield return info;
        }
    }

    private static ConsoleKeyInfo Special(ConsoleKey key, bool shift) => new('\0', key, shift, false, false);

    private static ConsoleKeyInfo Key(char ch)
    {
        var key = ch switch
        {
            >= 'a' and <= 'z' => ConsoleKey.A + (ch - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (ch - 'A'),
            >= '0' and <= '9' => ConsoleKey.D0 + (ch - '0'),
            ' ' => ConsoleKey.Spacebar,
            '+' => ConsoleKey.OemPlus,
            '-' => ConsoleKey.OemMinus,
            '/' => ConsoleKey.Oem2,
            '[' => ConsoleKey.Oem4,
            ']' => ConsoleKey.Oem6,
            _ => ConsoleKey.NoName,
        };
        return new ConsoleKeyInfo(ch, key, char.IsUpper(ch), false, false);
    }

    [GeneratedRegex(@"\x1b\[([0-9;?]*)([A-Za-z])")]
    private static partial Regex Csi();

    /// <summary>The subset of ANSI the map writes (24-bit fg/bg, bold, reset, cursor moves) as HTML.</summary>
    public static string ToHtml(string ansi, string title)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
        html.Append("<style>body{background:#111;margin:12px}pre{font:14px/1.0 'Cascadia Mono',Consolas,monospace;"
                    + "color:#ddd;background:#000;display:inline-block;padding:4px;margin:0}span{white-space:pre}</style><pre>");
        string fg = "#ddd", bg = "transparent";
        bool bold = false;
        int pos = 0;
        var span = new StringBuilder();

        void Flush()
        {
            if (span.Length == 0) return;
            html.Append($"<span style=\"color:{fg};background:{bg}{(bold ? ";font-weight:bold" : "")}\">")
                .Append(WebUtility.HtmlEncode(span.ToString())).Append("</span>");
            span.Clear();
        }

        foreach (Match m in Csi().Matches(ansi))
        {
            AppendText(ansi[pos..m.Index]);
            pos = m.Index + m.Length;
            if (m.Groups[2].Value != "m") continue;
            Flush();
            var codes = m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            if (codes.Length == 0) { fg = "#ddd"; bg = "transparent"; bold = false; continue; }
            for (int i = 0; i < codes.Length; i++)
            {
                switch (codes[i])
                {
                    case 0: fg = "#ddd"; bg = "transparent"; bold = false; break;
                    case 1: bold = true; break;
                    case 22: bold = false; break;
                    case 38 when i + 4 < codes.Length && codes[i + 1] == 2:
                        fg = $"rgb({codes[i + 2]},{codes[i + 3]},{codes[i + 4]})"; i += 4; break;
                    case 48 when i + 4 < codes.Length && codes[i + 1] == 2:
                        bg = $"rgb({codes[i + 2]},{codes[i + 3]},{codes[i + 4]})"; i += 4; break;
                }
            }
        }
        AppendText(ansi[pos..]);
        Flush();
        html.Append("</pre>");
        return html.ToString();

        void AppendText(string text) => span.Append(text.Replace("\r", ""));
    }
}
