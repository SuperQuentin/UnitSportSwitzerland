using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Interiors;

// What E (pad Y, a VR hand reaching out) does in a bowling centre (#1323): sign in or out at the shoe
// counter, take the house ball reached for at a rack, take your ball back from the hood, step up to bowl.
// One targeting rule (At) for the prompt, the outline and the use.
public partial class BowlingLanes
{
    private const float UseReach = 1.2f;

    private enum Use { SignIn, SignOut, AddBot, Rack, Hood, Bowl, Return }

    private (Use Use, InteriorNode Node, int Piece, int Tier, int Slot)? At(FootPlayer p)
    {
        if (!p.Indoors || InteriorManager.Instance is not { Current: { Type: BuildingType.Bowling } layout, CurrentNode: { } node }) return null;
        var me = SignedIn(MyId);
        int counter = Loot.LootService.NearestOf(p, layout, node, t => t == FurnitureType.ShoeCounter, UseReach);
        if (counter >= 0)
        {
            if (me == null) return (Use.SignIn, node, counter, 0, 0);
            // before the first ball, with room: a bot partner (leaving the building gives the shoes back too)
            var mine = PairOf(me.Value.Key, me.Value.Pair)[me.Value.Lane];
            bool open = mine.Bowlers.Count < MaxBowlers && mine.Bowlers.All(b => b.Rolls.Count == 0);
            return (open ? Use.AddBot : Use.SignOut, node, counter, 0, 0);
        }
        if (me == null) return null;
        if (me.Value.Bowler.Ball == BallAt.None && me.Value.Key == layout.Key)
        {
            // a house ball off the pair's return, at it (#1336)
            var mine = layout.Furniture[me.Value.Pair];
            var gr = LaneGeometry.Of(mine);
            var near = PieceFrame(layout, mine).AffineInverse() * node.ToLocal(p.GlobalPosition);
            var lanes = PairOf(layout.Key, me.Value.Pair);
            if (Mathf.Abs(near.X) < 1.2f && near.Z > gr.Front - 1f && near.Z < gr.HoodBack + 0.6f && lanes.Any(ln => ln.House.Count > 0))
                return (Use.Return, node, me.Value.Pair, 0, 0);
        }
        if (me.Value.Bowler.Ball == BallAt.None)
        {
            int rack = Loot.LootService.NearestOf(p, layout, node, t => t == FurnitureType.BallRack, UseReach);
            if (rack < 0) return null;
            // the ball reached for: the one across the rack nearest the player, the lower tier when looking down
            var f = layout.Furniture[rack];
            var local = PieceFrame(layout, f).AffineInverse() * node.ToLocal(p.GlobalPosition);
            int n = InteriorMeshBuilder.RackBalls(f), slot = 0;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float d = Mathf.Abs(InteriorMeshBuilder.RackBall(f, i, 0).X - local.X);
                if (d < best) { best = d; slot = i; }
            }
            int tier = p.Camera.GlobalTransform.Basis.Z.Y > 0.45f ? 0 : 1;
            return (Use.Rack, node, rack, tier, slot);
        }
        if (me.Value.Key != layout.Key) return null;
        var pair = layout.Furniture[me.Value.Pair];
        var g = LaneGeometry.Of(pair);
        var at = PieceFrame(layout, pair).AffineInverse() * node.ToLocal(p.GlobalPosition);
        bool onApproach = Mathf.Abs(at.X) < g.W / 2 + 0.6f && at.Z > g.Front - 1.5f && at.Z < g.Foul;
        if (!onApproach) return null;
        if (me.Value.Bowler.Ball == BallAt.Hood) return (Use.Hood, node, me.Value.Pair, 0, 0);
        var lane = PairOf(layout.Key, me.Value.Pair)[me.Value.Lane];
        if (me.Value.Bowler.Ball == BallAt.Hand && lane.Up?.Peer == MyId) return (Use.Bowl, node, me.Value.Pair, 0, 0);
        return null;
    }

    private string Text((Use Use, InteriorNode Node, int Piece, int Tier, int Slot) at)
    {
        switch (at.Use)
        {
            case Use.SignIn: return "Sign in to bowl";
            case Use.SignOut: return "Give the shoes back";
            case Use.AddBot: return "Add a bot partner";
            case Use.Rack:
            {
                var (_, _, _, pounds) = InteriorMeshBuilder.RackBall(at.Node.Layout.Furniture[at.Piece], at.Slot, at.Tier);
                return $"Take the {pounds} lb ball";
            }
            case Use.Hood: return "Take your ball";
            case Use.Return: return "Take a house ball";
            default: return "Bowl";
        }
    }

    /// <summary>What E does here and what to outline, or null.</summary>
    public static Interaction? InteractionFor(FootPlayer p) =>
        Instance?.At(p) is { } at ? new(Core.InputHints.Prompt(Core.PlayerInput.InteractMount, Instance.Text(at)), OutlineOf(at)) : null;

    /// <summary>A counter or rack whole; at a lane pair (26 m long) only its hood, where the ball is and the bowler stands.</summary>
    private static Outline OutlineOf((Use Use, InteriorNode Node, int Piece, int Tier, int Slot) at)
    {
        if (at.Use is not (Use.Hood or Use.Bowl or Use.Return)) return Outline.Piece(at.Node, at.Piece, 0);
        var l = at.Node.Layout;
        var g = LaneGeometry.Of(l.Furniture[at.Piece]);
        // the ball return whole (#1336): its curled track and its hood
        float near = g.ReturnBend - LaneGeometry.ReturnRun - 0.12f;
        var hood = PieceFrame(l, l.Furniture[at.Piece]) * new Transform3D(Basis.Identity, new Vector3(0, 0.53f, (near + g.HoodBack) / 2));
        return Outline.Box(at.Node, hood, new Vector3(0.62f, 1.06f, g.HoodBack - near));
    }

    /// <summary>E in a bowling centre: true when it did something here.</summary>
    public static bool TryUse(FootPlayer p)
    {
        if (Instance is not { } b || b.At(p) is not { } at) return false;
        string key = at.Node.Layout.Key;
        switch (at.Use)
        {
            case Use.SignIn or Use.SignOut:
                b.AskSignIn(key, "You");
                break;
            case Use.Rack:
            {
                var (_, _, color, pounds) = InteriorMeshBuilder.RackBall(at.Node.Layout.Furniture[at.Piece], at.Slot, at.Tier);
                b.AskTakeBall(key, color, pounds);
                break;
            }
            case Use.AddBot:
                b.AskAddBot(key);
                break;
            case Use.Hood:
                b.AskTakeBall(key, 0, 0);
                break;
            case Use.Return:
                b.AskTakeBall(key, -1, 0);
                break;
            case Use.Bowl:
                BowlingAim.Begin(p, key, at.Piece);
                break;
        }
        return true;
    }
}
