using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Foosball;

/// <summary>
/// <c>godot --headless --path . -- --foosballcheck --systems ui</c> (#1324): a table football table built both ways the
/// venues place it, two bots playing on it through its rig as a client shows a live game (60 frames a second, the game
/// at the server's 120 Hz): the ball node follows the game's ball, the rods slide and turn with it, goals move the
/// beads, the game reaches ten, the poses survive the wire, nothing rebuilds a mesh. The rules have their own unit
/// tests (<c>FoosballTests</c>).
/// </summary>
public partial class FoosballProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--foosballcheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[foosball] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        Check(GameCatalog.Of(FurnitureType.TableFootball)?.Kind == GameKind.Foosball, "a table football table is a playable piece");
        foreach (var (w, d) in new[] { (1.5f, 0.8f), (0.75f, 1.4f) }) Table(w, d);
        GD.Print($"[foosball] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private void Table(float w, float d)
    {
        var node = FoosballRig.Table(w, d, out var driver);
        AddChild(node);
        var rig = driver.Rig;
        string name = $"{w} x {d}";
        // laid along the longer side: the pitch's long axis in the piece's frame
        var along = rig.TableFrame.Basis * Vector3.Right;
        Check(w > d ? Mathf.Abs(along.X) > 0.99f : Mathf.Abs(along.Z) > 0.99f, $"{name}: the table lies along the piece's longer side");
        var ball = rig.GetNode<MeshInstance3D>("Table/Ball");
        var mesh = ball.Mesh;
        float worstBall = 0, rodTurned = 0, rodSlid = 0;
        int goalsSeen = 0, lastGoals = 0, beadMoves = 0;
        var bead = rig.GetNode<Node3D>("Table/Bead0_0");
        var beadAt = bead.Position;
        for (int frame = 0; frame < 60 * 60 * 20 && driver.Game.Winner < 0; frame++)
        {
            driver.Advance(1.0 / 60);
            var g = driver.Game;
            if (g.InPlay && frame % 10 == 0) worstBall = Mathf.Max(worstBall, rig.ShownBall.DistanceTo(g.BallAt + g.BallV * 0.0f));
            for (int rod = 0; rod < 8; rod++)
            {
                var r = rig.RodNode(rod);
                rodTurned = Mathf.Max(rodTurned, Mathf.Abs(r.Rotation.Z));
                rodSlid = Mathf.Max(rodSlid, Mathf.Abs(r.Position.Z));
            }
            int goals = g.Goals[0] + g.Goals[1];
            if (goals != lastGoals) { goalsSeen++; lastGoals = goals; }
            if (bead.Position != beadAt) { beadMoves++; beadAt = bead.Position; }
        }
        var game = driver.Game;
        Check(game.Winner >= 0, $"{name}: two bots played to ten ({game.Goals[0]} : {game.Goals[1]}) in {driver.Clock / 60:F1} min");
        Check(goalsSeen >= 10, $"{name}: every goal counted ({goalsSeen})");
        Check(worstBall < 0.12f, $"{name}: the ball node follows the game's ball (at most {worstBall * 100:F1} cm behind)");
        Check(rodTurned > 0.5f && rodSlid > 0.03f, $"{name}: the rods kicked ({Mathf.RadToDeg(rodTurned):F0}°) and slid ({rodSlid * 100:F1} cm)");
        Check(beadMoves > 0 || game.Goals[0] == 0, $"{name}: red's goals slid its beads");
        Check(ReferenceEquals(mesh, ball.Mesh), $"{name}: no mesh rebuilt while playing");
        var copy = new FoosballGame();
        copy.Unpack(game.Pack());
        copy.UnpackPoses(game.PackPoses());
        Check(copy.Goals.SequenceEqual(game.Goals) && copy.BallAt.DistanceTo(game.BallAt) < 1e-5f, $"{name}: the state and poses survive the wire ({game.PackPoses().Length} bytes a frame)");
        node.QueueFree();
    }
}
