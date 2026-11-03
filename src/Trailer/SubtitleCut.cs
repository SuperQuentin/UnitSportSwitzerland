using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using UnitSport.Core;

namespace UnitSport.Trailer;

/// <summary>
/// The films' subtitle files (#862), run by <c>tools/trailer.sh</c> with no world:
/// <list type="bullet">
/// <item><c>--trailer-subtitles DIR --trailer-film F</c>: a render's cut (<c>DIR/shots/shotNN.cues.json</c>,
/// in the cut's order) and the film's translations (<c>docs/trailer/F.subtitles.json</c>) into
/// <c>DIR/F.LANG.srt</c> for English and every language the file has; a line with no
/// translation is English in that track, with a warning.</item>
/// <item><c>--trailer-lines --trailer-film F</c>: every English line of the film (captions,
/// bubbles, chat) added to its translations file, the translations kept; prints what each
/// language still lacks.</item>
/// </list>
/// Prints <c>[subtitles] RESULT: ok</c> and quits.
/// </summary>
public partial class SubtitleCut : Node
{
    public static bool Requested => CmdArgs.Has("--trailer-subtitles") || CmdArgs.Has("--trailer-lines");

    private static string Film => CmdArgs.Value("--trailer-film") ?? "story";

    /// <summary>The film's translations file, in the project's <c>docs/trailer/</c>.</summary>
    public static string TranslationsPath => ProjectSettings.GlobalizePath($"res://docs/trailer/{Film}.subtitles.json");

    public override void _Ready()
    {
        bool ok;
        try
        {
            ok = CmdArgs.Value("--trailer-subtitles") is { } dir ? Cut(dir) : Lines();
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[subtitles] {e.Message}");
            ok = false;
        }
        GD.Print(ok ? "[subtitles] RESULT: ok" : "[subtitles] RESULT: FAILED");
        GetTree().Quit(ok ? 0 : 1);
    }

    private static bool Cut(string dir)
    {
        var shotsDir = Path.Combine(dir, "shots");
        var files = new List<CueFile>();
        foreach (var video in Directory.GetFiles(shotsDir, "shot*.mp4").OrderBy(f => f, System.StringComparer.Ordinal))
        {
            var cues = Path.ChangeExtension(video, ".cues.json");
            if (!File.Exists(cues))
            {
                GD.PrintErr($"[subtitles] {Path.GetFileName(video)} has no cues (filmed before #862): no subtitles from the cut");
                return false;
            }
            files.Add(JsonSerializer.Deserialize<CueFile>(File.ReadAllText(cues))!);
        }
        if (files.Count == 0) { GD.PrintErr($"[subtitles] no shots in {shotsDir}"); return false; }
        string path = TranslationsPath;
        var translations = SubtitleTrack.ReadTranslations(File.Exists(path) ? File.ReadAllText(path) : null);
        int fps = files[0].Fps;
        foreach (var lang in SubtitleTrack.LanguagesOf(translations))
        {
            var warnings = new List<string>();
            var subs = SubtitleTrack.Cut(files, translations, lang, warnings);
            foreach (var w in warnings.Distinct()) GD.PrintErr($"[subtitles] warning: {w}");
            var srt = Path.Combine(dir, $"{Film}.{lang}.srt");
            File.WriteAllText(srt, SubtitleTrack.Srt(subs, fps));
            GD.Print($"[subtitles] {srt}: {subs.Count} subtitle(s)");
        }
        return true;
    }

    private static bool Lines()
    {
        var film = TrailerDirector.Film;
        var lines = new List<(string Id, string En)>();
        foreach (var shot in film)
            foreach (var c in ShotCues.Of(shot, 30))
                if (!lines.Any(l => l.Id == c.Id)) lines.Add((c.Id, c.En));
        string path = TranslationsPath;
        var unused = new List<string>();
        var reworded = new List<string>();
        string json = SubtitleTrack.Sync(File.Exists(path) ? File.ReadAllText(path) : null, lines, unused, reworded);
        File.WriteAllText(path, json);
        var back = SubtitleTrack.ReadTranslations(json);
        GD.Print($"[subtitles] {path}: {lines.Count} line(s)");
        foreach (var (code, _, _) in SubtitleTrack.Languages.Skip(1))
        {
            int missing = lines.Count(l => !back[l.Id].ContainsKey(code));
            GD.Print($"[subtitles] {code}: {lines.Count - missing} translated, {missing} missing");
        }
        foreach (var id in reworded) GD.PrintErr($"[subtitles] warning: \"{id}\" was reworded in English: check its translations");
        foreach (var id in unused) GD.PrintErr($"[subtitles] warning: \"{id}\" is no longer in the film (kept)");
        return true;
    }
}
