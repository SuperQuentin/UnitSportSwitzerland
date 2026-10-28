using Godot;
using UnitSport.Avatar;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// Bowlers for the look of a centre (#1323, basic for now): figures on a few lanes (<see cref="BowlingLanes.DecoLanes"/>)
/// that stand on the approach and, on a schedule of the shared clock, lift a ball, wind up and roll it, the pins
/// falling in a deck simulated here (a decoration: each peer's own pins), the machine resetting after. And a figure
/// beside the approach for each bot partner in a game (<see cref="BowlingLanes.ServeAddBot"/>), throwing as the
/// server rolls its ball. Part of the built interior, so none of it runs unless someone is inside.
/// </summary>
public partial class BowlingDeco : Node3D
{
    private sealed class Bowler
    {
        public int Pair, Lane;
        public SkinnedFigure Figure = null!;
        public MeshInstance3D Ball = null!;
        public LaneRig Rig = null!;
        public double Period, Offset;
        public int Throw = -1;
        public BowlingBall.Run? Run;
        public PinDeck? Deck;
        public byte Color;
    }

    private const double LiftAt = 0.0, WindAt = 1.2, ReleaseAt = 2.0;

    private readonly List<Bowler> _bowlers = new();
    private readonly Dictionary<long, SkinnedFigure> _partners = new();
    private InteriorNode _node = null!;
    private Material? _figureMaterial;

    public static BowlingDeco Create(InteriorNode node, Material material)
    {
        var deco = new BowlingDeco { Name = "BowlingDeco", _node = node };
        var l = node.Layout;
        foreach (var (pair, lane) in BowlingLanes.DecoLanes(l))
        {
            if (node.LaneRigOf(pair) is not { } rig) continue;
            var f = l.Furniture[pair];
            var g = LaneGeometry.Of(f);
            uint h = (uint)(pair * 2246822519u + (uint)lane * 3266489917u);
            var b = new Bowler
            {
                Pair = pair, Lane = lane, Rig = rig, Period = 20 + h % 9, Offset = h % 97, Color = (byte)(h % InteriorMeshBuilder.BallColorCount),
            };
            b.Figure = Figure(deco, (int)(h % 997), BowlingLanes.PieceFrame(l, f) * new Vector3(g.LaneX(lane) + 0.22f, 0, g.Foul - 1.3f), f.Turns, material);
            b.Ball = new MeshInstance3D { Name = "DecoBall", Mesh = LaneRig.BallMeshOf(b.Color), MaterialOverride = material, Visible = false };
            deco.AddChild(b.Ball);
            deco._bowlers.Add(b);
        }
        return deco;
    }

    /// <summary>A skinned figure standing at <paramref name="at"/> (the plan's frame), facing the pins of a pair turned <paramref name="turns"/>.</summary>
    private static SkinnedFigure Figure(BowlingDeco deco, int look, Vector3 at, int turns, Material material)
    {
        deco._figureMaterial ??= HumanMeshBuilder.FigureMaterial();
        // a figure is authored looking down −Z; the pins are the piece's +Z
        var figure = new SkinnedFigure
        {
            Name = "Bowler", MaterialOverride = deco._figureMaterial,
            Transform = new Transform3D(new Basis(Vector3.Up, turns * Mathf.Pi / 2) * new Basis(Vector3.Up, Mathf.Pi), at),
        };
        figure.Show(new FigureLook(HumanPalette.ForRider(look)));
        HumanMeshBuilder.PosePosed(figure, HumanPose.Standing);
        deco.AddChild(figure);
        return figure;
    }

