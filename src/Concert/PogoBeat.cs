using System;

namespace UnitSport.Concert;

/// <summary>How a press landed on the beat (#1150).</summary>
public enum PogoGrade : byte { None, Perfect, Good, Miss }

/// <summary>
/// The concert pogo's rhythm judge (#1150): a jump pressed on the song's beat as heard is Perfect
/// or Good and keeps the dancer pogoing, a press off the beat (or twice on one beat) is a Miss, and
/// a beat let go by while pogoing breaks the combo; two in a row end the pogo. Beats in, as a beat
/// count (<c>beat + phase</c>) and the beat's length in seconds, so the windows are in time, not in
/// beats. Pure C# (tier 0: <c>PogoBeatTests</c>).
/// </summary>
public sealed class PogoBeat
{
    /// <summary>Half windows round the beat, s.</summary>
    public const double PerfectSeconds = 0.07, GoodSeconds = 0.14;
    /// <summary>At a fast tempo the Good window never takes more than this share of the beat on each side.</summary>
    public const double MaxBeatShare = 0.4;
    /// <summary>Beats let go by in a row that end the pogo.</summary>
    public const int MissesToStop = 2;

    public int Combo { get; private set; }
    public int Best { get; private set; }
    public bool Pogoing { get; private set; }
    /// <summary>The last grade and the beat it was given on, for the screen.</summary>
    public PogoGrade Last { get; private set; }
    public int LastBeat { get; private set; } = int.MinValue;

    private int _hit = int.MinValue, _checked = int.MinValue, _missed;

    /// <summary>The Good half window for this tempo, s.</summary>
    public static double Window(double secondsPerBeat) => Math.Min(GoodSeconds, secondsPerBeat * MaxBeatShare);

    /// <summary>A jump pressed at <paramref name="beats"/>.</summary>
    public PogoGrade Press(double beats, double secondsPerBeat)
    {
        int n = (int)Math.Round(beats);
        double off = Math.Abs(beats - n) * secondsPerBeat;
        var grade = n == _hit || off > Window(secondsPerBeat) ? PogoGrade.Miss
            : off <= Math.Min(PerfectSeconds, Window(secondsPerBeat) * 0.5) ? PogoGrade.Perfect : PogoGrade.Good;
        Last = grade;
        LastBeat = n;
        if (grade == PogoGrade.Miss)
        {
            Combo = 0;
            return grade;
        }
        _hit = n;
        _missed = 0;
        Pogoing = true;
        Best = Math.Max(Best, ++Combo);
        return grade;
    }

    /// <summary>
    /// Every frame: the beats whose window closed by <paramref name="beats"/> with no hit count as
    /// let go by while pogoing. True when that ended the pogo.
    /// </summary>
    public bool Tick(double beats, double secondsPerBeat)
    {
        int closed = (int)Math.Floor(beats - Window(secondsPerBeat) / secondsPerBeat);
        if (_checked == int.MinValue || closed < _checked || closed - _checked > 8) _checked = closed;   // a start, a seek, a new song
        bool stopped = false;
        for (; _checked < closed; )
        {
            int n = ++_checked;
            if (!Pogoing || n == _hit) continue;
            Combo = 0;
            Last = PogoGrade.Miss;
            LastBeat = n;
            if (++_missed >= MissesToStop) { Pogoing = false; stopped = true; }
        }
        return stopped;
    }

    /// <summary>Off the floor, or a new show: everything but the best combo forgotten.</summary>
    public void Reset()
    {
        Combo = _missed = 0;
        Pogoing = false;
        Last = PogoGrade.None;
        _hit = _checked = LastBeat = int.MinValue;
    }
}
