using System.Threading.Tasks;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--chaircheck</c> (#1385), offline, on the real tiles at Trois Verres, Geneva
/// (<c>--chunks &lt;main&gt;/terrain_chunks --at 2499794.3,1117466.5</c>): the player stands behind a chair of the
/// restaurant's terrace (<see cref="Interiors.ChairSeats"/>), sits down with E (the key's own path), is on the seat facing
/// the table and held still; E again does not stand up at a table (it is the fondue there); a jump stands up; then a
/// bot's way, sitting by the method and standing by wishing to walk. Windowed, it saves <c>test_output/chairs/*.png</c>.
/// Read the <c>[chaircheck]</c> lines; the last is the RESULT.
/// </summary>
public partial class ChairProbe : Node
{
    public static bool Requested => CmdArgs.Has("--chaircheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public ChairProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "ChairProbe";
        // a check never takes the pointer from whoever is at the machine
        MouseCapture.Disabled = true;
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[chaircheck] {what}");

    private void Check(bool ok, FormattableString what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {FormattableString.Invariant(what)}");
        if (!ok) _failed++;
    }

    private async Task Press(string action, double hold = 0)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (hold > 0) await Wait(hold);
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Waits for <paramref name="ok"/>, polled every 0.1 s, at most <paramref name="seconds"/>.</summary>
    private async Task<bool> Until(Func<bool> ok, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.1)
        {
            if (ok()) return true;
            await Wait(0.1);
        }
        return ok();
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1200 && (me == null || !me.IsOnFloor()); i++)
        {
            me = _local();
            if (me == null && i % 50 == 25) await Press(PlayerInput.ToggleMode);
            await Wait(0.1);
        }
        if (me?.Terrain is not { } terrain) { Finish("no local player"); return; }

        // the terrace in front of Trois Verres: built once its tile is furnished and its roads are in
        (Vector3 Hip, Vector3 Facing)? seat = null;
        for (int i = 0; i < 600 && seat == null; i++)
        {
            var feet = me.GlobalPosition;
            foreach (var s in Interiors.ChairSeats.TerraceSeats())
                if (new Vector2(s.Hip.X - feet.X, s.Hip.Z - feet.Z).Length() < 80f
                    && (seat == null || s.Hip.DistanceTo(feet) < seat.Value.Hip.DistanceTo(feet)))
                    seat = s;
            if (seat == null) await Wait(0.2);
        }
        if (seat is not { } chair) { Finish("no terrace seat within 80 m"); return; }
        Log(FormattableString.Invariant($"a terrace seat at {chair.Hip}, facing {chair.Facing}"));

        // behind the chair, on the pavement
        var behind = chair.Hip - chair.Facing.Normalized() * 0.6f;
        if (!terrain.TryGetSurface(behind, out float ground)) ground = chair.Hip.Y - 0.47f;
        // as --sitcheck does: placed again until it stays (the spawn's drop onto open ground can still be moving it)
        for (int k = 0; k < 4 && me.SitInReach() == null; k++)
        {
            me.GlobalPosition = new Vector3(behind.X, ground + 0.3f, behind.Z);
            me.Velocity = Vector3.Zero;
            await Until(() => me.SitInReach() != null, 3);
        }
        // looking at the chair, as a player behind it does (a terrace stands in front of its venue's door, #1292: E is
        // the door when the door is what you face)
        me.LookYaw = Mathf.Atan2(-(chair.Hip.X - me.GlobalPosition.X), -(chair.Hip.Z - me.GlobalPosition.Z));
        me.LookPitch = -0.3f;
        await Wait(0.3);
        var offer = me.SitInReach();
        Check(offer != null, $"a seat in reach behind the chair: {offer} (at {me.GlobalPosition}, on floor {me.IsOnFloor()})");
        Check(me.SeatInteraction()?.Text == "Sit down", $"the prompt says Sit down ({me.SeatInteraction()?.Text})");
        Shot("terrace_standing.png");

        await Press(PlayerInput.InteractMount);
        await Until(() => me.Sitting, 2);
        Check(me.Sitting && me.PoseKind == FootPlayer.PoseSit, $"E sits down (sitting {me.Sitting}, pose {me.PoseKind})");
        float off = new Vector2(me.GlobalPosition.X - chair.Hip.X, me.GlobalPosition.Z - chair.Hip.Z).Length();
        Check(off < 0.05f, $"on the seat's place ({off:F3} m off)");
        var forward = -me.GlobalBasis.Z;
        Check(new Vector2(forward.X, forward.Z).Normalized().Dot(new Vector2(chair.Facing.X, chair.Facing.Z).Normalized()) > 0.98f,
            $"facing the table ({forward} against {chair.Facing})");
        var before = me.GlobalPosition;
        await Wait(1.0);
        Check(me.GlobalPosition.DistanceTo(before) < 0.01f, $"held still while seated");
        Shot("terrace_seated.png");

        // at a table, E is the fondue (#1384): it starts a pot on that table and does not stand up
        Check(Interiors.PotTables.Near(me.GlobalPosition) != null, $"a table at hand for a pot");
        await Press(PlayerInput.InteractMount);
        var together = Adventure.Together.TogetherService.Instance;
        await Until(() => together?.MyPot != null, 2);
        Check(together?.MyPot is { } pot && pot.Spot == Adventure.Together.PotSpot.Venue, $"E at a table starts a pot there ({together?.MyPot?.Spot})");
        Check(me.Sitting, $"E at a table does not stand up");
        // leaving the pot closes its panel, which holds the keys while open
        together?.AskLeave();
        await Until(() => !UiFocus.TextEntryActive, 2);

        // a jump does, held as long as a press lasts
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.Jump, Pressed = true });
        await Until(() => !me.Sitting, 1.5);
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.Jump, Pressed = false });
        Check(!me.Sitting && me.PoseKind != FootPlayer.PoseSit, $"a jump stands up");

        // a bot's way: sit by the method, stand by wishing to walk
        me.GlobalPosition = new Vector3(behind.X, ground + 0.3f, behind.Z);
        me.Velocity = Vector3.Zero;
        await Until(() => me.SitInReach() != null, 5);
        Check(me.TrySit(), $"TrySit, as a bot calls it");
        Vector3 wish = Vector3.Zero;
        me.WalkControls = () => (wish, false);
        await Wait(0.3);
        Check(me.Sitting, $"still seated while the wish is still");
        wish = new Vector3(0, 0, -1);
        await Wait(0.3);
        Check(!me.Sitting, $"a wish to walk stands it up");
        me.WalkControls = null;
        Finish(null);
    }

    private void Finish(string? error)
    {
        if (error != null) { Log($"RESULT: FAIL {error}"); GetTree().Quit(1); return; }
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAIL {_failed} checks");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private void Shot(string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/chairs/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
    }
}