    public override void _Process(double delta)
    {
        double now = ClockSync.SimNow;
        var lanes = BowlingLanes.Instance;
        foreach (var b in _bowlers)
        {
            // a lane players took has no decoration bowler
            bool taken = lanes?.LaneBusy(_node.Layout.Key, b.Pair, b.Lane) == true;
            if (b.Figure.Visible == taken) b.Figure.Visible = !taken;
            if (taken) { b.Ball.Visible = false; continue; }
            double t = (now + b.Offset) % b.Period;
            int throwNo = (int)((now + b.Offset) / b.Period);
            if (throwNo != b.Throw)
            {
                b.Throw = throwNo;
                b.Deck?.Dispose();
                b.Deck = null;
                var run = b.Rig.Geometry.Run;
                b.Run = BowlingBall.Roll(run, BowlingLanes.BotRelease(run, b.Pair * 7 + b.Lane, throwNo, BowlingLanes.Rack));
                b.Rig.SetRack(b.Lane);
            }
            Pose(b.Figure, t);
            // the ball: in the hand while lifted, then on its run
            if (t < ReleaseAt)
            {
                b.Ball.Visible = t >= LiftAt;
                b.Ball.Position = b.Figure.Position + b.Figure.Basis * new Vector3(-0.25f, 0.45f + 0.25f * (float)Math.Min(1, t / WindAt), -0.15f);
                continue;
            }
            b.Ball.Visible = false;
            float run2 = (float)(t - ReleaseAt);
            if (b.Run is not { } r) continue;
            if (run2 < r.Time) { var (pos, turn) = r.At(run2); b.Rig.ShowBall(b.Lane, pos, turn, b.Color, false); continue; }
            if (r.Gutter) { b.Rig.HideBall(b.Lane); continue; }
            b.Deck ??= new PinDeck(b.Rig.Geometry.Run, BowlingLanes.Rack, r.AtDeck);
            if (b.Deck.Record.Count > 0)
            {
                var last = b.Deck.Record[^1].Bodies;
                b.Rig.ShowDeck(b.Lane, last, last, 0, b.Color);
            }
        }
        Partners(now);
    }

    public override void _PhysicsProcess(double delta)
    {
        foreach (var b in _bowlers)
        {
            if (b.Deck is not { } deck || deck.Done) continue;
            deck.Tick((float)delta);
            if (!deck.Done) continue;
            b.Rig.HideBall(b.Lane);
            b.Rig.StartCycle(b.Lane, PinsetterCycle.Kind.Reset, ClockSync.SimNow + 0.6);
            deck.Dispose();
        }
    }

    /// <summary>Lift, wind up, release, follow through, stand.</summary>
    private static void Pose(SkinnedFigure f, double t)
    {
        if (t < WindAt) HumanMeshBuilder.PosePosed(f, HumanPose.Standing, ItemArmPose.Hold, (float)Math.Clamp(t / 0.4, 0, 1));
        else if (t < ReleaseAt) HumanMeshBuilder.PosePosed(f, HumanPose.Standing, ItemArmPose.ThrowWindup, (float)Math.Clamp((t - WindAt) / 0.5, 0, 1));
        else if (t < ReleaseAt + 1.2) HumanMeshBuilder.PosePosed(f, HumanPose.Standing, ItemArmPose.ThrowRelease, (float)Math.Clamp(1 - (t - ReleaseAt - 0.6) / 0.6, 0, 1));
        else if (t < ReleaseAt + 1.3) HumanMeshBuilder.PosePosed(f, HumanPose.Standing);
    }

    /// <summary>A figure beside the approach for each bot partner of a game in this centre, throwing as its ball leaves.</summary>
    private void Partners(double now)
    {
        if (BowlingLanes.Instance is not { } lanes) return;
        foreach (var (pair, lane, bowler, index, rolling) in lanes.BotsIn(_node.Layout.Key))
        {
            if (!_partners.TryGetValue(bowler.Peer, out var fig))
            {
                var l = _node.Layout;
                var f = l.Furniture[pair];
                var g = LaneGeometry.Of(f);
                // beside the approach, toward the settees, one step apart per bot
                var at = BowlingLanes.PieceFrame(l, f) * new Vector3(g.LaneX(lane) + (lane == 0 ? -0.75f : 0.75f), 0, g.Front + 0.6f + index * 0.7f);
                fig = _partners[bowler.Peer] = Figure(this, (int)(-bowler.Peer % 997), at, f.Turns, null!);
            }
            double since = now - rolling;
            if (!double.IsNaN(rolling) && since < 1.2) Pose(fig, ReleaseAt + since);
            else if (bowler.Ball == BowlingLanes.BallAt.Hand) Pose(fig, WindAt * 0.5);
        }
    }

    public override void _ExitTree()
    {
        foreach (var b in _bowlers) b.Deck?.Dispose();
    }
}
