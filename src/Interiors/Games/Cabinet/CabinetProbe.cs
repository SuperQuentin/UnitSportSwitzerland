using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Games.Cabinet;

/// <summary>
/// <c>godot --headless --path . -- --cabinetcheck --systems ui</c> (#1326): every arcade machine with a screen (upright,
/// racer twin, dance, drums) built as an arcade builds it: its screen plate and attract picture, then a robot's game drawn
/// into it as a player's client draws it (the picture changes every frame, the mesh never), and the machine's coin,
/// score and tickets. The small games themselves have unit tests (<c>CabinetTests</c>).
/// </summary>
public partial class CabinetProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--cabinetcheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[cabinet] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        foreach (var (type, w, d, h) in new[] { (FurnitureType.ArcadeCabinet, 0.78f, 0.9f, 1.85f), (FurnitureType.Racer, 1.6f, 2.3f, 2.0f), (FurnitureType.DanceMachine, 2.0f, 2.4f, 2.4f), (FurnitureType.DrumGame, 1.2f, 1.4f, 1.9f) })
            Machine(type, w, d, h);
        GD.Print($"[cabinet] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private static int Colours(Image img)
    {
        var seen = new HashSet<Color>();
        for (int y = 0; y < img.GetHeight(); y += 3)
            for (int x = 0; x < img.GetWidth(); x += 3) seen.Add(img.GetPixel(x, y));
        return seen.Count;
    }

    private void Machine(FurnitureType type, float w, float d, float h)
    {
        var plan = new FurniturePlan { Type = type, W = w, D = d, H = h };
        var kind = CabinetRig.PlaysOf(plan);
        Check(GameCatalog.Of(type)?.Kind == GameKind.Cabinet, $"{type}: a playable machine ({MiniGame.TitleOf(kind)})");
        var node = CabinetRig.Machine(type, w, d, h, kind, out var robot);
        robot.ProcessMode = ProcessModeEnum.Disabled;
        AddChild(node);
        var rig = robot.Rig;
        var screen = rig.GetNode<MeshInstance3D>("Screen");
        var mesh = screen.Mesh;
        // the attract screen first: drawn, not blank
        rig.Live = false;
        rig.Show(null, 0, 0.016);
        Check(Colours(rig.Picture) >= 3, $"{type}: the attract screen is drawn ({Colours(rig.Picture)} colours)");
        // a robot's game: the picture moves every frame
        robot.Game = MiniGame.Make(kind, 7);
        rig.Live = true;
        var before = rig.Picture.GetData();
        int changed = 0;
        for (int frame = 0; frame < 300; frame++)
        {
            robot.Advance(1 / 60f);
            if (frame % 30 == 29)
            {
                var now = rig.Picture.GetData();
                if (!now.AsSpan().SequenceEqual(before)) changed++;
                before = now;
            }
        }
        Check(changed >= 8, $"{type}: the game moves on the screen ({changed} of 10 looks changed)");
        Check(ReferenceEquals(mesh, screen.Mesh), $"{type}: no mesh rebuilt while playing");
        // the machine's side: a coin, a score, tickets
        var game = new CabinetGame(kind);
        game.Sit(9, 0);
        Check(game.Act(0, CabinetGame.ActCoin, Array.Empty<int>(), 0, new Random(1)) == null && game.Playing, $"{type}: a coin starts a game");
        game.Act(0, CabinetGame.ActEnd, new[] { robot.Game.Score }, 30, new Random(1));
        Check(!game.Playing && game.Payouts.Count == 1 && game.Highs.Count == 1, $"{type}: the score {robot.Game.Score} pays {game.Payouts[0].Count} tickets and tops the table");
        rig.Live = false;
        rig.OnState(game, 31);
        rig.Show(game, 31, 0.016);
        node.QueueFree();
    }
}
