using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Cabinet;

// The arcade's machines in the model viewer (--models), each with a robot playing its small game on the screen; the
// in-game check (--cabinetcheck) drives the same.
public partial class CabinetRig
{
    [Showcase("Venues", "Arcade games")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseMachines()
    {
        yield return ("Upright: Snowball Patrol", () => Machine(FurnitureType.ArcadeCabinet, 0.78f, 0.9f, 1.85f, MiniGame.Kind.Shooter, out _));
        yield return ("Upright: Col Rush", () => Machine(FurnitureType.ArcadeCabinet, 0.78f, 0.9f, 1.85f, MiniGame.Kind.Racer, out _));
        yield return ("Racer twin: Col Rush", () => Machine(FurnitureType.Racer, 1.6f, 2.3f, 2.0f, MiniGame.Kind.Racer, out _));
        yield return ("Dance machine: Yodel Beat", () => Machine(FurnitureType.DanceMachine, 2.0f, 2.4f, 2.4f, MiniGame.Kind.Rhythm, out _));
        yield return ("Drums: Yodel Beat", () => Machine(FurnitureType.DrumGame, 1.2f, 1.4f, 1.9f, MiniGame.Kind.Rhythm, out _));
    }

    /// <summary>A machine alone in a room of its own, a robot playing its game on the screen.</summary>
    public static InteriorNode Machine(FurnitureType type, float w, float d, float h, MiniGame.Kind kind, out Robot robot)
    {
        var layout = new InteriorLayout { StoreyHeight = 3f, Key = "cabinet-showcase" };
        layout.Furniture.Add(new FurniturePlan { Type = type, W = w, D = d, H = h });
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
        var rig = (CabinetRig)node.TableRigOf(0)!;
        robot = new Robot { Rig = rig, Kind = kind };
        node.AddChild(robot);
        return node;
    }

    /// <summary>A robot at the controls: the game run as a player's client runs it, with simple hands, again when it ends.</summary>
    public sealed partial class Robot : Node
    {
        public CabinetRig Rig = null!;
        public MiniGame.Kind Kind;
        public MiniGame Game = null!;
        public int Games;
        private float _t;
        private int _seed = 1326;

        public override void _Ready() => Start();

        private void Start()
        {
            Game = MiniGame.Make(Kind, _seed++);
            Games++;
            Rig.Live = true;
        }

        public override void _Process(double delta) => Advance((float)delta);

        public void Advance(float dt)
        {
            _t += dt;
            // a sweep and a finger on the button; the four lanes in turn for the rhythm game
            int lane = (int)(_t * 4) % 4;
            var input = new MiniInput(Mathf.Sin(_t * 1.3f), 0.8f, true, lane == 0, lane == 1, lane == 2, lane == 3);
            Game.Step(Math.Min(dt, 0.1f), input);
            Rig.DrawGame(Game);
            if (Game.Over) Start();
        }
    }
}
