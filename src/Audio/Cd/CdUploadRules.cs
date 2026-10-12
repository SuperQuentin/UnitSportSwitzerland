using System;
using System.IO;
using System.Text;

namespace UnitSport.Audio.Cd;

// Plain C#, no Godot: linked into the unit tests (tests/UnitSportSwitzerland.Tests, CdUploadTests).

/// <summary>What a player may upload as a CD (#736, <see cref="CdUpload"/>), checked on both sides.</summary>
public static class CdUploadRules
{
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>The audio files a player may pick (and the server accepts), lower case.</summary>
    public static readonly string[] Extensions = { ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".opus", ".aac" };

    public static bool AudioFile(string path) =>
        Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>
    /// The upload's name on the server's disk (it becomes the CD's title): the file's own name with
    /// no folders, cut to letters, digits and a few marks, at most 80 characters, with its audio
    /// extension (anything else ends in ".bin", which the burner never takes for audio by name).
    /// </summary>
    public static string SafeName(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        string stem = Path.GetFileNameWithoutExtension(Path.GetFileName(name.Replace('\\', '/')));
        var chars = new StringBuilder(stem.Length);
        foreach (char c in stem)
            chars.Append(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '(' or ')' or '\'' or '&' or ',' ? c : '_');
        string clean = chars.ToString().Trim(' ', '.', '_');
        if (clean.Length == 0) clean = "upload";
        if (clean.Length > 80) clean = clean[..80];
        return clean + (AudioFile("x" + ext) ? ext : ".bin");
    }
}
