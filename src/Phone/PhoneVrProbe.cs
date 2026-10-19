using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.XR;

namespace UnitSport.Phone;

/// <summary>
/// <c>--phonevrcheck --xrsim --world fixture</c> (#920): the phone in VR with a scripted hand and
/// fingertip (<see cref="XrPhone.SimHold"/>, <see cref="XrPhone.SimGrip"/>, <see cref="XrPhone.SimTip"/>;
/// no controller is tracked under --xrsim). A grip at the front pocket takes it out into the hand;
/// held up before the eyes, the fingertip taps the cover's quick action (the camera app opens), B goes
/// back, a tap on the home screen's first tile opens the map; a teleport goes behind the blink and
/// the phone goes back to the pocket there; the wrist menu's way takes it out without a grip, X opens
/// it, B puts it away; letting go of the grip does too. The game's camera never moves. Windowed it
/// saves <c>test_output/phonevr_*.png</c>. Read the <c>[phonevrcheck]</c> lines.
/// </summary>
public partial class PhoneVrProbe : Node
{
    public static bool Requested => CmdArgs.Has("--phonevrcheck");

    private readonly Func<FootPlayer?> _local;
    private int _failed;

    public PhoneVrProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "PhoneVrProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[phonevrcheck] {what}");

    private void Check(bool ok, string what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failed++;
    }

    private Transform3D Head => XrSession.Anchor?.GlobalTransform ?? Transform3D.Identity;

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            if (me == null && i % 50 == 25)
                foreach (bool down in new[] { true, false })
                    Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = down });
            await Wait(0.1);
        }
        var phone = PhoneController.Instance;
        if (me == null || phone == null || !XrSession.Active) { Log("RESULT: FAIL no player, no phone or no VR (--xrsim)"); GetTree().Quit(1); return; }
        await Wait(1.0);

        // a grip closing at the left front pocket takes it out into the hand
        var head = Head;
        XrPhone.SimHold = head * new Transform3D(Basis.Identity, new Vector3(-0.2f, -0.78f, -0.08f));
        XrPhone.SimGrip = true;
        await Wait(0.4);
        Check(phone.IsOut && me.PhoneState == FootPlayer.PhoneFoldedState, $"a grip at the front pocket takes it out (state {me.PhoneState})");
        var lens = me.CameraOrNull?.GlobalTransform;

        // held up before the eyes, the screen to them
        HoldBeforeEyes();
        await Wait(0.8);
        float off = me.PhoneModel is { Visible: true } m ? m.GlobalPosition.DistanceTo(Head * new Vector3(-0.02f, -0.16f, -0.32f)) : -1f;
        Check(off is >= 0f and < 0.08f, $"it is in the hand, before the eyes ({off:F2} m off)");
        Shot("phonevr_1_folded.png");

        // the fingertip taps the cover's quick action: the camera app
        await Tap(phone.Screens.CoverCardUv(), cover: true);
        await Wait(1.4);
        Check(me.PhoneState == FootPlayer.PhoneCameraState, $"a tap on the cover runs its quick action, the camera app (state {me.PhoneState})");
        Shot("phonevr_2_camera.png");
        await Button(JoyButton.B, 0.6);
        Check(me.PhoneState == FootPlayer.PhoneOpenState && phone.Screens.Shown == PhoneScreens.Page.Home, "B goes back to the home screen");

        // a tap on the home screen's first tile: the map
        await Wait(0.3);
        if (phone.Screens.FocusUv() is { } tile)
        {
            await Tap(tile, cover: false);
            await Wait(0.5);
        }
        Check(phone.Screens.Shown == PhoneScreens.Page.Map, $"a tap on a tile opens it ({phone.Screens.Shown})");
        Shot("phonevr_3_map.png");
        float drift = me.CameraOrNull?.GlobalTransform is { } now && lens is { } then ? now.Origin.DistanceTo(then.Origin) : -1f;
        Check(drift is >= 0f and < 0.05f, $"the game's camera never moved (the eye is the head's; {drift:F2} m)");

        // a teleport while it is open: behind the blink, then back in the pocket at the new place
        var at = me.Global;
        Teleporter.Instance?.TeleportTo(at.E + 250, at.N, "probe vr");
        for (int i = 0; i < 300 && phone.IsOut; i++) await Wait(0.1);
        var moved = me.Global;
        Check(Math.Abs(moved.E - (at.E + 250)) < 5, $"travelled behind the blink ({moved.E - at.E:F0} m)");
        Check(!phone.IsOut, "and back in the pocket there");

        // the wrist menu's way: no grip, held all the same; X opens it, B puts it away from folded
        XrPhone.SimGrip = false;
        await Wait(0.3);
        XrPad.Tap(PlayerInput.Phone);
        await Wait(0.8);
        Check(phone.IsOut, "the wrist menu's Phone takes it out without a grip");
        HoldBeforeEyes();
        await Button(JoyButton.X, 1.0);
        Check(me.PhoneState == FootPlayer.PhoneOpenState, "X opens it");
        Shot("phonevr_4_open.png");
        await Button(JoyButton.X, 0.8);
        Check(me.PhoneState == FootPlayer.PhoneFoldedState, "X folds it");
        await Button(JoyButton.B, 0.8);
        Check(!phone.IsOut, "B puts the folded phone away");

        // out by the pocket again, and letting go of the grip puts it back
        XrPhone.SimHold = Head * new Transform3D(Basis.Identity, new Vector3(-0.2f, -0.78f, -0.08f));
        XrPhone.SimGrip = true;
        await Wait(0.4);
        XrPhone.SimGrip = false;
        await Wait(1.2);
        Check(!phone.IsOut, "letting go of the grip puts it back in the pocket");

        XrPhone.SimHold = null;
        XrPhone.SimTip = null;
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAIL {_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>The hand held so the phone sits 32 cm before the eyes, a little low and left, its screen to them.</summary>
    private void HoldBeforeEyes()
    {
        var head = Head;
        var at = head * new Vector3(-0.02f, -0.16f, -0.32f);
        var z = (head.Origin - at).Normalized();
        var y = (Vector3.Up - z * z.Dot(Vector3.Up)).Normalized();
        var phone = new Transform3D(new Basis(y.Cross(z), y, z), at);
        XrPhone.SimHold = phone * XrPhone.InPalm(leftHanded: false).AffineInverse();
    }

    /// <summary>The fingertip: over the point, onto the glass, and off again.</summary>
    private async Task Tap(Vector2 uv, bool cover)
    {
        var me = _local();
        if (me == null) return;
        foreach (float lift in new[] { 0.02f, 0.002f, 0.02f })
        {
            XrPhone.SimTip = me.PhoneScreenToWorld(uv, cover, lift);
            await Wait(0.15);
        }
        XrPhone.SimTip = null;
        await Wait(0.1);
    }

    private async Task Button(JoyButton button, double after)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = down, Device = 7 });
        await Wait(after);
    }

    private void Shot(string file)
    {
        if (DisplayServer.GetName() == "headless") return;
        string path = ProjectSettings.GlobalizePath("res://test_output/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
    }
}
