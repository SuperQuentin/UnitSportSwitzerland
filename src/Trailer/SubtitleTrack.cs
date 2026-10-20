using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UnitSport.Trailer;

/// <summary>
/// One English line of a film on screen (#862): its stable id, what kind of line it is
/// (<see cref="Caption"/>, <see cref="Bubble"/> or <see cref="Speech"/>), the English, and the
/// frames it shows on, counted from its shot's first frame (<paramref name="To"/> excluded).
/// </summary>
public sealed record Cue(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("en")] string En,
    [property: JsonPropertyName("from")] int From,
    [property: JsonPropertyName("to")] int To)
{
    public const string Caption = "caption", Bubble = "bubble", Speech = "chat";
}

/// <summary>
/// A filmed shot's lines (<c>shotNN.cues.json</c> beside <c>shotNN.mp4</c>): the shot, the frame
/// rate, how many frames it has, and its cues in its own frames, so a cut of shots from several
/// renders adds them up frame-exactly as the video's concat does.
/// </summary>
public sealed record CueFile(
    [property: JsonPropertyName("shot")] int Shot,
    [property: JsonPropertyName("fps")] int Fps,
    [property: JsonPropertyName("frames")] int Frames,
    [property: JsonPropertyName("cues")] IReadOnlyList<Cue> Cues);

/// <summary>One subtitle on screen: from frame <paramref name="From"/> of the cut to <paramref name="To"/>, the text.</summary>
public readonly record struct Subtitle(int From, int To, string Text);

