// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-montreuxjazz.md).

namespace UnitSport.Concert;

/// <summary>What a bar of a jazz night's set is: a song, the beat between songs, the call for the encore, the encore, or the rest before the next set.</summary>
public enum JazzPart : byte { Song, Between, Call, Bis, Rest }

/// <summary>Where a moment of the show is in a jazz set (<see cref="JazzSet.At"/>).</summary>
/// <param name="Set">The set's number since the grid's origin (a set is the songs, the call, the bis and the rest).</param>
/// <param name="Part">What is playing.</param>
/// <param name="Song">The song's number in the set (0-based; the bis is the one after the last).</param>
/// <param name="Section">The made-up song's section, as the light desk plays it.</param>
/// <param name="SectionBar">Bars since the section began.</param>
/// <param name="LastSection">Whether it is the song's last section.</param>
/// <param name="BeatInPart">Beats since the part began.</param>
public readonly record struct JazzMoment(long Set, JazzPart Part, int Song, ShowSection Section, int SectionBar, bool LastSection, double BeatInPart);

/// <summary>
/// The shape of a jazz night's set at Montreux (#1394), a pure function of the show's beat count so the light desk, the server and the tests read the
/// same clock. A set is <c>songs</c> made-up songs (six sections of <see cref="SectionBars"/> bars, a bar of dark between them), the band leaves and the
/// crowd claps it back (<see cref="CallBars"/>: two beats of silence, <see cref="CallBeats"/> beats of the clap ring, two more), the bis (a
/// short chorus and a gentle peak), and <see cref="RestBars"/> bars of rest. The sets follow each other for as long as the show is on. Songs are short on
/// purpose: the show plays 20:30 to 23:30 of the game clock, three real minutes at the default day length.
/// </summary>
public static class JazzSet
{
    public const int SectionBars = 4, SongBars = 6 * SectionBars + 1, CallBars = 3, BisBars = 8, RestBars = 2;

    /// <summary>Beats of the clap ring in the call, after <see cref="CallLeadBeats"/> of silence.</summary>
    public const int CallBeats = 8, CallLeadBeats = 2;

    private static readonly ShowSection[] Form =
    {
        ShowSection.Calm, ShowSection.Groove, ShowSection.Chorus, ShowSection.Groove, ShowSection.Peak, ShowSection.Chorus,
    };

    /// <summary>Bars in a set of <paramref name="songs"/> songs.</summary>
    public static int SetBars(int songs) => songs * SongBars + CallBars + BisBars + RestBars;

    /// <summary>The moment at <paramref name="beat"/> (beats since the grid's origin) in sets of <paramref name="songs"/> songs.</summary>
    public static JazzMoment At(double beat, int songs)
    {
        songs = Math.Max(1, songs);
        double setBeats = SetBars(songs) * 4.0;
        long set = (long)Math.Floor(beat / setBeats);
        double inSet = beat - set * setBeats;
        double songsBeats = songs * SongBars * 4.0;
        if (inSet < songsBeats)
        {
            int song = (int)(inSet / (SongBars * 4.0));
            double inSong = inSet - song * SongBars * 4.0;
            int bar = (int)(inSong / 4.0);
            if (bar >= SongBars - 1) return new(set, JazzPart.Between, song, ShowSection.Blackout, 0, false, inSong - (SongBars - 1) * 4.0);
            int k = bar / SectionBars;
            return new(set, JazzPart.Song, song, Form[k], bar % SectionBars, k == Form.Length - 1, inSong);
        }
        double rest = inSet - songsBeats;
        if (rest < CallBars * 4.0) return new(set, JazzPart.Call, songs - 1, ShowSection.Blackout, 0, false, rest);
        rest -= CallBars * 4.0;
        if (rest < BisBars * 4.0)
        {
            int bar = (int)(rest / 4.0);
            bool second = bar >= BisBars / 2;
            return new(set, JazzPart.Bis, songs, second ? ShowSection.Peak : ShowSection.Chorus, bar % (BisBars / 2), second, rest);
        }
        return new(set, JazzPart.Rest, songs, ShowSection.Blackout, 0, false, rest - BisBars * 4.0);
    }

    /// <summary>The beat count at a moment of the show: the desk's own, <c>seconds x bpm / 60</c>.</summary>
    public static double BeatOf(double showSeconds, float bpm) => showSeconds * bpm / 60.0;

    /// <summary>Seconds of the show at which beat <paramref name="beat"/> falls.</summary>
    public static double SecondsOf(double beat, float bpm) => beat * 60.0 / bpm;

    /// <summary>The beat count at which a call begins: set <paramref name="set"/> of <paramref name="songs"/> songs.</summary>
    public static double CallStart(long set, int songs) => (set * SetBars(songs) + songs * SongBars) * 4.0;

    /// <summary>The beat count at which a song ends (its dark bar begins): song <paramref name="song"/> of set <paramref name="set"/>.</summary>
    public static double SongEnd(long set, int song, int songs) => (set * SetBars(songs) + (song + 1) * SongBars - 1) * 4.0;
}
