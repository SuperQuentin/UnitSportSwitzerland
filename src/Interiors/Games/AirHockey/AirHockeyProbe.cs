using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.AirHockey;

/// <summary>
/// <c>godot --headless --path . -- --airhockeycheck --systems ui</c> (#1326): an air hockey table built as an arcade
/// builds it, two bots playing on it through its rig as a client shows a live game (60 frames a second, the game at the
/// server's 120 Hz): the puck node follows the game's puck, each mallet stays in its half, the scorer shows each goal,
/// the game reaches seven, the poses survive the wire, nothing rebuilds a mesh. The rules have unit tests (<c>AirHockeyTests</c>).
/// </summary>
public partial class AirHockeyProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--airhockeycheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[airhockey] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        Check(GameCatalog.Of(FurnitureType.AirHockey)?.Kind == GameKind.AirHockey, "an air hockey table is a playable piece");
        var node = AirHockeyRig.Table(1.2f, 2.1f, out var driver);
        AddChild(node);
        var rig = driver.Rig;
        Check(node.TableRigOf(0) == rig, "the arcade's node built the table's rig");
        var puck = rig.GetNode<MeshInstance3D>("Puck");
        var mesh = puck.Mesh;
        var score = rig.GetNode<Label3D>("Score");
        float worst = 0;
        bool halves = true;
        int scoreChanges = 0;
        string shown = score.Text;
        for (int frame = 0; frame < 60 * 60 * 20 && driver.Game.Winner < 0; frame++)
        {
            driver.Advance(1.0 / 60);
            var g = driver.Game;
            if (g.InPlay && frame % 10 == 0) worst = Mathf.Max(worst, rig.ShownPuck.DistanceTo(g.PuckAt));
            halves &= rig.GetNode<Node3D>("Mallet0").Position.Z < 0.01f && rig.GetNode<Node3D>("Mallet1").Position.Z > -0.01f;
            if (score.Text != shown) { shown = score.Text; scoreChanges++; }
        }
        var game = driver.Game;
        Check(game.Winner >= 0, $"two bots played to seven ({game.Goals[0]} : {game.Goals[1]}) in {driver.Clock / 60:F1} min");
        Check(scoreChanges >= 7, $"the scorer showed every goal ({scoreChanges}, now {score.Text})");
        Check(worst < 0.25f, $"the puck node follows the game's puck (at most {worst * 100:F1} cm behind)");
        Check(halves, "each mallet stayed in its half");
        Check(ReferenceEquals(mesh, puck.Mesh), "no mesh rebuilt while playing");
        var copy = new AirHockeyGame();
        copy.Unpack(game.Pack());
        copy.UnpackPoses(game.PackPoses());
        Check(copy.Goals.SequenceEqual(game.Goals) && copy.PuckAt.DistanceTo(game.PuckAt) < 1e-5f, $"the state and poses survive the wire ({game.PackPoses().Length} bytes a frame)");
        GD.Print($"[airhockey] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }
}