/// <summary>
/// The films' subtitle tracks (#862): the ids of English lines, the translations file
/// (<c>docs/trailer/&lt;film&gt;.subtitles.json</c>: <c>{"id": {"en": "...", "fr": "...", ...}}</c>),
/// a cut's lines laid end to end, and SRT. Overlapping lines are merged into one subtitle per
/// stretch of time (MP4's <c>mov_text</c> cannot overlap), two speakers each on a line opened by
/// a dash. Plain C#, tier-0 tested (<c>SubtitleTrackTests</c>).
/// </summary>
public static class SubtitleTrack
{
    /// <summary>The languages a track can be, in the order they are muxed: the two-letter code, ISO 639-2, the track's title.</summary>
    public static readonly (string Code, string Iso, string Title)[] Languages =
    [
        ("en", "eng", "English"),
        ("fr", "fra", "Français"),
        ("de", "deu", "Deutsch"),
        ("it", "ita", "Italiano"),
        ("rm", "roh", "Rumantsch"),
    ];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// A line's id when its script gives none: its English made a slug ("Who stole the fondue?!"
    /// → <c>who-stole-the-fondue</c>), at most 48 letters cut at a word. It survives retiming and
    /// moving the line to another shot; rewording the English makes it a new line, whose
    /// translations are then missing (and said so) rather than silently stale.
    /// </summary>
    public static string Slug(string english)
    {
        var sb = new StringBuilder();
        bool dash = false;
        foreach (char ch in english.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            char c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (dash && sb.Length > 0) sb.Append('-');
                sb.Append(c);
                dash = false;
            }
            else if (c != '\'' && c != '’') dash = true;
        }
        string slug = sb.ToString();
        if (slug.Length > 48)
        {
            int cut = slug.LastIndexOf('-', 48);
            slug = slug[..(cut > 16 ? cut : 48)];
        }
        return slug.Length > 0 ? slug : "line";
    }

    /// <summary>The translations file: id → language → text. Empty for an empty or missing text.</summary>
    public static Dictionary<string, Dictionary<string, string>> ReadTranslations(string? json)
    {
        var all = new Dictionary<string, Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(json)) return all;
        if (JsonNode.Parse(json) is not JsonObject root) return all;
        foreach (var (id, node) in root)
        {
            var langs = new Dictionary<string, string>();
            if (node is JsonObject o)
                foreach (var (lang, text) in o)
                    if (text is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)) langs[lang] = s;
            all[id] = langs;
        }
        return all;
    }

    /// <summary>
    /// The languages a cut gets tracks in: English and every language of <see cref="Languages"/>
    /// that at least one line of the file has, in that order.
    /// </summary>
    public static List<string> LanguagesOf(Dictionary<string, Dictionary<string, string>> translations) =>
        Languages.Select(l => l.Code).Where(code => code == "en" || translations.Values.Any(t => t.ContainsKey(code))).ToList();

    /// <summary>
    /// The subtitles of a cut in <paramref name="lang"/>: the shots' cues end to end (each shot
    /// starting where the frames before it end), each line in that language, or in English with a
    /// warning when it has no translation (a warning too when the file's English is not the film's,
    /// a line reworded under an explicit id).
    /// </summary>
    public static List<Subtitle> Cut(IReadOnlyList<CueFile> shots, Dictionary<string, Dictionary<string, string>> translations,
        string lang, List<string> warnings)
    {
        var lines = new List<(int From, int To, string Kind, string Text)>();
        int offset = 0;
        foreach (var shot in shots)
        {
            foreach (var c in shot.Cues)
            {
                int from = Math.Clamp(c.From, 0, shot.Frames), to = Math.Clamp(c.To, 0, shot.Frames);
                if (to <= from) continue;
                string text = c.En;
                translations.TryGetValue(c.Id, out var t);
                if (lang != "en")
                {
                    if (t != null && t.TryGetValue(lang, out var said)) text = said;
                    else warnings.Add($"{lang}: no translation of \"{c.Id}\" (shot {shot.Shot}): English instead");
                }
                else if (t != null && t.TryGetValue("en", out var en) && en != c.En)
                    warnings.Add($"\"{c.Id}\" (shot {shot.Shot}) reads \"{c.En}\" in the film but \"{en}\" in the translations: check them");
                lines.Add((offset + from, offset + to, c.Kind, text));
            }
            offset += shot.Frames;
        }
        return Flatten(lines);
    }

    /// <summary>
    /// Lines that may overlap made into subtitles that do not: the time cut wherever a line starts
    /// or ends, each stretch showing every line on then, in the order they started. Two or more
    /// people talking (bubbles, chat) get a dash each; captions stand plain.
    /// </summary>
    public static List<Subtitle> Flatten(IReadOnlyList<(int From, int To, string Kind, string Text)> lines)
    {
        var order = lines.Select((l, i) => (l, i)).OrderBy(x => x.l.From).ThenBy(x => x.i).Select(x => x.l).ToList();
        var cuts = order.SelectMany(l => new[] { l.From, l.To }).Distinct().OrderBy(f => f).ToList();
        var result = new List<Subtitle>();
        for (int k = 0; k + 1 < cuts.Count; k++)
        {
            int a = cuts[k], b = cuts[k + 1];
            var on = order.Where(l => l.From <= a && l.To >= b).ToList();
            if (on.Count == 0) continue;
            int talking = on.Count(l => l.Kind != Cue.Caption);
            string text = string.Join("\n", on.Select(l => talking > 1 && l.Kind != Cue.Caption ? "- " + l.Text : l.Text));
            // one subtitle while the same lines stay up
            if (result.Count > 0 && result[^1].To == a && result[^1].Text == text) result[^1] = result[^1] with { To = b };
            else result.Add(new Subtitle(a, b, text));
        }
        return result;
    }

    /// <summary>SRT of <paramref name="subtitles"/> at <paramref name="fps"/> frames a second (times to the millisecond).</summary>
    public static string Srt(IReadOnlyList<Subtitle> subtitles, int fps)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < subtitles.Count; i++)
        {
            var s = subtitles[i];
            sb.Append(i + 1).Append('\n')
                .Append(Time(s.From, fps)).Append(" --> ").Append(Time(s.To, fps)).Append('\n')
                .Append(s.Text.Replace("\r", "")).Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>A frame's time as SRT writes it, <c>HH:MM:SS,mmm</c>.</summary>
    public static string Time(int frame, int fps)
    {
        long ms = (long)Math.Round(frame * 1000.0 / fps);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    /// <summary>
    /// The translations file brought up to a film's lines (<paramref name="film"/>, in film order):
    /// a line it lacks is added with its English; the others keep their translations, their
    /// English set to the film's (a line reworded under an explicit id is listed in
    /// <paramref name="reworded"/>: its translations need a look). Lines the film no longer has
    /// stay, at the end, and are listed in <paramref name="unused"/>.
    /// </summary>
    public static string Sync(string? json, IReadOnlyList<(string Id, string En)> film, List<string> unused, List<string> reworded)
    {
        var old = ReadTranslations(json);
        var root = new JsonObject();
        foreach (var (id, en) in film)
        {
            if (root.ContainsKey(id)) continue;
            var entry = new JsonObject();
            var had = old.TryGetValue(id, out var t) ? t : new Dictionary<string, string>();
            if (had.TryGetValue("en", out var e) && e != en && had.Count > 1) reworded.Add(id);
            entry["en"] = en;
            foreach (var (code, _, _) in Languages)
                if (code != "en" && had.TryGetValue(code, out var s)) entry[code] = s;
            foreach (var (code, s) in had)
                if (!entry.ContainsKey(code)) entry[code] = s;
            root[id] = entry;
        }
        foreach (var (id, langs) in old)
        {
            if (root.ContainsKey(id)) continue;
            unused.Add(id);
            var entry = new JsonObject();
            foreach (var (code, s) in langs) entry[code] = s;
            root[id] = entry;
        }
        return root.ToJsonString(Json) + "\n";
    }
}
