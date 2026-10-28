using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Pinball;

// The four tables in the model viewer (--models): each cabinet with a robot playing it, so the
// flippers, balls, lamps, drop targets, spinners and the backbox's display all move.
public partial class PinballCabinet
{
    [Showcase("Venues", "Pinball")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseTables()
    {
        for (int i = 0; i < PinballLayouts.Count; i++)
        {
            int table = i;
            yield return ($"{PinballLayouts.Get(i).Title}, a robot playing", () =>
            {
                var c = Create(table, Styles.StyleKit.Material(Styles.MaterialRole.Interior));
                c.AddChild(new RobotDriver { Cabinet = c });
                return c;
            });
        }
    }

    [Showcase("Venues", "Pinball attract")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseAttract()
    {
        for (int i = 0; i < PinballLayouts.Count; i++)
        {
            int table = i;
            yield return ($"{PinballLayouts.Get(i).Title}, high scores", () =>
            {
                var c = Create(table, Styles.StyleKit.Material(Styles.MaterialRole.Interior));
                c.AddChild(new AttractDriver { Cabinet = c });
                return c;
            });
        }
    }

    /// <summary>A machine nobody plays, with a high-score table: the backbox's attract cycle, starting on the scores.</summary>
    private sealed partial class AttractDriver : Node
    {
        public PinballCabinet Cabinet = null!;
        private double _clock = 6.0;
        private PinballHighScores _highs = null!;

        public override void _Ready()
        {
            _highs = new PinballHighScores();
            foreach (var (who, score) in new[] { ("Nicolas", 1_284_310L), ("Quentin", 977_420L), ("Anna", 412_060L), ("Beat", 98_770L), ("Chantal", 47_300L) })
                _highs.Offer(who, score);
        }

        public override void _Process(double delta)
        {
            _clock += delta;
            Cabinet.ShowScreen(PinballScreen.Idle(Cabinet.Table, new long[] { 412_060, 98_770 }), _clock, _highs);
        }
    }

    /// <summary>
    /// Plays the cabinet's table on its own, flipping at balls that come down near a flipper: the viewer's
    /// demo, and the in-game check's player (<c>--pinballcheck</c>).
    /// </summary>
    public sealed partial class RobotDriver : Node
    {
        public PinballCabinet Cabinet = null!;
        public PinballGame Game { get; private set; } = null!;
        private double _acc, _clock;
        private int _ms;
        private readonly Vector3[] _poses = new Vector3[MaxBalls];

        public override void _Ready()
        {
            Game = new PinballGame(Cabinet.Table, 1325);
            Game.Start();
        }

        public override void _Process(double delta)
        {
            _clock += delta;
            _acc += Math.Min(delta, 0.1);
            int steps = (int)(_acc / PinballSolver.Dt);
            _acc -= steps * PinballSolver.Dt;
            for (int i = 0; i < steps; i++)
            {
                Play(Game, _ms++);
                Game.Run(1);
            }
            Game.Outbox.Clear();
            if (Game.Rules.State == PinballRules.Phase.GameOver && Game.Time > 1 && _ms % 8000 == 0) Game.Start();
            Cabinet.Show(Game, _clock, _poses);
        }

        /// <summary>The robot: a flipper up when a ball rolls down near it; the plunger pulled and let go.</summary>
        public static void Play(PinballGame g, int ms)
        {
            bool left = false, right = false;
            foreach (var b in g.Solver.Balls)
                if (b.Ramp < 0 && b.Held < 0 && b.P.Y < 0.2f && b.V.Y < -0.05f)
                {
                    if (b.P.X < PinballLayouts.Xc) left = true;
                    else right = true;
                }
            g.Button(0, left);
            g.Button(1, right);
            if (g.Solver.BallOnPlunger()) g.Solver.Pull = ms % 1500 < 600 ? 0.7f : 0f;
            if (g.Rules.State == PinballRules.Phase.GameOver && ms % 6000 == 0) g.Start();
        }
    }

    /// <summary>Everything a running game shows on this cabinet: balls, flippers, drops, spinners, plunger, lamps, backbox.</summary>
    public void Show(PinballGame g, double now, Vector3[] scratch)
    {
        int n = Math.Min(g.Solver.Balls.Count, MaxBalls);
        for (int i = 0; i < n; i++) scratch[i] = g.Solver.Balls[i].Pos3;
        ShowPoses(scratch.AsSpan(0, n), g.Solver.Angle);
        ShowDrops(w => g.Solver.Down[w]);
        ShowSpinAndPlunger(s => g.Solver.Spin[s], g.Solver.Pull);
        if (_rulesShown != g.Rules.Version || now - _screenAt > 0.1)
        {
            _rulesShown = g.Rules.Version;
            _screenAt = now;
            ShowScreen(PinballScreen.Of(g.Rules), now);
        }
    }

    private int _rulesShown = -1;
    private double _screenAt;
}
