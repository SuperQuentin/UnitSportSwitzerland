using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Claw;

/// <summary>
/// <c>godot --headless --path . -- --clawcheck --systems ui</c> (#1326): a claw machine built as an arcade builds it, a bot
/// playing it through its rig as a client shows a live game: the gantry and carriage follow the claw, the cable reaches
/// down and the prongs close, a won prize rides up in the claw and leaves the box, the pile restocks, the countdown
/// counts, nothing rebuilds a mesh, the state survives the wire. The rules have unit tests (<c>ClawTests</c>).
/// </summary>
public partial class ClawProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--clawcheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[claw] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        Check(GameCatalog.Of(FurnitureType.ClawMachine)?.Kind == GameKind.Claw, "a claw machine is a playable piece");
        var node = ClawRig.Machine(out var driver);
        AddChild(node);
        var rig = driver.Rig;
        Check(node.TableRigOf(0) == rig, "the arcade's node built the machine's rig");
        var cable = rig.GetNode<MeshInstance3D>("Gantry/Carriage/Cable");
        var prong = rig.GetNode<Node3D>("Gantry/Carriage/Claw/Prong0/Pivot");
        var countdown = rig.GetNode<Label3D>("Countdown");
        var mesh = cable.Mesh;
        float longest = 0, closedMost = 1, worstClaw = 0;
        bool counted = false, prizeRode = false;
        for (int frame = 0; frame < 60 * 60 * 8 && driver.Goes < 12; frame++)
        {
            driver.Advance(1.0 / 60);
            var g = driver.Game;
            longest = Mathf.Max(longest, cable.Scale.Y);
            closedMost = Mathf.Min(closedMost, prong.Rotation.X);
            if (g.State == ClawGame.Phase.Steer && countdown.Text.Length == 2) counted = true;
            if (g.Held >= 0 && g.State == ClawGame.Phase.Carry) prizeRode = true;
            if (frame % 30 == 0) worstClaw = Mathf.Max(worstClaw, rig.Shown.At.DistanceTo(g.ClawAt));
        }
        var game = driver.Game;
        Check(driver.Goes >= 10, $"the bot had {driver.Goes} goes");
        Check(game.Won >= 1, $"it won {game.Won} prizes");
        Check(prizeRode, "a won prize rode in the claw to the chute");
        Check(game.Pile.Count >= ClawGame.Restock, $"the pile restocked ({game.Pile.Count})");
        Check(longest > 0.6f, $"the cable reached down ({longest:F2} m)");
        Check(closedMost < 0.1f, $"the prongs closed ({Mathf.RadToDeg(closedMost):F0}°)");
        Check(worstClaw < 0.06f, $"the gantry follows the claw (at most {worstClaw * 100:F1} cm behind)");
        Check(counted, "the countdown counted");
        Check(ReferenceEquals(mesh, cable.Mesh), "no mesh rebuilt while playing (the cable is scaled)");
        var copy = new ClawGame();
        copy.Unpack(game.Pack());
        Check(copy.Pile.Count == game.Pile.Count && copy.Won == game.Won, $"the state survives the wire ({game.Pack().Length} bytes)");
        GD.Print($"[claw] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }
}
