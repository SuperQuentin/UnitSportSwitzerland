using Godot;
using UnitSport.Avatar;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// Bowlers for the look of a centre (#1323, basic for now): figures on a few lanes (<see cref="BowlingLanes.DecoLanes"/>)
/// that, on a schedule of the shared clock, take a ball from the return and bowl it (<see cref="DecoThrow"/>: the
/// approach and the underarm swing, #1334), the pins falling in a deck simulated here (a decoration: each peer's own
/// pins). Frames of two balls as on a real lane: a first ball that leaves pins has the pinsetter pick and set them back
/// and the second ball aimed at them; a strike or a second ball has the sweep clear the deck and a new rack come down.
/// The deck is drawn only while it moves, then the machine has the pins. And a figure beside the approach for each bot
/// partner in a game (<see cref="BowlingLanes.ServeAddBot"/>), swinging as the server rolls its ball. Part of the built
/// interior, so none of it runs unless someone is inside.
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
        public Transform3D Frame;
        /// <summary>The ball of the frame (1, 2), the pins standing for it, and whether this throw's pins have settled.</summary>
        public int BallNo = 1;
        public ushort Standing = BowlingLanes.Rack;
        public bool Settled;
        /// <summary>This throw's ball is back on the return.</summary>
        public bool Returned;
    }

    /// <summary>How long a returned ball takes to rise into the hood, s.</summary>
    private const double ReturnRise = 0.35;

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
                // a turn, the roll, the deck and the pinsetter's longest cycle: about 21 s
                Pair = pair, Lane = lane, Rig = rig, Period = 23 + h % 7, Offset = h % 97, Color = (byte)(h % InteriorMeshBuilder.BallColorCount),
                Frame = BowlingLanes.PieceFrame(l, f),
            };
            b.Figure = Figure(deco, (int)(h % 997), b.Frame * new Vector3(g.LaneX(lane) + 0.22f, 0, g.Foul - 1.3f), f.Turns, material);
            b.Ball = new MeshInstance3D { Name = "DecoBall", Mesh = LaneRig.BallMeshOf(b.Color), MaterialOverride = material, Visible = false };
            deco.AddChild(b.Ball);
            deco._bowlers.Add(b);
            var stance = b.Frame * new Vector3(g.LaneX(lane), 0, g.Foul - 3f);
            GD.Print($"[bowling] decoration bowler on pair {pair} lane {lane}, its approach at plan {stance.X:F1},{stance.Z:F1}, pins toward turn {f.Turns}");
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
            var g = b.Rig.Geometry;
            if (throwNo != b.Throw)
            {
                // the first turn seen starts a frame on a full rack; after that the pinsetter has set what stands
                if (b.Throw < 0) { b.Rig.SetRack(b.Lane); (b.BallNo, b.Standing) = (1, BowlingLanes.Rack); }
                b.Throw = throwNo;
                b.Deck?.Dispose();
                b.Deck = null;
                b.Settled = false;
                b.Returned = false;
                b.Run = BowlingBall.Roll(g.Run, BowlingLanes.BotRelease(g.Run, b.Pair * 7 + b.Lane, throwNo, b.Standing));
            }
            var frame = DecoThrow.At(g, b.Lane, t);
            b.Figure.Transform = b.Frame * new Transform3D(new Basis(Vector3.Up, frame.Face + Mathf.Pi), frame.Feet);
            HumanMeshBuilder.PoseJoints(b.Figure, frame.Joints);
            // the ball: on the return, then in the hands until they let go, on its run, and back up through the
            // return after the real lanes' time in the subway (#1334)
            b.Ball.Visible = frame.Ball != null;
            if (frame.Ball is { } held) { b.Ball.Position = b.Frame * held; continue; }
            float run = (float)(t - DecoThrow.Release);
            if (run < 0 || b.Run is not { } r) continue;
            double back = DecoThrow.Release + r.Time + BowlingLanes.ReturnTime;
            if (t >= back - ReturnRise)
            {
                // pushed up into the hood from under it, with its thunk as it arrives
                float up = (float)Math.Min(1, (t - (back - ReturnRise)) / ReturnRise);
                b.Ball.Visible = true;
                b.Ball.Position = b.Frame * (DecoThrow.HoodBall(g) + Vector3.Down * (0.3f * (1 - up)));
                if (up >= 1 && !b.Returned) { b.Returned = true; b.Rig.HoodThunk(); }
            }
            if (b.Settled) continue;
            if (run < r.Time) { var (pos, turn) = r.At(run); b.Rig.ShowBall(b.Lane, pos, turn, b.Color, false); continue; }
            if (r.Gutter) { Settle(b); continue; }
            b.Deck ??= new PinDeck(g.Run, b.Standing, r.AtDeck);
            // the pins as the deck has them, only while they move: then the machine has them
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
            if (b.Settled || b.Deck is not { } deck) continue;
            if (!deck.Done) deck.Tick((float)delta);
            if (deck.Done) Settle(b);
        }
    }

    /// <summary>
    /// A throw's pins at rest: the pinsetter picks and sets back what stands after a first ball that left pins, and the
    /// second ball is aimed at them; after a strike or a second ball the sweep clears the deck and a new rack comes down.
    /// </summary>
    private static void Settle(Bowler b)
    {
        b.Settled = true;
        b.Rig.HideBall(b.Lane);
        ushort standing = 0;
        for (int i = 0; i < 10; i++)
            if ((b.Standing & (1 << i)) != 0 && b.Rig.StateOf(b.Lane, i) == LaneRig.PinState.Standing) standing |= (ushort)(1 << i);
        bool pick = b.BallNo == 1 && standing != 0;
        b.Rig.StartCycle(b.Lane, pick ? PinsetterCycle.Kind.Pick : PinsetterCycle.Kind.Reset, ClockSync.SimNow + 0.6);
        (b.BallNo, b.Standing) = pick ? (2, standing) : (1, BowlingLanes.Rack);
        b.Deck?.Dispose();
        b.Deck = null;
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
            // the underarm swing round the server's release (DecoThrow), the ball at the chest while it waits to bowl
            double since = now - rolling;
            if (!double.IsNaN(rolling) && since < 1.2) HumanMeshBuilder.PoseJoints(fig, DecoThrow.Swing(since));
            else if (bowler.Ball == BowlingLanes.BallAt.Hand) HumanMeshBuilder.PoseJoints(fig, DecoThrow.Swing(-1));
        }
    }

    public override void _ExitTree()
    {
        foreach (var b in _bowlers) b.Deck?.Dispose();
    }
}
