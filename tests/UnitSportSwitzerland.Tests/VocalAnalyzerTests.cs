using UnitSport.Audio.Cd;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The singer's analysis (src/Audio/Cd/VocalAnalyzer.cs, linked in, #1153) and the <c>/cd</c> command.</summary>
public class VocalAnalyzerTests
{
    private const int Rate = 22050;

    /// <summary>A sung-like tone: a fundamental and two harmonics, gliding from one MIDI note to another, then silence.</summary>
    private static float[] Glide(float fromNote, float toNote, float seconds, float silence = 1f)
    {
        int n = (int)(seconds * Rate), quiet = (int)(silence * Rate);
        var x = new float[n + quiet];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float note = fromNote + (toNote - fromNote) * i / n;
            double hz = 440.0 * Math.Pow(2, (note - 69) / 12.0);
            phase += 2 * Math.PI * hz / Rate;
            x[i] = (float)(0.3 * Math.Sin(phase) + 0.15 * Math.Sin(2 * phase) + 0.08 * Math.Sin(3 * phase));
        }
        return x;
    }

    private static int PitchAt(VocalAnalyzer.Result r, float t) => r.Frames[4 * (int)(t * VocalAnalyzer.FrameRate) + 3];

    [Fact]
    public void A_steady_note_is_heard_at_its_pitch_and_held()
    {
        var r = VocalAnalyzer.Analyse(Glide(57, 57, 3f), Rate, 120f);   // A3, 220 Hz
        Assert.InRange(PitchAt(r, 1.5f), 56, 58);
        Assert.Equal(0, PitchAt(r, 3.5f));                               // the silence after has no pitch
        Assert.True(r.Holds.Length >= 2 && r.Holds[0] < 0.5f && r.Holds[1] > 2.5f);
        Assert.Empty(r.Rises);
        Assert.Single(r.VocalIns);
    }

    [Fact]
    public void A_climb_of_an_octave_is_a_rise_and_a_descent_a_fall()
    {
        var up = VocalAnalyzer.Analyse(Glide(55, 67, 2f), Rate, 120f);
        Assert.True(up.Rises.Length >= 2 && up.Rises[1] - up.Rises[0] > 1f);
        Assert.Empty(up.Falls);
        var down = VocalAnalyzer.Analyse(Glide(67, 55, 2f), Rate, 120f);
        Assert.True(down.Falls.Length >= 2);
        Assert.Empty(down.Rises);
    }

    [Fact]
    public void Melody_runs_need_four_semitones_and_half_a_second()
    {
        float dt = 1f / VocalAnalyzer.FrameRate;
        // three semitones up over a second: no run; then five more quickly but in 0.25 s: too short
        var small = Enumerable.Range(0, 20).Select(i => 60f + 3f * i / 19f).ToArray();
        VocalAnalyzer.Melody(small, out var rises, out _, out _);
        Assert.Empty(rises);
        var quick = Enumerable.Range(0, 5).Select(i => 60f + 5f * i / 4f).ToArray();
        VocalAnalyzer.Melody(quick, out rises, out _, out _);
        Assert.Empty(rises);
        Assert.True(dt > 0f);
    }

    [Fact]
    public void The_voice_comes_back_after_two_bars()
    {
        // at 120 bpm two bars are 4 s: a 2 s gap is one entry, a 5 s gap two
        var level = new float[20 * 12];
        for (int i = 0; i < 20; i++) level[i] = 1f;            // 0..1 s
        for (int i = 60; i < 80; i++) level[i] = 1f;           // 3..4 s (gap 2 s)
        for (int i = 180; i < 200; i++) level[i] = 1f;         // 9..10 s (gap 5 s)
        var ins = VocalAnalyzer.Entries(level, 120f);
        Assert.Equal(new[] { 0f, 9f }, ins.Select(t => MathF.Round(t, 2)).ToArray());
    }

    [Theory]
    [InlineData("", CdCommand.Op.Report, 0)]
    [InlineData("analysis", CdCommand.Op.Report, 0)]
    [InlineData("reanalyse", CdCommand.Op.Stale, 0)]
    [InlineData("reanalyze all", CdCommand.Op.All, 0)]
    [InlineData("reanalyse 12", CdCommand.Op.One, 12)]
    [InlineData("reanalyse -3", CdCommand.Op.One, -3)]
    public void Cd_command_parses(string text, CdCommand.Op op, int id)
    {
        Assert.True(CdCommand.TryParse(text, out var got, out int gotId, out _));
        Assert.Equal(op, got);
        Assert.Equal(id, gotId);
    }

    [Theory]
    [InlineData("reanalyse twelve")]
    [InlineData("reanalyse all now")]
    [InlineData("burn")]
    public void Cd_command_refuses_nonsense(string text) =>
        Assert.False(CdCommand.TryParse(text, out _, out _, out _));

    [Fact]
    public void Cd_command_reports_versions_and_sources()
    {
        string s = CdCommand.Describe(new[] { (3, "Kim_Vocal_2"), (3, "centre"), (2, ""), (0, "") }, 3, busy: true, queued: 2);
        Assert.Contains("4 CD(s)", s);
        Assert.Contains("v3 x2 (Kim_Vocal_2 x1, centre x1)", s);
        Assert.Contains("v2 x1", s);
        Assert.Contains("not analysed x1", s);
        Assert.Contains("1 analysing, 2 queued", s);
    }
}
