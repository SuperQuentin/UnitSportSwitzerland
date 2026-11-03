using Godot;
using UnitSport.Interiors.Games.Darts;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>Darts (#1324): the board's beds, 501 double out (busts, checkouts, turns), the bot's targets, the wire.</summary>
public class DartsTests
{
    private readonly ITestOutputHelper _out;
    public DartsTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(0f, 0f, "bull", 50)]
    [InlineData(0f, 12f, "25", 25)]
    [InlineData(0f, 103f, "T20", 60)]
    [InlineData(0f, 166f, "D20", 40)]
    [InlineData(0f, 60f, "20", 20)]
    [InlineData(0f, 180f, "miss", 0)]
    [InlineData(0f, -103f, "T3", 9)]
    [InlineData(103f, 0f, "T6", 18)]
    [InlineData(-166f, 0f, "D11", 22)]
    public void The_board_scores_its_beds(float x, float y, string name, int score)
    {
        var hit = DartBoard.Score(new Vector2(x, y));
        Assert.Equal(name, hit.Name);
        Assert.Equal(score, hit.Score);
    }

    [Fact]
    public void Every_bed_aimed_at_scores_itself()
    {
        foreach (int v in DartBoard.Order)
            for (int m = 1; m <= 3; m++)
            {
                var hit = DartBoard.Score(DartBoard.Aim(v, m));
                Assert.Equal((v, m), (hit.Value, hit.Multiplier));
            }
    }

    /// <summary>A two-player game with the darts landing exactly where aimed (bots: no hand spread).</summary>
    private static DartsGame Game()
    {
        var g = new DartsGame();
        g.Sit(DartsGame.BotBase, 0);
        g.Sit(DartsGame.BotBase - 1, 1);
        return g;
    }

    private static double _now = 10;

    private static string? Throw(DartsGame g, int seat, int value, int mult)
    {
        _now = Math.Max(_now, g.FreeAt) + 0.01;
        var at = DartBoard.Aim(value, mult);
        return g.Act(seat, DartsGame.ActThrow, new[] { (int)(at.X * 10), (int)(at.Y * 10) }, _now, new Random(1));
    }

    [Fact]
    public void Three_darts_a_visit_then_the_next_player()
    {
        var g = Game();
        Assert.Null(Throw(g, 0, 20, 3));
        Assert.Null(Throw(g, 0, 20, 3));
        Assert.Null(Throw(g, 0, 20, 3));
        Assert.Equal(321, g.Scores[0]);
        Assert.Contains("eighty", g.Call);
        Assert.Equal(1, g.Up);
        Assert.NotNull(Throw(g, 0, 20, 1));
    }

    [Fact]
    public void A_bust_goes_back_to_the_visit_s_start()
    {
        var g = Game();
        g.Scores[0] = 32;
        Assert.Null(Throw(g, 0, 20, 1));
        Assert.Equal(12, g.Scores[0]);
        Assert.Null(Throw(g, 0, 20, 1));
        Assert.Equal(32, g.Scores[0]);
        Assert.Contains("bust", g.Call);
        Assert.Equal(1, g.Up);
    }

    [Fact]
    public void Zero_off_a_single_is_bust_and_off_a_double_wins()
    {
        var g = Game();
        g.Scores[0] = 40;
        Assert.Null(Throw(g, 0, 20, 2));
        Assert.Equal(0, g.Winner);
        Assert.Contains("checks out on D20", g.Call);
        // a throw after the leg starts the next, the other first
        Assert.Null(Throw(g, 0, 20, 1));
        Assert.Equal(-1, g.Winner);
        Assert.Equal(501, g.Scores[0]);
        Assert.Equal(1, g.Up);

        var h = Game();
        h.Scores[0] = 20;
        Assert.Null(Throw(h, 0, 20, 1));
        Assert.Equal(20, h.Scores[0]);
        Assert.Equal(1, h.Up);
    }

    [Theory]
    [InlineData(170, "T20 T20 bull")]
    [InlineData(100, "T20 D20")]
    [InlineData(32, "D16")]
    [InlineData(50, "bull")]
    public void The_checkout_hint(int score, string expected) => Assert.Equal(expected, DartsGame.Checkout(score));

    [Fact]
    public void Bots_finish_a_leg_in_a_pub_s_number_of_darts()
    {
        var darts = new List<int>();
        for (int seed = 0; seed < 20; seed++)
        {
            var g = Game();
            var rng = new Random(seed);
            double now = 0;
            int thrown = 0;
            while (g.Winner < 0 && thrown < 400)
            {
                now = Math.Max(now, g.FreeAt + DartsGame.BotThink + 0.01);
                var move = g.Bot(g.Up, now, rng);
                Assert.NotNull(move);
                Assert.Null(g.Act(g.Up, move!.Value.Action, move.Value.Args, now, rng));
                thrown++;
            }
            Assert.True(g.Winner >= 0, $"seed {seed}: no winner in {thrown} darts");
            darts.Add(thrown);
        }
        _out.WriteLine($"darts a leg (both players): {string.Join(", ", darts)}");
        Assert.InRange(darts.Average(), 25, 120);
    }

    [Fact]
    public void The_state_packs_and_unpacks()
    {
        var g = Game();
        Throw(g, 0, 19, 3);
        Throw(g, 0, 7, 1);
        var copy = new DartsGame();
        copy.Unpack(g.Pack());
        Assert.Equal(g.Scores, copy.Scores);
        Assert.Equal(g.Call, copy.Call);
        Assert.Equal(g.ThrowSeq, copy.ThrowSeq);
        Assert.Equal(g.Board.Count, copy.Board.Count);
        Assert.Equal(g.Board[1].Hit, copy.Board[1].Hit);
    }
}
