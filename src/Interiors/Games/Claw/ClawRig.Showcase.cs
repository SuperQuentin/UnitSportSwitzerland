using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Claw;

// A claw machine in the model viewer (--models) with a bot playing it again and again, so the gantry runs, the claw
// drops and closes and the plush is won; the in-game check (--clawcheck) drives the same.
public partial class ClawRig
{
    [Showcase("Venues", "Claw machine")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseMachine()
    {
        yield return ("A bot playing", () => Machine(out _));
    }

    /// <summary>A claw machine piece alone in a room of its own, its rig played by a bot.</summary>
    public static InteriorNode Machine(out BotDriver driver)
    {
        var layout = new InteriorLayout { StoreyHeight = 3f, Key = "claw-showcase" };
        layout.Furniture.Add(new FurniturePlan { Type = FurnitureType.ClawMachine, W = 1.0f, D = 1.0f, H = 2.1f });
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
        var rig = (ClawRig)node.TableRigOf(0)!;
        driver = new BotDriver { Rig = rig };
        node.AddChild(driver);
        return node;
    }

    /// <summary>A bot at the machine with no server: the game stepped here at the server's rate, its moves asked as the server would.</summary>
    public sealed partial class BotDriver : Node
    {
        public ClawRig Rig = null!;
        public ClawGame Game = null!;
        public double Clock = 1;
        public int Goes;
        private double _acc, _bots;
        private readonly Random _rng = new(1326);

        public override void _Ready()
        {
            Game = new ClawGame(Rig.Piece.W, Rig.Piece.D, 1326);
            Game.Sit(TableGame.BotBase, 0);
            Rig.OnState(Game, Clock);
        }

        public override void _Process(double delta) => Advance(delta);

        public void Advance(double delta)
        {
            Clock += delta;
            _acc += delta;
            while (_acc >= TableGames.LiveStep)
            {
                _acc -= TableGames.LiveStep;
                if ((_bots -= TableGames.LiveStep) <= 0)
                {
                    _bots = 0.1;
                    if (Game.Bot(0, Clock, _rng) is { } move && Game.Act(0, move.Action, move.Args, Clock, _rng) == null)
                    {
                        if (move.Action == ClawGame.ActCoin) Goes++;
                        Rig.OnState(Game, Clock);
                    }
                }
                if (Game.Step(TableGames.LiveStep, Clock)) Rig.OnState(Game, Clock);
            }
            Rig.Show(Game, Clock, delta);
        }
    }
}
