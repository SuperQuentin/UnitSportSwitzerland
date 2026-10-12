using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnitSport.Trailer;

/// <summary>
/// A film's song (#706, #717): every cut lands on one of its bar lines, measured off the file
/// (spectral-flux tempo, the comb's phase). The files are not in the repository;
/// <c>tools/trailer.sh</c> downloads them. Plain C#, tier-0 tested (<c>TrailerSongTests</c>).
/// </summary>
/// <param name="FirstBeat">The first downbeat, s.</param>
/// <param name="End">Where the film ends, s: the end of the last shot.</param>
public sealed record Song(string Title, double Bpm, double FirstBeat, double End, string Credit, string Url)
{
    /// <summary>The trailers' (#706): Kevin MacLeod, CC BY 4.0. 123 BPM, the first downbeat at 0.186 s.</summary>
    public static readonly Song VoxelRevolution = new("Voxel Revolution", 123.0, 0.186, 127.0,
        "\"Voxel Revolution\" Kevin MacLeod (incompetech.com)\nLicensed under Creative Commons: By Attribution 4.0",
        "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Voxel%20Revolution.mp3");

    /// <summary>
    /// The Drognens clip's (#717): Kevin MacLeod with James Gavins (voice) and Bryan Teoh, CC BY 4.0.
    /// 121 BPM (incompetech's figure), the first downbeat at 0.322 s, 14 bars of music fading out by
    /// 28.5 s; the film runs a 15th bar, to 30.07 s, the end card over the silence.
    /// </summary>
    public static readonly Song IGotAStick = new("I Got a Stick Feat James Gavins", 121.0, 0.322, 30.07,
        "\"I Got a Stick Feat James Gavins\" Kevin MacLeod, James Gavins, Bryan Teoh (incompetech.com)\nLicensed under Creative Commons: By Attribution 4.0",
        "https://incompetech.com/music/royalty-free/mp3-royaltyfree/I%20Got%20a%20Stick%20Feat%20James%20Gavins.mp3");

    public double Beat => 60.0 / Bpm;
    public double BarLength => 4 * Beat;

    /// <summary>Where bar <paramref name="k"/> (from 1) starts, s; bar 1 starts the film at 0.</summary>
    public double Bar(int k) => k <= 1 ? 0 : FirstBeat + (k - 1) * BarLength;

    /// <summary>
    /// What is wrong with a cut, shots given as (number, first bar, bars) in film order: a shot under a
    /// bar, shots out of order, a gap or an overlap between two (each starts on the bar the one before
    /// ends), or a film that stops before the song does.
    /// </summary>
    public IEnumerable<string> CutProblems(IReadOnlyList<(int Number, int FromBar, int Bars)> shots)
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
