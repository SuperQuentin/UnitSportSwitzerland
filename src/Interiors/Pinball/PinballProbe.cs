using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// <c>godot --headless --path . -- --pinballcheck --systems ui</c> (#1325): each of the four cabinets built and
/// played by the robot for two minutes of game time through its nodes, as a client shows a game: the flippers
/// and balls follow the solver, drop targets fall and come back, the lamps and the backbox change, every
/// era's display draws the long messages, the screen survives the wire, and nothing that moves rebuilds a
/// mesh (the shared meshes stay the same objects). The solver and rules have their own unit tests.
/// </summary>
public partial class PinballProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--pinballcheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[pinball] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        var material = Styles.StyleKit.Material(Styles.MaterialRole.Interior);
        for (int i = 0; i < PinballLayouts.Count; i++) Table(i, material);
        GD.Print($"[pinball] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private void Table(int index, Material material)
    {
        var t = PinballLayouts.Get(index);
        var c = PinballCabinet.Create(index, material);
        AddChild(c);
        string name = t.Title;
        var meshes = Meshes(c);
        Check(c.FindChildren("Flipper*", "", true, false).Count == t.Flippers.Count, $"{name}: a flipper node per flipper");
        var inserts = c.FindChild("Inserts", true, false) as MultiMeshInstance3D;
        var arrows = c.FindChild("Arrows", true, false) as MultiMeshInstance3D;
        Check(inserts != null && arrows != null && inserts.Multimesh.InstanceCount + arrows.Multimesh.InstanceCount == t.Lamps.Count,
            $"{name}: a lamp instance per lamp ({t.Lamps.Count})");

        var game = new PinballGame(t, 1325 + index);
        game.Start();
        var poses = new Vector3[PinballCabinet.MaxBalls];
        float flipMoved = 0, ballGap = 0;
        int dropsDown = 0, dropHits = 0, displayChanges = 0, lampChanges = 0;
        string shown = "";
        var lampsBefore = (byte[])c.LampsShown.Clone();
        var flipper = (Node3D)c.FindChild("Flipper0", true, false)!;
        var ball = (Node3D)c.FindChild("Ball0", true, false)!;
        for (int ms = 0; ms < 120_000; ms++)
        {
            PinballCabinet.RobotDriver.Play(game, ms);
            game.Run(1);
            foreach (var e in game.Outbox)
                if (e.Kind == PinballSolver.EventKind.Switch && e.Id >= 0 && t.Switches[e.Id].Kind == SwitchKind.Drop) dropHits++;
            game.Outbox.Clear();
            if (ms % 16 != 0) continue;
            // a client's frame: what it shows of the game
            c.Show(game, ms / 1000.0, poses);
            flipMoved = Math.Max(flipMoved, Mathf.Abs(flipper.Rotation.Y - t.Flippers[0].Rest));
            if (game.Solver.Balls.Count > 0 && ball.Visible)
                ballGap = Math.Max(ballGap, (ball.Position - PinballCabinet.Local(game.Solver.Balls[0].Pos3)).Length());
            foreach (var d in c.FindChildren("Drop*", "", true, false))
                if (((Node3D)d).Position.Y < -0.01f) { dropsDown++; break; }
            string key = PinballScreen.Of(game.Rules).DisplayKey();
            if (key != shown) { shown = key; displayChanges++; }
            if (ms % 4000 == 0 && !c.LampsShown.SequenceEqual(lampsBefore)) { lampChanges++; lampsBefore = (byte[])c.LampsShown.Clone(); }
        }
        Check(flipMoved > 0.5f, $"{name}: the flipper node swung with the solver ({Mathf.RadToDeg(flipMoved):F0}°)");
        Check(ballGap < 1e-4f, $"{name}: the ball node is where the solver has it (off by {ballGap * 1000:F2} mm)");
        Check(dropHits == 0 || dropsDown > 0, $"{name}: drop targets hit went down ({dropHits} hits, {dropsDown} frames down)");
        Check(displayChanges > 10, $"{name}: the backbox redrew as the game went ({displayChanges} changes)");
        Check(lampChanges > 0, $"{name}: the lamps changed ({lampChanges} times)");
        Check(game.Rules.Players[0].Score > 0, $"{name}: the robot scored {PinballRules.Fmt(game.Rules.Players[0].Score)}");
        Check(SameMeshes(meshes, Meshes(c)), $"{name}: no mesh rebuilt while playing");

        // the screen over the wire, and every era's display with the longest words
        var s = PinballScreen.Of(game.Rules);
        var back = PinballScreen.Unpack(s.Pack());
        Check(back.DisplayKey() == s.DisplayKey() && back.Lamps.SequenceEqual(s.Lamps), $"{name}: the screen survives the wire ({s.Pack().Length} bytes)");
        try
        {
            var shout = new PinballScreen { Phase = PinballRules.Phase.Playing, Scores = new long[] { 9_999_999_999, 123, 0, 45_678 }, Up = 3, Ball = 3,
                Message = "SUPER LONG MESSAGE THAT DOES NOT FIT ON ANY DISPLAY", Mission = t.Missions[^1].Name.ToUpperInvariant() + " 99/99 120s", Lamps = s.Lamps };
            c.ShowScreen(shout, 1);
            c.ShowScreen(PinballScreen.Idle(t, new long[] { 1, 2 }), 4);
            Check(true, $"{name}: the {t.Era} display draws long words and four players");
            // attract: the high scores come up every third turn of the cycle (3 s each), in the era's style
            var highs = new PinballHighScores();
            foreach (var (who, score) in new[] { ("Anna", 1_234_560L), ("Beat", 987_650L), ("Chantal Verylongname", 45_000L), ("Dario", 12_340L), ("Elsa", 990L) }) highs.Offer(who, score);
            c.ShowScreen(PinballScreen.Idle(t), 6.5, highs);
            Check(c.Display.Shown.Contains("Anna"), $"{name}: attract shows the high scores");
            c.ShowScreen(PinballScreen.Idle(t), 0.5, highs);
            Check(!c.Display.Shown.Contains("Anna"), $"{name}: and then the title again");
        }
        catch (Exception e)
        {
            Check(false, $"{name}: the {t.Era} display threw {e.Message}");
        }
        c.QueueFree();
    }

    private static List<Mesh> Meshes(Node root)
    {
        var list = new List<Mesh>();
        foreach (var n in root.FindChildren("*", "MeshInstance3D", true, false))
            if (n is MeshInstance3D { Mesh: { } m }) list.Add(m);
        return list;
    }

    private static bool SameMeshes(List<Mesh> a, List<Mesh> b) => a.Count == b.Count && a.Zip(b).All(p => ReferenceEquals(p.First, p.Second));
}
