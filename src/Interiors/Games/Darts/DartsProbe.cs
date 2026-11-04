using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Darts;

/// <summary>
/// <c>godot --headless --path . -- --dartscheck --systems ui</c> (#1324): a dartboard built as a venue builds it, two
/// bots playing 501 on it through its rig as a client shows a game (60 frames a second of shared time): every dart
/// flies and ends stuck where the server says it landed, the visit's darts are pulled, the leg ends with a checkout,
/// the face's picture scores as the board does, the state survives the wire, and nothing rebuilds a mesh. The board
/// and the rules have their own unit tests (<c>DartsTests</c>).
/// </summary>
public partial class DartsProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--dartscheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[darts] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        var node = DartsRig.Board(out var driver);
        AddChild(node);
        var rig = driver.Rig;
        Check(GameCatalog.Of(FurnitureType.DartBoard)?.Kind == GameKind.Darts, "a dartboard is a playable piece");
        Check(node.TableRigOf(0) == rig, "the venue's node built the board's rig");
        var dart0 = rig.GetNode<MeshInstance3D>("Dart0");
        var mesh = dart0.Mesh;

        int landed = 0, offBy = 0, flying = 0, pulls = 0, lastSeq = 0, shownBefore = 0;
        for (int frame = 0; frame < 60 * 60 * 10 && driver.Game.Winner < 0; frame++)
        {
            driver.Advance(1.0 / 60);
            var g = driver.Game;
            int shown = rig.GetChildren().OfType<MeshInstance3D>().Count(m => m.Name.ToString().StartsWith("Dart") && m.Visible);
            if (shown < shownBefore) pulls++;
            shownBefore = shown;
            double since = driver.Clock - g.ThrowAt;
            if (g.ThrowSeq != lastSeq && since > 0.05 && since < DartsGame.Flight * 0.9) flying++;
            if (g.ThrowSeq != lastSeq && since >= DartsGame.Flight + 0.05)
            {
                lastSeq = g.ThrowSeq;
                // a bust's placeholders and a checkout's pull leave nothing new to look at
                if (g.Board.Count == 0 || g.Board[^1].At.X > 9000) continue;
                var last = g.Board[^1];
                landed++;
                int k = g.InBoard().Count() - 1;
                var d = rig.GetNode<MeshInstance3D>($"Dart{k}");
                var tip = d.Position;
                var want = rig.OnFace(last.At, -0.012f);
                if (!d.Visible || tip.DistanceTo(want) > 0.001f) offBy++;
            }
        }
        var game = driver.Game;
        Check(game.Winner >= 0, $"two bots played a leg to its end in {driver.Thrown} darts: {game.Call}");
        Check(landed > 20 && offBy == 0, $"every dart stuck where it landed ({landed} darts, {offBy} off)");
        Check(flying > 10, $"the darts flew to the board ({flying} frames in flight seen)");
        Check(pulls > 3, $"the visits' darts were pulled ({pulls})");
        Check(ReferenceEquals(mesh, dart0.Mesh), "no mesh rebuilt while playing");

        // the face's picture and the board agree: a treble 20's pixel is red, a single 20's black, the bull red
        var img = DartsRig.Face().GetImage();
        Color At(Vector2 mm) => img.GetPixel((int)(256 + mm.X / DartBoard.Radius * 256), (int)(256 - mm.Y / DartBoard.Radius * 256));
        Check(At(DartBoard.Aim(20, 3)).R > 0.5f && At(DartBoard.Aim(20, 3)).G < 0.3f, "the treble 20 is drawn red");
        Check(At(DartBoard.Aim(20, 1)).R < 0.2f, "the single 20 is drawn black");
        Check(At(DartBoard.Aim(1, 1)).R > 0.7f, "the single 1 is drawn cream");
        Check(At(Vector2.Zero).R > 0.5f && At(Vector2.Zero).G < 0.3f, "the bull is red");

        var copy = new DartsGame();
        var bytes = game.Pack();
        copy.Unpack(bytes);
        Check(copy.Call == game.Call && copy.Winner == game.Winner && copy.Scores.SequenceEqual(game.Scores), $"the state survives the wire ({bytes.Length} bytes)");
        GD.Print($"[darts] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }
}
