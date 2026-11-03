using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Pool;

// A pool table in the model viewer (--models) with two bots playing eight-ball on it, so the balls roll, turn,
// drop and the cue strokes; the in-game check (--poolcheck) drives the same.
public partial class PoolRig
{
    [Showcase("Venues", "Pool")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseTable()
    {
        yield return ("Eight-ball, two bots", () => Table(1.4f, 2.7f, out _));
    }

    /// <summary>A pool table piece alone in a room of its own, its rig playing a bot game.</summary>
    public static InteriorNode Table(float w, float d, out BotDriver driver)
    {
        var layout = new InteriorLayout { StoreyHeight = 3f, Key = "pool-showcase" };
        layout.Furniture.Add(new FurniturePlan { Type = FurnitureType.PoolTable, W = w, D = d, H = 0.8f });
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
        var rig = (PoolRig)node.TableRigOf(0)!;
        driver = new BotDriver { Rig = rig };
        node.AddChild(driver);
        return node;
    }

    /// <summary>Two bots at a table with no server: the game run here, its states handed to the rig as they would arrive.</summary>
    public sealed partial class BotDriver : Node
    {
        public PoolRig Rig = null!;
        public PoolGame Game = null!;
        public double Clock = 1;
        public int Shots;
        private readonly Random _rng = new(1324);

        public override void _Ready()
        {
            Game = new PoolGame(Rig.Cloth.Hx, Rig.Cloth.Hz, 7);
            Game.Sit(TableGames.BotBase, 0);
            Game.Sit(TableGames.BotBase - 1, 1);
            Rig.OnState(Game, Clock);
        }

        public override void _Process(double delta) => Advance(delta);

        public void Advance(double delta)
        {
            Clock += delta;
            if (Game.Bot(Game.Up, Clock, _rng) is { } move && Game.Act(Game.Up, move.Action, move.Args, Clock, _rng) == null)
            {
                if (move.Action == PoolGame.ActShoot) Shots++;
                Rig.OnState(Game, Clock);
            }
            Rig.Show(Game, Clock, delta);
        }
    }
}
