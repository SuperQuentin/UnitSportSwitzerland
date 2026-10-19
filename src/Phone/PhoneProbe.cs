using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Phone;

/// <summary>
/// <c>--phonecheck</c> (#889), offline, <c>--world fixture</c>: the phone driven with real input
/// events. Up takes it out (the hands and the camera come to it), Right picks a quick action, Up
/// opens it until the screen covers the view and is shown full screen; a teleport then runs behind
/// it, and the phone folds away at the new place. Taken out folded, a teleport waits for the screen
/// first. Windowed, it saves <c>test_output/phone_*.png</c>. Read the <c>[phonecheck]</c> lines.
/// </summary>
public partial class PhoneProbe : Node
{
    public static bool Requested => CmdArgs.Has("--phonecheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public PhoneProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "PhoneProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[phonecheck] {what}");

    private void Check(bool ok, string what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failed++;
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            if (me == null && i % 50 == 25) await Press(PlayerInput.ToggleMode, 0.05);
            await Wait(0.1);
        }
        var phone = PhoneController.Instance;
        if (me == null || phone == null) { Log("RESULT: FAIL no local player or no phone"); GetTree().Quit(1); return; }
        await Wait(1.0);
        Shot("phone_0_before.png");

        // out of the pocket, folded: in both hands, the camera over the shoulder
        await Press(PlayerInput.Phone, 0.3);
        Check(phone.IsOut, "Up takes the phone out");
        Shot("phone_1_drawing.png");
        await Wait(0.8);
        Check(me.PhoneState == FootPlayer.PhoneFoldedState, $"folded in the hands (state {me.PhoneState})");
        Check(me.PhoneModel is { Visible: true }, "the phone is drawn in the hands");
        Check(me.DrawnArmPose == Avatar.ItemArmPose.Phone, $"arms in the phone pose ({me.DrawnArmPose})");
        Check(me.PhoneLook >= 1f, $"the camera has come over the shoulder ({me.PhoneLook:F2})");
        Shot("phone_2_folded.png");
        await Press("ui_right", 0.3);
        Shot("phone_3_quick.png");
        Save(phone.Screens.Cover, "phone_3_cover.png");

        // open: the hinge, the camera onto the screen, then the full-screen picture
        await Press(PlayerInput.Phone, 0.25);
        Shot("phone_4_opening.png");
        await Wait(1.0);
        Check(phone.IsCovering, "Up again opens it and the screen covers the view");
        Check(me.PhoneState == FootPlayer.PhoneOpenState, $"open on the wire (state {me.PhoneState})");
        Shot("phone_5_open.png");

        // the camera app: raised before the face, the lens out of its back; Enter shoots into the album
        phone.Open(PhoneScreens.Page.Camera);
        await Wait(1.0);
        Check(me.PhoneState == FootPlayer.PhoneCameraState, $"the camera app raises it (state {me.PhoneState}, arms {me.DrawnArmPose})");
        Shot("phone_8_camera.png");
        await Key(Godot.Key.Enter, 1.5);
        Check(phone.LastPhoto != null && Items.PhotoStore.Has(phone.LastPhoto), $"Enter takes a photo into the album ({phone.LastPhoto})");
        Save(phone.Screens.Inner, "phone_8_camera_screen.png");
        await Key(Godot.Key.Escape, 0.8);
        Check(me.PhoneState == FootPlayer.PhoneOpenState, "Esc goes back to the home screen");

        // travel while open: behind the phone once the world is dark, and the phone folds away there
        var from = me.GlobalPosition;
        var at = me.Global;
        Teleporter.Instance?.TeleportTo(at.E + 300, at.N + 40, "probe east");
        await Wait(0.5);
        Shot("phone_6_travelling.png");
        Check(phone.IsOut, "still on the screen while the ground there streams in");
        for (int i = 0; i < 300 && phone.IsOut; i++) await Wait(0.1);
        var moved = me.Global;
        Check(!phone.IsOut, "back in the pocket after the travel");
        Check(Math.Abs(moved.E - (at.E + 300)) < 5 && Math.Abs(moved.N - (at.N + 40)) < 5, $"moved to the place ({moved.E - at.E:F0}, {moved.N - at.N:F0})");
        Check(me.PhoneState == FootPlayer.PhonePocketState, "pocket on the wire");
        Shot("phone_7_arrived.png");
        _ = from;

        // a teleport asked while folded waits for the screen, then goes behind it
        await Wait(0.5);
        await Press(PlayerInput.Phone, 1.0);
        var before = me.Global;
        Teleporter.Instance?.TeleportTo(before.E - 250, before.N, "probe back");
        await Wait(0.15);
        var held = me.Global;
        Check(Math.Abs(held.E - before.E) < 2, $"held until the screen covers the view ({held.E - before.E:F1} m)");
        for (int i = 0; i < 300 && phone.IsOut; i++) await Wait(0.1);
        var back = me.Global;
        Check(Math.Abs(back.E - (before.E - 250)) < 5, $"then moved behind it ({back.E - before.E:F0} m)");
        Check(!phone.IsOut, "and put away");

        // Esc puts a folded phone away
        await Press(PlayerInput.Phone, 1.0);
        await Key(Godot.Key.Down, 1.0);
        Check(!phone.IsOut, "Down puts the folded phone away");

        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAIL {_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    private async Task Press(string action, double after)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = down });
        await Wait(after);
    }

    private async Task Key(Key key, double after)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = down });
        await Wait(after);
    }

    /// <summary>One of the phone's screens as it is drawn, into test_output/ (windowed).</summary>
    private static void Save(SubViewport screen, string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/" + file);
        screen.GetTexture().GetImage().SavePng(path);
        Log($"screen {path}");
    }

    /// <summary>A screenshot into test_output/ when windowed (headless has no image).</summary>
    private void Shot(string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
    }
}
