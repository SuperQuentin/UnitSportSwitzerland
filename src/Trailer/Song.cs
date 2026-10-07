using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnitSport.Trailer;

/// <summary>
/// The trailer's song (#706): "Voxel Revolution", Kevin MacLeod (incompetech.com), CC BY 4.0.
/// Every cut lands on one of its bar lines, measured off the file (spectral-flux tempo, the comb's
/// phase): 123 BPM, the first downbeat at 0.186 s. The file is not in the repository;
/// <c>tools/trailer.sh</c> downloads it. Plain C#, tier-0 tested (<c>TrailerSongTests</c>).
/// </summary>
public static class Song
{
    public const double Bpm = 123.0;

    /// <summary>The first downbeat, s.</summary>
    public const double FirstBeat = 0.186;

    /// <summary>Where the music has faded out, s: the end of the last shot.</summary>
    public const double End = 127.0;

    public const string Title = "Voxel Revolution";
    public const string Credit = "\"Voxel Revolution\" Kevin MacLeod (incompetech.com)\nLicensed under Creative Commons: By Attribution 4.0";
    public const string Url = "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Voxel%20Revolution.mp3";

    public static double Beat => 60.0 / Bpm;
    public static double BarLength => 4 * Beat;

    /// <summary>Where bar <paramref name="k"/> (from 1) starts, s; bar 1 starts the film at 0.</summary>
    public static double Bar(int k) => k <= 1 ? 0 : FirstBeat + (k - 1) * BarLength;

    /// <summary>
    /// What is wrong with a cut, shots given as (number, first bar, bars) in film order: a shot under a
    /// bar, shots out of order, a gap or an overlap between two (each starts on the bar the one before
    /// ends), or a film that stops before the song does.
    /// </summary>
    public static IEnumerable<string> CutProblems(IReadOnlyList<(int Number, int FromBar, int Bars)> shots)
    {
        for (int i = 0; i < shots.Count; i++)
        {
            var s = shots[i];
            if (s.Bars < 1) yield return $"shot {s.Number} lasts {s.Bars} bars";
            if (i == 0)
            {
                if (s.FromBar != 1) yield return $"the film starts at bar {s.FromBar}, not 1";
                continue;
            }
            var before = shots[i - 1];
            if (s.Number <= before.Number) yield return $"shot {s.Number} comes after shot {before.Number}";
            if (s.FromBar != before.FromBar + before.Bars)
                yield return $"shot {s.Number} starts at bar {s.FromBar}, shot {before.Number} ends at bar {before.FromBar + before.Bars}";
        }
        if (shots.Count > 0 && Bar(shots[^1].FromBar + shots[^1].Bars) < End - 0.05)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"the last shot ends at {Bar(shots[^1].FromBar + shots[^1].Bars):F2} s, the song at {End:F2} s");
    }
}
