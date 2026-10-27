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
/// first. Windowed, it saves <c>test_output/phone_*.png</c> and checks the camera's shots (headless draws no
/// frame to shoot, so those are skipped there). Read the <c>[phonecheck]</c> lines.
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

        // the camera's own zoom (#1149): Shift + wheel in to the face, the hotbar left as it was; - back out
        if (me.ZoomsView)
        {
            int inHand = me.HeldItemId;
            for (int i = 0; i < 12; i++) await Wheel(MouseButton.WheelUp, shift: true, 0.05);
            await Wait(0.8);
            Check(me.ViewZoom < 0.7f, $"Shift + wheel zooms in to the face ({me.ViewZoom:F2} m)");
            Check(me.HeldItemId == inHand, $"and does not step the hotbar ({inHand} -> {me.HeldItemId})");
            Shot("phone_0b_zoom_back.png");
            me.LookYaw += Mathf.Pi;
            await Wait(0.6);
            Shot("phone_0c_zoom_face.png");
            me.LookYaw -= Mathf.Pi;
            for (int i = 0; i < 9; i++) await Key(Godot.Key.Minus, 0.05);
            await Wait(0.8);
            Check(me.ViewZoom > 2.8f, $"- zooms back out ({me.ViewZoom:F2} m)");
        }
        else Log("skip (first person: the zoom is third person's)");

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
        // the body stands while the phone is out: D is the phone's (it picks the next quick action)
        var stood = me.GlobalPosition;
        await Key(Godot.Key.D, 0.4);
        Check(me.GlobalPosition.DistanceTo(stood) < 0.05f, $"D picks a quick action, the body does not walk ({me.GlobalPosition.DistanceTo(stood):F2} m)");
        Shot("phone_3_quick.png");
        Save(phone.Screens.Cover, "phone_3_cover.png");

        // open: the hinge, the camera onto the screen, then the full-screen picture
        await Press(PlayerInput.Phone, 0.25);
        Shot("phone_4_opening.png");
        await Wait(1.0);
        Check(phone.IsCovering, "Up again opens it and the screen covers the view");
        Check(me.PhoneState == FootPlayer.PhoneOpenState, $"open on the wire (state {me.PhoneState})");
        Check(me.PhoneCover == 0f, "home is seen in the hands, not square on");
        // the cursor over the screen: the free hand's index finger comes to point at it
        var mid = me.CameraOrNull is { } eye && me.PhoneModel is { } pm
            ? eye.UnprojectPosition(pm.GlobalTransform * PhoneModel.InnerScreenCentre) : GetViewport().GetVisibleRect().Size / 2f;
        // a parsed event is in the window's pixels: the stretched canvas's units scaled up
        var atWindow = GetViewport().GetScreenTransform() * mid;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = atWindow, GlobalPosition = atWindow });
        await Wait(0.4);
        Check(me.PhonePointTip != null, $"the cursor on the screen: the index finger points at it (cursor at {phone.CursorUv?.ToString() ?? "none"}, mouse {mid})");
        Log($"finger: tip {me.PhonePointTip}, built {me.DrawnPoint}, body {me.GlobalPosition}, phone {me.PhoneModel?.GlobalPosition}");
        Shot("phone_5_open.png");
        await Key(Godot.Key.S, 0.4);
        Shot("phone_5b_swipe.png");

        // the Activities app, its own page on the phone
        phone.Open(PhoneScreens.Page.Activities);
        await Wait(0.6);
        Check(phone.Screens.Shown == PhoneScreens.Page.Activities, "the Activities app opens on the phone");
        Shot("phone_5c_activities.png");
        await Key(Godot.Key.Escape, 0.4);
        Check(phone.Screens.Shown == PhoneScreens.Page.Home, "Esc goes back from an app to home (#1253)");
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.4);
        await Key(Godot.Key.Backspace, 0.4);
        Check(phone.Screens.Shown == PhoneScreens.Page.Home, "Backspace goes back too (#1253)");
        phone.Open(PhoneScreens.Page.Training);
        await Wait(0.4);
        await Key(Godot.Key.Escape, 0.4);
        Check(phone.IsOut && phone.Screens.Shown == PhoneScreens.Page.Home, "Esc from Training goes up one level, it does not fold the phone (#1253)");
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.4);
        await Hold(Godot.Key.Escape, PhoneController.StowHoldSeconds + 0.6);
        for (int i = 0; i < 30 && phone.IsOut; i++) await Wait(0.1);
        Check(!phone.IsOut, "holding Esc puts the phone away from any page (#1253)");
        await Press(PlayerInput.Phone, 1.0);
        phone.Open(PhoneScreens.Page.Home);
        await Wait(0.6);

        // the map: closer first, then the world fades to black behind the phone
        phone.Open(PhoneScreens.Page.Map);
        await Wait(0.3);
        Check(me.PhoneBackdropShown < 0.5f, $"on the way in, the world still shows ({me.PhoneBackdropShown:F2})");
        await Wait(1.2);
        Check(me.PhoneCover >= 1f && me.PhoneBackdropShown >= 1f, $"the map square on, the world black behind ({me.PhoneCover:F2}, {me.PhoneBackdropShown:F2})");
        Shot("phone_5d_map.png");
        await Key(Godot.Key.Escape, 1.0);

        // the camera app: raised before the face, the lens out of its back; Enter shoots into the album
        phone.Open(PhoneScreens.Page.Camera);
        await Wait(1.0);
        Check(me.PhoneState == FootPlayer.PhoneCameraState, $"the camera app raises it (state {me.PhoneState}, arms {me.DrawnArmPose})");
        if (me.PhoneModel is { } raised)
        {
            float tilt = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(raised.GlobalBasis.Z.Normalized().Y, -1f, 1f)));
            Check(Mathf.Abs(tilt - Mathf.RadToDeg(me.LookPitch)) < 12f, $"raised level with the view, the horizon level ({tilt:F0}° against a {Mathf.RadToDeg(me.LookPitch):F0}° pitch)");
        }
        Shot("phone_8_camera.png");
        // open on the camera app the body walks (#1149): W held carries it forward, the phone still up
        var still = me.GlobalPosition;
        await Hold(Godot.Key.W, 1.0);
        await Wait(0.3);
        Check(me.GlobalPosition.DistanceTo(still) > 0.5f, $"W walks with the camera app open ({me.GlobalPosition.DistanceTo(still):F2} m)");
        Check(me.PhoneState == FootPlayer.PhoneCameraState, $"and the camera app stays up (state {me.PhoneState})");
        await Key(Godot.Key.Enter, 1.5);
        Shoots(phone.LastPhoto != null && Items.PhotoStore.Has(phone.LastPhoto), $"Enter takes a photo into the album ({phone.LastPhoto})");
        Save(phone.Screens.Inner, "phone_8_camera_screen.png");

        // a selfie, the foldable's way: F turns the open phone round, the main camera films the player, the cover shows it
        string? first = phone.LastPhoto;
        await Key(Godot.Key.F, 1.0);
        Check(me.PhoneState == FootPlayer.PhoneSelfieState, $"F turns the phone round for a selfie (state {me.PhoneState})");
        Shot("phone_9_selfie.png");
        await Key(Godot.Key.Enter, 1.5);
        Shoots(phone.LastPhoto != null && phone.LastPhoto != first, $"Enter takes the selfie ({phone.LastPhoto})");
        if (phone.LastPhoto is { } selfie && Items.PhotoStore.Meta(selfie)?.Source is { Length: > 0 } src && Image.LoadFromFile(src) is { } taken)
        {
            float shot = (float)taken.GetWidth() / taken.GetHeight(), cover = PhoneModel.CoverSize.X / PhoneModel.CoverSize.Y;
            Check(Mathf.Abs(shot - cover) < 0.03f, $"the selfie is cropped to what the cover showed ({taken.GetWidth()}x{taken.GetHeight()}, {shot:F2} against {cover:F2})");
        }
        Save(phone.Screens.Cover, "phone_9_selfie_cover.png");
        await Key(Godot.Key.F, 0.8);
        Check(me.PhoneState == FootPlayer.PhoneCameraState, "F turns it back");
        await Key(Godot.Key.Escape, 0.8);
        Check(me.PhoneState == FootPlayer.PhoneOpenState, "Esc goes back to the home screen");

        // the Photos app: the shots, one opened, set as the home screen with the keys
        phone.Open(PhoneScreens.Page.Photos);
        await Wait(0.6);
        Check(phone.Screens.Shown == PhoneScreens.Page.Photos, "the Photos app opens");
        Shot("phone_10_photos.png");
        if (Rendered)
        {
            await Key(Godot.Key.Enter, 0.5);
            Check(phone.Screens.Shown == PhoneScreens.Page.Photo, "Enter opens the newest photo");
            await Key(Godot.Key.D, 0.3);
            Shot("phone_11_photo.png");
            await Key(Godot.Key.Enter, 0.5);
            Check(phone.Wallpapers.Home == phone.LastPhoto, $"set as the home screen ({phone.Wallpapers.Home})");
            await Key(Godot.Key.Escape, 0.4);
        }
        else Log("skip (headless: no shot to open or set as a wallpaper)");
        await Key(Godot.Key.Escape, 0.6);
        Shot("phone_12_home_wallpaper.png");

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

    /// <summary>Headless draws no frame, so the camera has nothing to shoot: its checks run windowed only (as the Polaroid's).</summary>
    private static bool Rendered => DisplayServer.GetName() != "headless";

    private void Shoots(bool ok, string what)
    {
        if (Rendered) Check(ok, what);
        else Log($"skip (headless: no frame to shoot): {what}");
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

    private async Task Hold(Key key, double seconds)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await Wait(seconds);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
    }

    private async Task Wheel(MouseButton button, bool shift, double after)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = down, ShiftPressed = shift });
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
