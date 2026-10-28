using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

// The lane rig in the model viewer (--models): a pin, the house balls, and a lane pair whose
// pinsetters go round on their own, a pick after a first ball, then a reset after the second.
public partial class LaneRig
{
    [Showcase("Bowling")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseParts()
    {
        yield return ("Pin", () => Part(InteriorMeshBuilder.Pin()));
        yield return ("House balls", () =>
        {
            var root = new Node3D();
            for (int i = 0; i < InteriorMeshBuilder.BallColorCount; i++)
            {
                var ball = Part(InteriorMeshBuilder.Ball(i));
                ball.Position = new Vector3((i - InteriorMeshBuilder.BallColorCount / 2f) * 0.26f, LaneGeometry.BallRadius, 0);
                root.AddChild(ball);
            }
            return root;
        });
        yield return ("Pinsetter table", () => Part(InteriorMeshBuilder.PinTable(1f)));
        yield return ("Sweep", () => Part(InteriorMeshBuilder.Sweep()));
        yield return ("Lane pair, pinsetters cycling", () => Demo(false));
        yield return ("Mini lane pair, pinsetters cycling", () => Demo(true));
    }

    private static Node3D Part(InteriorMeshBuilder.MeshData data) =>
        new MeshInstance3D { Mesh = Mesh(data), MaterialOverride = Styles.StyleKit.Material(Styles.MaterialRole.Interior) };

    /// <summary>A lane pair with its rig, playing frames on its own clock.</summary>
    private static Node3D Demo(bool mini)
    {
        var plan = new FurniturePlan
        {
            Type = FurnitureType.LanePair, W = InteriorGenerator.PairPitch, H = 4f,
            D = mini ? InteriorGenerator.MiniLanes.Block - 0.7f : InteriorGenerator.FullLanes.Block - 0.7f,
        };
        var layout = new InteriorLayout { StoreyHeight = 4f };
        layout.Furniture.Add(plan);
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
        node.AddChild(new DemoDriver { Rig = node.LaneRigOf(0)! });
        return node;
    }

    /// <summary>Knocks pins over and runs the machine, again and again: what the viewer shows.</summary>
    private sealed partial class DemoDriver : Node
    {
        public LaneRig Rig = null!;
        private double _clock, _next;
        private int _step;
        private readonly RandomNumberGenerator _rng = new() { Seed = 1323 };

        public override void _Ready()
        {
            Rig.Clock = () => _clock;
            // --lanetime <s>: start that far into the show, for a still of one moment of the cycle
            var args = OS.GetCmdlineUserArgs();
            int at = Array.IndexOf(args, "--lanetime");
            if (at < 0 || at + 1 >= args.Length || !float.TryParse(args[at + 1], System.Globalization.CultureInfo.InvariantCulture, out float start)) return;
            while (_clock < start)
            {
                Tick(1 / 30.0);
                Rig._Process(1 / 30.0);
            }
        }

        public override void _Process(double delta) => Tick(delta);

        private void Tick(double delta)
        {
            _clock += delta;
            if (_clock < _next) return;
            for (int lane = 0; lane < 2; lane++)
            {
                if (_step % 2 == 0)
                {
                    // a first ball: knock most of them, a pick follows
                    Knock(lane, 0.7f);
                    Rig.StartCycle(lane, PinsetterCycle.Kind.Pick, _clock + 1.5);
                }
                else
                {
                    Knock(lane, 1f);
                    Rig.StartCycle(lane, PinsetterCycle.Kind.Reset, _clock + 1.5);
                }
            }
            _next = _clock + 1.5 + PinsetterCycle.Length(_step % 2 == 0 ? PinsetterCycle.Kind.Pick : PinsetterCycle.Kind.Reset) + 1.5;
            _step++;
        }

        private void Knock(int lane, float share)
        {
            for (int i = 0; i < 10; i++)
            {
                if (Rig.StateOf(lane, i) != PinState.Standing || _rng.Randf() > share) continue;
                var at = Rig.PinAt(lane, i);
                var axis = new Vector3(_rng.RandfRange(-1, 1), 0, _rng.RandfRange(-1, 1)).Normalized();
                var lying = new Basis(axis, Mathf.Pi / 2) * new Basis(Vector3.Up, _rng.Randf() * Mathf.Tau);
                var spot = at.Origin + new Vector3(_rng.RandfRange(-0.25f, 0.25f), LaneGeometry.PinRadius, _rng.RandfRange(-0.1f, 0.35f));
                Rig.SetPin(lane, i, new Transform3D(lying, spot), PinState.Down);
            }
        }
    }
}
