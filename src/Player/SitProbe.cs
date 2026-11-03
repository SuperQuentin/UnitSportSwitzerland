using System.Threading.Tasks;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--sitcheck</c> (#885), offline, on the real tiles at Stade de Genève
/// (<c>--chunks &lt;main&gt;/terrain_chunks --at 2498705,1114859</c>): the player is dropped onto a
/// row of the main stand's lower tier, finds a seat there, sits down with E (the key's own path,
/// through <c>ClientWorld</c>), is held still facing the pitch with the seated pose, stands up with
/// E; sits again and stands up by walking off, through <c>WalkControls</c>, which is how a
/// server-run bot asks. Windowed, it saves <c>test_output/stadium/sit_*.png</c>. Read the
/// <c>[sitcheck]</c> lines; the last is the RESULT.
/// </summary>
public partial class SitProbe : Node
{
    public static bool Requested => CmdArgs.Has("--sitcheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public SitProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "SitProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[sitcheck] {what}");

    private void Check(bool ok, FormattableString what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {FormattableString.Invariant(what)}");
        if (!ok) _failed++;
    }

    private static async Task Press(Node at, string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        await at.ToSignal(at.GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        await at.ToSignal(at.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1200 && (me == null || !me.IsOnFloor()); i++)
        {
            me = _local();
            // the game starts in the fly camera: walk
            if (me == null && i % 50 == 25) await Press(this, PlayerInput.ToggleMode);
            await Wait(0.1);
        }
        if (me?.Origin is not { } origin || me.Terrain is not { } terrain) { Finish("no local player"); return; }

        // down onto the row from under the roof: the spawn drop lands on the roof's top
        var spec = Terrain.Stadiums.StadiumCatalog.Geneva;
        var centre = origin.ToWorld(spec.E, spec.N, 0);
        if (!terrain.TryGetSurface(centre, out float pitch)) { Finish("no ground at the centre spot"); return; }
        // row 8 of the main (west) stand's lower tier, in a seat block (the --at of the doc comment)
        var over = origin.ToWorld(2498705.1, 1114859.2, 0);
        for (int k = 0; k < 3; k++)
        {
            me.GlobalPosition = new Vector3(over.X, pitch + 9f, over.Z);
            me.Velocity = Vector3.Zero;
            await Wait(0.2);
            for (int i = 0; i < 60 && !me.IsOnFloor(); i++) await Wait(0.1);
            await Wait(1.0);
        }
        float standY = me.GlobalPosition.Y - pitch;
        Check(me.IsOnFloor() && standY > 1.5f && standY < 8f, $"standing on the lower tier, {standY:F2} m over the pitch");

        var seat = me.SeatInReach();
        Check(seat is { Tier: 0, Row: > 0 }, $"a seat in reach: {seat}");
        Shot("sit_standing.png");

        // E sits down, by the key's own path
        await Press(this, PlayerInput.InteractMount);
        await Wait(0.5);
        Check(me.Sitting && me.PoseKind == FootPlayer.PoseSit, $"E sits down (sitting {me.Sitting}, pose {me.PoseKind})");
        if (seat is { } s)
        {
            var hip = origin.ToWorld(s.E, s.N, 0);
            float off = new Vector2(me.GlobalPosition.X - hip.X, me.GlobalPosition.Z - hip.Z).Length();
            Check(off < 0.05f, $"on the seat's place ({off:F3} m off)");
            float turn = Mathf.Abs(Mathf.AngleDifference(me.Rotation.Y, s.Yaw));
            Check(turn < 0.05f, $"facing the pitch ({Mathf.RadToDeg(turn):F1}° off)");
            // the pitch is east of the main (west) stand
            Check(Mathf.Abs(Mathf.AngleDifference(s.Yaw, -Mathf.Pi / 2)) < 0.2f, $"which is east ({Mathf.RadToDeg(s.Yaw):F0}°)");
        }
        var before = me.GlobalPosition;
        await Wait(1.5);
        Check(me.GlobalPosition.DistanceTo(before) < 0.01f, $"held still while seated");
        Shot("sit_seated.png");
        await Press(this, PlayerInput.CameraToggle);
        await Wait(1.0);
        Shot("sit_seated_other_view.png");
        await Press(this, PlayerInput.CameraToggle);

        // E again stands up, on the row
        await Press(this, PlayerInput.InteractMount);
        await Wait(0.8);
        Check(!me.Sitting && me.PoseKind != FootPlayer.PoseSit && me.IsOnFloor(), $"E stands up onto the row (on floor {me.IsOnFloor()})");

        // a bot's way: sit by the method, stand by wishing to walk (its WalkControls)
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
        string path = ProjectSettings.GlobalizePath("res://test_output/stadium/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
    }
}
