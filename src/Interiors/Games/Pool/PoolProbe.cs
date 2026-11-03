using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Pool;

/// <summary>
/// <c>godot --headless --path . -- --poolcheck --systems ui</c> (#1324): a pool table built as a venue builds it, two
/// bots playing eight-ball on it through its rig as a client shows a game (60 frames a second of shared time): the
/// ball instances end where the server's table rests after every shot, potted balls leave the cloth, the cue
/// strokes, the game ends with a winner, the state survives the wire, and nothing rebuilds a mesh. The solver and
/// the rules have their own unit tests (<c>PoolTests</c>).
/// </summary>
public partial class PoolProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--poolcheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[pool] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        var node = PoolRig.Table(1.4f, 2.7f, out var driver);
        AddChild(node);
        var rig = driver.Rig;
        Check(GameCatalog.Of(FurnitureType.PoolTable)?.Kind == GameKind.Pool, "a pool table is a playable piece");
        Check(node.TableRigOf(0) == rig, "the venue's node built the table's rig");
        var balls = rig.GetNode<MultiMeshInstance3D>("Balls");
        var cue = rig.GetNode<Node3D>("Cue");
        var mesh = balls.Multimesh.Mesh;
        Check(balls.Multimesh.InstanceCount == PoolSolver.Balls, "a ball instance per ball");

        int shotsSeen = 0, offBy = 0, cueStrokes = 0, potsShown = 0;
        float worst = 0;
        int lastShot = 0;
        bool cueWas = false;
        for (int frame = 0; frame < 60 * 60 * 15 && driver.Game.State != PoolGame.Phase.Over; frame++)
        {
            driver.Advance(1.0 / 60);
            if (cue.Visible && !cueWas) cueStrokes++;
            cueWas = cue.Visible;
            var g = driver.Game;
            // the table still and the replay over: the instances are where the server's table rests
            if (g.ShotSeq != lastShot && driver.Clock > g.ShotEnd + PoolRig.StrokeTime + 0.1)
            {
                lastShot = g.ShotSeq;
                shotsSeen++;
                for (int i = 0; i < PoolSolver.Balls; i++)
                {
                    var at = rig.Shown(i);
                    if (!g.Table.On[i])
                    {
                        if (at == null) potsShown++;
                        else offBy++;
                        continue;
                    }
                    float gap = at is { } p ? p.DistanceTo(g.Table.P[i]) : 99;
                    worst = Mathf.Max(worst, gap);
                    if (gap > 0.002f) offBy++;
                }
            }
        }
        var game = driver.Game;
        Check(game.State == PoolGame.Phase.Over && game.Winner >= 0, $"two bots played a game to its end in {driver.Shots} shots: {game.Call}");
        Check(shotsSeen >= driver.Shots - 1, $"every shot replayed ({shotsSeen} of {driver.Shots})");
        Check(offBy == 0, $"the balls end where the server's table rests (worst {worst * 1000:F2} mm)");
        Check(potsShown > 0, $"potted balls leave the cloth ({potsShown} ball-shots)");
        Check(cueStrokes >= driver.Shots - 1, $"the cue strokes each shot ({cueStrokes})");
        Check(ReferenceEquals(mesh, balls.Multimesh.Mesh), "no mesh rebuilt while playing");

        var copy = new PoolGame(rig.Cloth.Hx, rig.Cloth.Hz);
        var bytes = game.Pack();
        copy.Unpack(bytes);
        Check(copy.Call == game.Call && copy.ShotSeq == game.ShotSeq && copy.Winner == game.Winner, $"the state survives the wire ({bytes.Length} bytes)");
        GD.Print($"[pool] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }
}
