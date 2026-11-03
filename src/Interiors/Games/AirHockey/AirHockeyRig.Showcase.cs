using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.AirHockey;

// An air hockey table in the model viewer (--models) with two bots playing on it,
// so the mallets chase and the puck flies; the in-game check (--airhockeycheck) drives the same.
public partial class AirHockeyRig
{
    [Showcase("Venues", "Air hockey")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseTable()
    {
        yield return ("Two bots", () => Table(1.2f, 2.1f, out _));
    }

    /// <summary>An air hockey piece alone in a room of its own, its rig playing a bot game.</summary>
    public static InteriorNode Table(float w, float d, out BotDriver driver)
    {
        var layout = new InteriorLayout { StoreyHeight = 3f, Key = "airhockey-showcase" };
        layout.Furniture.Add(new FurniturePlan { Type = FurnitureType.AirHockey, W = w, D = d, H = 0.9f });
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
        var rig = (AirHockeyRig)node.TableRigOf(0)!;
        driver = new BotDriver { Rig = rig };
        node.AddChild(driver);
        return node;
    }

    /// <summary>Two bots at a table with no server: the game stepped here at the server's rate, its bots asked as the server asks them.</summary>
    public sealed partial class BotDriver : Node
    {
        public AirHockeyRig Rig = null!;
        public AirHockeyGame Game = null!;
        public double Clock = 1;
        private double _acc, _bots;
        private readonly Random _rng = new(1324);

        public override void _Ready()
        {
            Game = new AirHockeyGame();
            Game.Sit(TableGame.BotBase, 0);
            Game.Sit(TableGame.BotBase - 1, 1);
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
                    for (int s = 0; s < 2; s++)
                        if (Game.Bot(s, Clock, _rng) is { } move) Game.Act(s, move.Action, move.Args, Clock, _rng);
                }
                if (Game.Step(TableGames.LiveStep, Clock)) Rig.OnState(Game, Clock);
            }
            Rig.Show(Game, Clock, delta);
        }
    }
}
