using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --headless --path . -- --bowlingrollcheck --systems physics</c> (#1323): known balls rolled
/// down a full lane (<see cref="BowlingBall"/>) into the pins (<see cref="PinDeck"/>), every deck in its
/// own physics space at once, and what falls checked against what a bowler expects: a pocket ball
/// strikes often, a straight head-on ball leaves pins, a ball down an edge takes the corner
/// pin, a spare is picked up, a gutter ball knocks nothing. No world, no building.
/// </summary>
public partial class BowlingRollProbe : Node
{
    public static bool ParseArgs() => CmdArgs.All.Contains("--bowlingrollcheck");

    private sealed record Case(string Name, BowlingBall.Release Release, ushort Standing, Func<int, ushort, bool> Expect, string Want)
    {
        public BowlingBall.Run? Run;
        public PinDeck? Deck;
        public int Knocked;
        public ushort Left;
    }

    private const ushort Rack = 0x3FF;
    private readonly List<Case> _cases = new();
    private bool _ok = true;

    private static bool Down(ushort left, int pin) => (left & (1 << (pin - 1))) == 0;

    public override void _Ready()
    {
        var lane = BowlingSizes.Run.Full;
        // the pocket for a right-hander: between the 1 and 3 pins, found by moving the release across
        // until the run ends there, for a few speeds and revs
        foreach (var (speed, revs, axis) in new[] { (8f, 30f, 0.5f), (7.5f, 34f, 0.6f), (8.5f, 26f, 0.45f), (7f, 30f, 0.55f), (9f, 32f, 0.5f) })
        {
            var r = Pocket(lane, speed, revs, axis);
            _cases.Add(new($"pocket {speed} m/s {revs} rad/s", r, Rack, (k, _) => k >= 7, ">= 7 pins"));
        }
        _cases.Add(new("straight at the head pin", new(0, 0, 8f, 30f, 0), Rack, (k, _) => k >= 3, ">= 3 pins"));
        _cases.Add(new("down the right edge", new(-0.47f, 0, 7.5f, 25f, 0), Rack, (k, left) => Down(left, 10) && k <= 3, "the 10 pin, few others"));
        _cases.Add(new("down the left edge", new(0.47f, 0, 7.5f, 25f, 0), Rack, (k, left) => Down(left, 7) && k <= 3, "the 7 pin, few others"));
        _cases.Add(new("the 10-pin spare", new(-0.47f, 0, 7.5f, 25f, 0), 1 << 9, (k, _) => k == 1, "the 10 pin"));
        _cases.Add(new("into the gutter", new(0.45f, 0.03f, 7f, 20f, 0), Rack, (k, _) => k == 0, "nothing"));

        foreach (var c in _cases)
        {
            c.Run = BowlingBall.Roll(lane, c.Release);
            if (!c.Run.Gutter) c.Deck = new PinDeck(lane, c.Standing, c.Run.AtDeck);
        }
    }

    /// <summary>A right-hander's release whose run reaches the deck in the 1-3 pocket.</summary>
    private static BowlingBall.Release Pocket(BowlingSizes.Run lane, float speed, float revs, float axis)
    {
        const float target = -0.065f;
        float lo = -0.45f, hi = 0.2f;
        for (int i = 0; i < 30; i++)
        {
            float mid = (lo + hi) / 2;
            var run = BowlingBall.Roll(lane, new BowlingBall.Release(mid, -0.012f, speed, revs, axis));
            if (run.Gutter || run.AtDeck.Pos.X > target) hi = mid;
            else lo = mid;
        }
        return new BowlingBall.Release(lo, -0.012f, speed, revs, axis);
    }

    public override void _PhysicsProcess(double delta)
    {
        bool all = true;
        foreach (var c in _cases)
        {
            if (c.Deck is not { } deck) continue;
            deck.Tick((float)delta);
            all &= deck.Done;
        }
        if (!all) return;
        int strikes = 0, pockets = 0;
        foreach (var c in _cases)
        {
            if (c.Deck is { } deck)
            {
                c.Knocked = deck.Knocked();
                c.Left = deck.Standing();
                deck.Dispose();
            }
            else c.Left = c.Standing;
            bool pass = c.Expect(c.Knocked, c.Left);
            if (c.Name.StartsWith("pocket")) { pockets++; if (c.Knocked == 10) strikes++; }
            var standing = string.Join(",", Enumerable.Range(1, 10).Where(p => !Down(c.Left, p)));
            GD.Print($"[bowlingroll] {(pass ? "ok  " : "FAIL")} {c.Name}: {c.Knocked} down, left [{standing}] (want {c.Want})" +
                $"{(c.Run!.Gutter ? " gutter" : $" at x {c.Run.AtDeck.Pos.X:F3}, {c.Run.Time:F2} s")}");
            _ok &= pass;
        }
        GD.Print($"[bowlingroll] strikes from the pocket: {strikes}/{pockets}");
        // a good pocket ball strikes about every other time for a real bowler, and leaves a single corner pin otherwise
        bool enough = strikes * 3 >= pockets;
        GD.Print($"[bowlingroll] {(enough ? "ok  " : "FAIL")} at least one pocket ball in three strikes");
        _ok &= enough;
        GD.Print($"[bowlingroll] RESULT: {(_ok ? "ok" : "FAILED")}");
        SetPhysicsProcess(false);
        GetTree().Quit(_ok ? 0 : 1);
    }
}
