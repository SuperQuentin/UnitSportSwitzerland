using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Darts;

// A dartboard in the model viewer (--models) with two bots playing 501 on it, so the darts fly, stick and are pulled;
// the in-game check (--dartscheck) drives the same.
public partial class DartsRig
{
    [Showcase("Venues", "Darts")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseBoard()
    {
        yield return ("501, two bots", () => Board(out _));
    }

    /// <summary>A dartboard piece alone in a room of its own, its rig playing a bot game.</summary>
    public static InteriorNode Board(out BotDriver driver)
    {
        var layout = new InteriorLayout { StoreyHeight = 3f, Key = "darts-showcase" };
        layout.Furniture.Add(new FurniturePlan { Type = FurnitureType.DartBoard, W = 0.7f, D = 0.15f, H = 2.0f });
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
        var rig = (DartsRig)node.TableRigOf(0)!;
        driver = new BotDriver { Rig = rig };
        node.AddChild(driver);
        return node;
    }

    /// <summary>Two bots at a board with no server: the game run here, its states handed to the rig as they would arrive.</summary>
    public sealed partial class BotDriver : Node
    {
        public DartsRig Rig = null!;
        public DartsGame Game = null!;
        public double Clock = 1;
        public int Thrown;
        private readonly Random _rng = new(1324);

        public override void _Ready()
        {
            Game = new DartsGame();
            Game.Sit(TableGame.BotBase, 0);
            Game.Sit(TableGame.BotBase - 1, 1);
            Rig.OnState(Game, Clock);
        }

        public override void _Process(double delta) => Advance(delta);

        public void Advance(double delta)
        {
            Clock += delta;
            int up = Game.Winner >= 0 ? 0 : Game.Up;
            if (Game.Bot(up, Clock, _rng) is { } move && Game.Act(up, move.Action, move.Args, Clock, _rng) == null)
            {
                Thrown++;
                Rig.OnState(Game, Clock);
            }
            Rig.Show(Game, Clock, delta);
        }
    }
}
