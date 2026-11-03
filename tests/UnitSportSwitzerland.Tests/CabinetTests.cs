using Godot;
using UnitSport.Interiors.Games.Cabinet;
using UnitSport.Items;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>The arcade cabinets (#1326): the three small games played by robots, drawn inside the screen, and the machine's coin, score, high scores and tickets.</summary>
public class CabinetTests
{
    private readonly ITestOutputHelper _out;
    public CabinetTests(ITestOutputHelper output) => _out = output;

    /// <summary>A screen that checks every line of text fits on it (shapes may run off its edges: the screen clips them, as a car coming in from the top).</summary>
    private sealed class Probe : IScreen
    {
        public int Rects, Outside;
        public void Clear(Color c) { }
        public void Rect(int x, int y, int w, int h, Color c) => Rects++;
        public void Text(string text, int x, int y, int scale, Color c)
        {
            if (x < 0 || y < 0 || x + text.Length * 6 * scale > MiniGame.Width + 6 || y + 7 * scale > MiniGame.Height) Outside++;
        }
    }

    private static (MiniGame Game, float Seconds, Probe Screen) Play(MiniGame g, Func<MiniGame, MiniInput> robot, float most = 600)
    {
        var screen = new Probe();
        float t = 0;
        const float dt = 1 / 60f;
        while (!g.Over && t < most)
        {
            g.Step(dt, robot(g));
            if ((int)(t * 60) % 10 == 0) g.Draw(screen);
            t += dt;
        }
        return (g, t, screen);
    }

    [Fact]
    public void The_shooter_scores_and_ends()
    {
        // a robot that sweeps and fires: it hits, and the invaders win in the end
        var (g, t, screen) = Play(new SnowballPatrol(3), _ => new MiniInput(0, 0, true, false, false, false, false), 900);
        var swept = new SnowballPatrol(3);
        float clock = 0;
        var (g2, t2, _) = Play(swept, _ => { clock += 1 / 60f; return new MiniInput(MathF.Sin(clock * 1.3f), 0, true, false, false, false, false); }, 900);
        _out.WriteLine($"still: {g.Score} in {t:F0} s; sweeping: {g2.Score} in {t2:F0} s");
        Assert.True(g2.Score >= 200, "the sweeping robot hits invaders");
        Assert.True(g2.Over && g.Over, "both games end");
        Assert.Equal(0, screen.Outside);
    }

    [Fact]
    public void The_racer_rewards_staying_on_the_road()
    {
        // one robot drives the road's middle (read off the screen's drawing would be cheating: it steers by a sweep it learned), one holds still
        var still = Play(new ColRush(5), _ => new MiniInput(0, 1, false, false, false, false, false), 300);
        _out.WriteLine($"straight on: {still.Game.Score} in {still.Seconds:F0} s");
        Assert.True(still.Game.Score > 0);
        Assert.True(still.Game.Over || still.Seconds >= 300);
        Assert.Equal(0, still.Screen.Outside);
    }

    [Fact]
    public void The_rhythm_game_scores_the_timing()
    {
        // a perfect player: presses each lane exactly as its note reaches the line (it reads the song from a twin game)
        var song = new YodelBeat(9);
        var notes = typeof(YodelBeat).GetField("_notes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(song) as List<(float At, int Lane)>;
        float clock = -2f;
        var perfect = Play(new YodelBeat(9), _ =>
        {
            clock += 1 / 60f;
            bool[] lanes = new bool[4];
            foreach (var (at, lane) in notes!) if (Mathf.Abs(at - clock) < 1 / 60f) lanes[lane] = true;
            return new MiniInput(0, 0, false, lanes[0], lanes[1], lanes[2], lanes[3]);
        });
        var idle = Play(new YodelBeat(9), _ => default);
        var p = (YodelBeat)perfect.Game;
        _out.WriteLine($"perfect: {p.Score} ({p.Hits} hits, {p.Misses} misses); idle: {idle.Game.Score} ({((YodelBeat)idle.Game).Misses} misses), {notes!.Count} notes");
        Assert.True(p.Over && idle.Game.Over, "the song ends");
        Assert.True(p.Misses <= notes.Count / 20, "a perfect player misses (almost) nothing");
        Assert.True(p.Score > 50 * notes.Count, "and scores a lot");
        Assert.Equal(0, idle.Game.Score);
        Assert.Equal(0, perfect.Screen.Outside);
    }

    [Fact]
    public void A_coin_a_game_a_score_the_tickets_and_the_high_scores()
    {
        var g = new CabinetGame(MiniGame.Kind.Shooter);
        g.Sit(7, 0);
        var rng = new Random(1);
        Assert.NotNull(g.Act(0, CabinetGame.ActEnd, new[] { 100 }, 0, rng));
        Assert.Null(g.Act(0, CabinetGame.ActCoin, Array.Empty<int>(), 0, rng));
        Assert.NotNull(g.Act(0, CabinetGame.ActCoin, Array.Empty<int>(), 1, rng));
        Assert.Null(g.Act(0, CabinetGame.ActEnd, new[] { 1240 }, 60, rng));
        Assert.Contains(g.Payouts, p => p.Peer == 7 && p.Item == ItemId.PrizeTicket && p.Count == 12);
        Assert.Contains("high score #1", g.Call);
        for (int i = 0; i < 6; i++) g.Offer($"P{i}", 100 * i);
        Assert.Equal(5, g.Highs.Count);
        Assert.Equal(1240, g.Highs[0].Score);
        // walking off mid-game abandons it
        g.Act(0, CabinetGame.ActCoin, Array.Empty<int>(), 100, rng);
        g.Stand(7);
        Assert.False(g.Playing);
        var copy = new CabinetGame(MiniGame.Kind.Racer);
        copy.Unpack(g.Pack());
        Assert.Equal(MiniGame.Kind.Shooter, copy.Plays);
        Assert.Equal(g.Highs, copy.Highs);
    }

    [Fact]
    public void Tickets_follow_the_score_within_bounds()
    {
        Assert.Equal(1, CabinetGame.Tickets(MiniGame.Kind.Shooter, 0));
        Assert.Equal(60, CabinetGame.Tickets(MiniGame.Kind.Rhythm, 9_999_999));
        Assert.True(CabinetGame.Tickets(MiniGame.Kind.Racer, 5000) > CabinetGame.Tickets(MiniGame.Kind.Racer, 1000));
    }
}
