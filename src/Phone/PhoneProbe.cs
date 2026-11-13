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

        // the camera's own zoom (#1149, #1369): the plain wheel zooms whatever is held (Shift too: it is sprint),
        // in to the face, the hotbar left as it was; - back out
        await HolsterChecks(me);
        if (me.ZoomsView)
        {
            int inHand = me.HeldItemId;
            for (int i = 0; i < 12; i++) await Wheel(MouseButton.WheelUp, shift: true, 0.05);
            await Wait(0.8);
            Check(me.ViewZoom < 0.7f, $"wheel with Shift held zooms in to the face ({me.ViewZoom:F2} m)");
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
        double pressed = GameClock.Now;
        await Press(PlayerInput.Phone, 0);
        Check(phone.IsOut, "Up takes the phone out");
        for (int i = 0; i < 100 && !phone.IsFolded; i++) await Wait(0.01);
        double drawn = GameClock.Now - pressed;
        Check(phone.IsFolded && drawn < 0.35, $"the first press ends folded in {drawn:F2} s (#1825)");
        Shot("phone_1_drawing.png");
        await Wait(0.8);
        Check(me.PhoneState == FootPlayer.PhoneFoldedState, $"folded in the hands (state {me.PhoneState})");
        Check(me.PhoneModel is { Visible: true }, "the phone is drawn in the hands");
        Check(me.DrawnArmPose == Avatar.ItemArmPose.Phone, $"arms in the phone pose ({me.DrawnArmPose})");
        Check(me.PhoneLook >= 1f, $"the camera has come over the shoulder ({me.PhoneLook:F2})");
        Shot("phone_2_folded.png");
        // the body walks while the phone is out (#1825): D carries it, the arrows pick (touch keeps the body still)
        var stood = me.GlobalPosition;
        await Key(Godot.Key.Right, 0.3);
        if (Platform.IsTouch) await Key(Godot.Key.D, 0.4);
        else await HoldShot(Godot.Key.D, 0.8, "phone_1825/folded_walking.png");
        float walkedFolded = me.GlobalPosition.DistanceTo(stood);
        Check(Platform.IsTouch ? walkedFolded < 0.05f : walkedFolded > 0.4f, $"D walks the body with the phone folded, the arrows pick ({walkedFolded:F2} m)");
        Check(me.PhoneState == FootPlayer.PhoneFoldedState, "and it stays folded while walking");
        Shot("phone_3_quick.png");
        Save(phone.Screens.Cover, "phone_3_cover.png");

        // open: the hinge, the camera onto the screen, then the full-screen picture
        pressed = GameClock.Now;
        await Press(PlayerInput.Phone, 0);
        for (int i = 0; i < 100 && !phone.IsCovering; i++) await Wait(0.01);
        double opened = GameClock.Now - pressed;
        Check(phone.IsCovering && opened < 0.45, $"the second press ends open in {opened:F2} s (#1825)");
        Shot("phone_4_opening.png");
        await Wait(1.0);
        Check(phone.IsCovering, "Up again opens it and the screen covers the view");
        Check(phone.Screens.Shown == PhoneScreens.Page.Home, $"the first time it opens on Home ({phone.Screens.Shown})");
        Check(me.PhoneState == FootPlayer.PhoneOpenState, $"open on the wire (state {me.PhoneState})");
        if (Platform.IsTouch) Check(me.PhoneCover > 0f, $"touch: Home is seen square on, enlarged ({me.PhoneCover:F2})");
        else Check(me.PhoneCover == 0f, "home is seen in the hands, not square on");
        // the cursor over the screen: the free hand's index finger comes to point at it
        var mid = me.CameraOrNull is { } eye && me.PhoneModel is { } pm
            ? eye.UnprojectPosition(pm.GlobalTransform * PhoneModel.InnerScreenCentre) : GetViewport().GetVisibleRect().Size / 2f;
        // a parsed event is in the window's pixels: the stretched canvas's units scaled up
        var atWindow = GetViewport().GetScreenTransform() * mid;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = atWindow, GlobalPosition = atWindow });
        await Wait(0.4);
        Check(me.PhonePointTip != null || Platform.IsTouch, $"the cursor on the screen: the index finger points at it (cursor at {phone.CursorUv?.ToString() ?? "none"}, mouse {mid})");
        Log($"finger: tip {me.PhonePointTip}, built {me.DrawnPoint}, body {me.GlobalPosition}, phone {me.PhoneModel?.GlobalPosition}");
        Shot("phone_5_open.png");
        Save(phone.Screens.Inner, "phone_5_open_screen.png");
        await Key(Godot.Key.Down, 0.4);
        Shot("phone_5b_swipe.png");

        // the Activities app, its own page on the phone
        phone.Open(PhoneScreens.Page.Activities);
        await Wait(0.6);
        Check(phone.Screens.Shown == PhoneScreens.Page.Activities, "the Activities app opens on the phone");
        Shot("phone_5c_activities.png");
        Save(phone.Screens.Inner, "phone_5c_activities_screen.png");
        await Key(Godot.Key.Escape, 0.4);
        Check(phone.Screens.Shown == PhoneScreens.Page.Home, "Esc goes back from an app to home (#1253)");
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.4);
        await Key(Godot.Key.Backspace, 0.4);
        Check(phone.Screens.Shown == PhoneScreens.Page.Home, "Backspace goes back too (#1253)");
        phone.Open(PhoneScreens.Page.Training);
        await Wait(0.4);
        await Key(Godot.Key.Escape, 0.4);
        Check(phone.IsOut && phone.Screens.Shown == PhoneScreens.Page.Activities, "Esc from Training goes up to Activities, it does not fold the phone (#1253)");
        await Key(Godot.Key.Escape, 0.4);
        Check(phone.IsOut && phone.Screens.Shown == PhoneScreens.Page.Home, "and once more to the home screen (#1253)");
        Check(phone.Screens.HomeTileCount == 6, $"the home screen has six apps ({phone.Screens.HomeTileCount}); Menu and Training live in Activities (#1253)");
        await ContactsChecks(phone);
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.4);
        await Hold(Godot.Key.Escape, PhoneController.StowHoldSeconds + 0.6);
        for (int i = 0; i < 30 && phone.IsOut; i++) await Wait(0.1);
        Check(!phone.IsOut, "holding Esc puts the phone away from any page (#1253)");
        await Press(PlayerInput.Phone, 1.0);
        phone.Open(PhoneScreens.Page.Home);
        await Wait(0.6);

        // the map is in the hands like every app (#1825): no dolly square on, no black behind, the world visible
        phone.Open(PhoneScreens.Page.Map);
        await Wait(1.5);
        if (Platform.IsTouch) Check(me.PhoneCover >= 1f && me.PhoneBackdropShown >= 1f, $"touch: the map is seen square on ({me.PhoneCover:F2}, {me.PhoneBackdropShown:F2})");
        else Check(me.PhoneCover == 0f && me.PhoneBackdropShown == 0f, $"the map is in the hands, the world shows ({me.PhoneCover:F2}, {me.PhoneBackdropShown:F2})");
        Shot("phone_5d_map.png");
        Save(phone.Screens.Inner, "phone_5d_map_screen.png");
        Check(phone.Screens.MapRowCount > 0 || !phone.Screens.HasPlaces, $"the Map lists places without typing ({phone.Screens.MapRowCount} rows) (#1253)");
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

        // the camera's own buttons (#1253): zoom chips with the current lens lit, a focusable shutter and Selfie
        Check(phone.Screens.ShutterButton.FocusMode == Control.FocusModeEnum.All, "the shutter button can take focus");
        Check(phone.Screens.LitChip == 0 && phone.Screens.Zoom == 1f, "the 1x chip is lit to start with");
        phone.CameraTap(phone.Screens.ZoomChipUv(2));
        await Wait(0.2);
        Check(phone.Screens.Zoom == 5f && phone.Screens.LitChip == 2, $"a tap on the 5x chip zooms to 5x and lights it ({phone.Screens.Zoom}x, chip {phone.Screens.LitChip})");
        await Key(Godot.Key.Down, 0.2);
        Check(phone.Screens.Zoom == 2f && phone.Screens.LitChip == 1, $"Down steps the zoom and moves the lit chip ({phone.Screens.Zoom}x, chip {phone.Screens.LitChip})");
        phone.CameraTap(phone.Screens.ZoomChipUv(0));
        await Wait(0.2);
        Check(phone.Screens.Zoom == 1f && phone.Screens.LitChip == 0, "the 1x chip puts it back");
        // a finger on the picture itself does not shoot
        string? beforeTap = phone.LastPhoto;
        await FingerTap(me, new Vector2(0.3f, 0.5f), cover: false);
        await Wait(1.0);
        Check(phone.LastPhoto == beforeTap && !phone.Selfie, "a tap on the glass does not shoot (touch)");
        phone.CameraTap(phone.Screens.SelfieUv());
        await Wait(0.8);
        Check(phone.Selfie, "the Selfie button turns the phone round");
        await Key(Godot.Key.F, 0.8);
        Check(!phone.Selfie, "and F turns it back");
        await Key(Godot.Key.Escape, 0.8);
        Check(me.PhoneState == FootPlayer.PhoneOpenState, "Esc goes back to the home screen");

        // the Photos app: the shots, one opened, set as the home screen with the keys
        phone.Open(PhoneScreens.Page.Photos);
        await Wait(0.6);
        Check(phone.Screens.Shown == PhoneScreens.Page.Photos, "the Photos app opens");
        Shot("phone_10_photos.png");
        Save(phone.Screens.Inner, "phone_10_photos_screen.png");
        // the selfie key means nothing outside the camera app
        await Key(Godot.Key.F, 0.3);
        await Key(Godot.Key.Tab, 0.3);
        Check(!phone.Selfie && phone.Screens.Shown == PhoneScreens.Page.Photos, "F and Tab are not a selfie outside the camera");
        if (Rendered)
        {
            int count = phone.Screens.GridCount;
            Check(count >= 2, $"the album has the shots ({count})");
            Pad(JoyButton.RightShoulder);
            await Wait(0.3);
            Check(phone.Screens.GridFocus == count - 1, $"RB pages the grid down ({phone.Screens.GridFocus} of {count})");
            Pad(JoyButton.LeftShoulder);
            await Wait(0.3);
            Check(phone.Screens.GridFocus == 0, $"LB pages it back up ({phone.Screens.GridFocus})");
            await Key(Godot.Key.Enter, 0.5);
            Check(phone.Screens.Shown == PhoneScreens.Page.Photo, "Enter opens the newest photo");
            string? newest = phone.Screens.PhotoShown;
            await Key(Godot.Key.Right, 0.3);
            Check(phone.Screens.PhotoShown != newest && phone.Screens.PhotoShown != null, "Right shows the next photo");
            await Key(Godot.Key.Left, 0.3);
            Check(phone.Screens.PhotoShown == newest, "Left shows the previous one");
            Pad(JoyButton.RightShoulder);
            await Wait(0.3);
            Check(phone.Screens.PhotoShown != newest, "RB is next in the viewer");
            Pad(JoyButton.LeftShoulder);
            await Wait(0.3);
            Check(phone.Screens.PhotoShown == newest && phone.Screens.Shown == PhoneScreens.Page.Photo, "LB is previous");
            await Key(Godot.Key.Down, 0.3);
            Shot("phone_11_photo.png");
            Save(phone.Screens.Inner, "phone_11_photo_screen.png");
            await Key(Godot.Key.Enter, 0.5);
            Check(phone.Wallpapers.Home == phone.LastPhoto, $"set as the home screen ({phone.Wallpapers.Home})");
            await Key(Godot.Key.Escape, 0.4);
        }
        else Log("skip (headless: no shot to open or set as a wallpaper)");
        await Key(Godot.Key.Escape, 0.6);
        Shot("phone_12_home_wallpaper.png");

        // Esc on the curtain cancels a travel that has not jumped yet
        var stay = me.Global;
        Teleporter.Instance?.TeleportTo(stay.Moved(500, 0), "probe cancelled");
        await Wait(0.1);
        Check(phone.Screens.Shown == PhoneScreens.Page.Travel, "the curtain is up while the jump waits (#1253)");
        await Key(Godot.Key.Escape, 2.5);
        var stayed = me.Global;
        Check(Math.Abs(stay.OffsetTo(stayed).East) < 2 && phone.IsOut && phone.Screens.Shown == PhoneScreens.Page.Home, "Esc on the curtain cancels the travel and stays on the phone (#1253)");

        await FoldedFirstChecks(me, phone);
        await WalkingChecks(me, phone);
        await FingerChecks(me, phone);

        // travel while open: behind the phone once the world is dark, and the phone folds away there
        var from = me.GlobalPosition;
        var at = me.Global;
        Teleporter.Instance?.TeleportTo(at.Moved(300, 40), "probe east");
        await Wait(0.1);
        Check(me.Global.OffsetTo(at).East is > -2 and < 2, "the jump is held while the curtain comes down (#1825)");
        for (int i = 0; i < 1500 && Math.Abs(me.Global.OffsetTo(at).East) < 20; i++) await Wait(0.02);
        Check(Math.Abs(me.Global.OffsetTo(at).East) >= 20 && me.PhoneBackdropShown >= 0.99f, $"and runs only once the world is black ({me.PhoneBackdropShown:F2}) (#1825)");
        await Wait(0.3);
        Shot("phone_6_travelling.png");
        Check(phone.IsOut, "still on the screen while the ground there streams in");
        for (int i = 0; i < 300 && phone.IsOut; i++) await Wait(0.1);
        var moved = me.Global;
        Check(!phone.IsOut, "back in the pocket after the travel");
        var (offE, offN, _) = at.Moved(300, 40).OffsetTo(moved);
        var (movedE, movedN, _) = at.OffsetTo(moved);
        Check(Math.Abs(offE) < 5 && Math.Abs(offN) < 5, $"moved to the place ({movedE:F0}, {movedN:F0})");
        Check(me.PhoneState == FootPlayer.PhonePocketState, "pocket on the wire");
        Shot("phone_7_arrived.png");
        _ = from;

        // a teleport asked while folded waits for the screen, then goes behind it
        await Wait(0.5);
        await Press(PlayerInput.Phone, 1.0);
        var before = me.Global;
        Teleporter.Instance?.TeleportTo(before.Moved(-250, 0), "probe back");
        await Wait(0.15);
        var held = me.Global;
        var (heldE, _, _) = before.OffsetTo(held);
        Check(Math.Abs(heldE) < 2, $"held until the screen covers the view ({heldE:F1} m)");
        for (int i = 0; i < 300 && phone.IsOut; i++) await Wait(0.1);
        var back = me.Global;
        var (backOff, _, _) = before.Moved(-250, 0).OffsetTo(back);
        var (backE, _, _) = before.OffsetTo(back);
        Check(Math.Abs(backOff) < 5, $"then moved behind it ({backE:F0} m)");
        Check(!phone.IsOut, "and put away");

        // Esc puts a folded phone away
        await Press(PlayerInput.Phone, 1.0);
        await Key(Godot.Key.Down, 1.0);
        Check(!phone.IsOut, "Down puts the folded phone away");

        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAIL {_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>A key held for <paramref name="seconds"/>, a screenshot taken half way (the body walking), then let go.</summary>
    private async Task HoldShot(Key key, double seconds, string shot)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await Wait(seconds * 0.5);
        Shot(shot);
        await Wait(seconds * 0.5);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
    }

    private static void Mouse(MouseButton button, bool down) =>
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = down });

    /// <summary>
    /// Folded first, then the last app (#1825): the second press opens on the app last used this session, a digit on the
    /// cover runs its tile straight into that app.
    /// </summary>
    private async Task FoldedFirstChecks(FootPlayer me, PhoneController phone)
    {
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.6);
        Check(phone.LastApp == PhoneScreens.Page.Contacts, $"the last app is the Contacts ({phone.LastApp})");
        phone.PutAway();
        for (int i = 0; i < 60 && phone.IsOut; i++) await Wait(0.1);
        Check(!phone.IsOut, "put away");
        double t0 = GameClock.Now;
        await Press(PlayerInput.Phone, 0);
        for (int i = 0; i < 100 && !phone.IsFolded; i++) await Wait(0.01);
        double folded = GameClock.Now - t0;
        Check(phone.IsFolded && me.PhoneState == FootPlayer.PhoneFoldedState && folded < 0.35, $"the first press comes out folded ({folded:F2} s)");
        t0 = GameClock.Now;
        await Press(PlayerInput.Phone, 0);
        for (int i = 0; i < 100 && !phone.IsCovering; i++) await Wait(0.01);
        double opened = GameClock.Now - t0;
        Check(phone.IsCovering && phone.Screens.Shown == PhoneScreens.Page.Contacts && opened < 0.45, $"the second press opens on the last app, the Contacts ({phone.Screens.Shown}, {opened:F2} s)");
        await Key(Godot.Key.Escape, 0.3);
        await Key(Godot.Key.Escape, 0.8);
        Check(phone.IsFolded, "Esc on Home folds it again");
        // a digit on the cover runs its tile straight into that app (2 is the Map)
        await Key(Godot.Key.Key2, 0.8);
        Check(phone.IsCovering && phone.Screens.Shown == PhoneScreens.Page.Map, $"a digit on the cover opens its app straight away ({phone.Screens.Shown})");
        Check(phone.LastApp == PhoneScreens.Page.Map, "and it becomes the last app");
        phone.Screens.ShowPage(PhoneScreens.Page.Home);
        await Wait(0.3);
    }

    /// <summary>The body walks in every state, a held right button turns the view, back and put away have their keys, typing stands the body.</summary>
    private async Task WalkingChecks(FootPlayer me, PhoneController phone)
    {
        if (!phone.IsCovering) { phone.Open(PhoneScreens.Page.Home); await Wait(0.8); }
        if (!Platform.IsTouch)
        {
            // open on Home: W walks, on the Map too, the phone stays open
            var from = me.GlobalPosition;
            await HoldShot(Godot.Key.W, 1.0, "phone_1825/open_home_walking.png");
            Check(me.GlobalPosition.DistanceTo(from) > 0.5f && me.PhoneState == FootPlayer.PhoneOpenState, $"open on Home, W walks the body ({me.GlobalPosition.DistanceTo(from):F2} m), the phone stays open");
            phone.Open(PhoneScreens.Page.Map);
            await Wait(0.6);
            from = me.GlobalPosition;
            await HoldShot(Godot.Key.S, 1.0, "phone_1825/open_map_walking.png");
            Check(me.GlobalPosition.DistanceTo(from) > 0.4f && phone.Screens.Shown == PhoneScreens.Page.Map, $"open on the Map, S walks the body ({me.GlobalPosition.DistanceTo(from):F2} m)");
            await Wait(0.2);

            // the held right button captures the mouse and turns the view; let go, the cursor is back
            phone.Open(PhoneScreens.Page.Contacts);
            await Wait(0.5);
            float yaw = me.LookYaw;
            Check(MouseCapture.Mode == Input.MouseModeEnum.Visible, "open, the cursor shows");
            Mouse(MouseButton.Right, true);
            await Wait(0.15);
            Check(MouseCapture.Mode == Input.MouseModeEnum.Captured, "the right button held captures the mouse");
            Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(120, 0) });
            await Wait(0.2);
            float turned = Mathf.Abs(Mathf.AngleDifference(yaw, me.LookYaw));
            Check(turned > 0.1f, $"and turns the view ({Mathf.RadToDeg(turned):F0} degrees)");
            Check(phone.Screens.Shown == PhoneScreens.Page.Contacts, "the right button is not Back any more");
            Mouse(MouseButton.Right, false);
            await Wait(0.15);
            Check(MouseCapture.Mode == Input.MouseModeEnum.Visible, "let go, the cursor is back");
            Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(120, 0) });
            await Wait(0.2);
            Check(Mathf.Abs(Mathf.AngleDifference(yaw, me.LookYaw)) - turned < 0.01f, "the mouse no longer turns the view once the cursor is back");
        }

        // back: Esc and Backspace (checked above), the mouse's fourth button
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.4);
        Mouse(MouseButton.Xbutton1, true);
        Mouse(MouseButton.Xbutton1, false);
        await Wait(0.4);
        Check(phone.Screens.Shown == PhoneScreens.Page.Home && phone.IsCovering, "the mouse's fourth button goes back");

        if (!Platform.IsTouch)
        {
            // typing in the Map's search: letters type, the body stands
            phone.Open(PhoneScreens.Page.Map);
            await Wait(0.6);
            phone.Screens.FocusSearch();
            await Wait(0.3);
            var from = me.GlobalPosition;
            foreach (var (key, ch) in new[] { (Godot.Key.W, 'w'), (Godot.Key.A, 'a'), (Godot.Key.S, 's'), (Godot.Key.D, 'd') })
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = ch, Pressed = true });
                await Wait(0.3);
                Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = ch, Pressed = false });
            }
            Check(me.GlobalPosition.DistanceTo(from) < 0.1f, $"typing W A S D in the search does not walk the body ({me.GlobalPosition.DistanceTo(from):F2} m)");
            Check(phone.Screens.SearchText == "wasd", $"the letters were typed ({phone.Screens.SearchText})");
            await Key(Godot.Key.Escape, 0.4);
            phone.Screens.ShowPage(PhoneScreens.Page.Home);
            await Wait(0.3);
            from = me.GlobalPosition;
            await Hold(Godot.Key.W, 0.8);
            Check(me.GlobalPosition.DistanceTo(from) > 0.3f, "and the body walks again once the box is left");

            // a sprint start puts the phone away, as it holsters an item
            foreach (var k in new[] { Godot.Key.W, Godot.Key.Shift })
                Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true });
            for (int i = 0; i < 40 && phone.IsOut; i++) await Wait(0.1);
            foreach (var k in new[] { Godot.Key.Shift, Godot.Key.W })
                Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = false });
            Check(!phone.IsOut, "a sprint start puts the phone away");
            await Wait(0.5);
            await Press(PlayerInput.Phone, 1.0);
            phone.Open(PhoneScreens.Page.Home);
            await Wait(0.8);
        }
    }

    /// <summary>The window pixels where uv (0..1) of the inner or cover screen is, as a parsed event wants them.</summary>
    private Vector2 WindowAt(FootPlayer me, Vector2 uv, bool cover)
    {
        var world = me.PhoneScreenToWorld(uv, cover, 0f) ?? Vector3.Zero;
        var canvas = me.CameraOrNull?.UnprojectPosition(world) ?? Vector2.Zero;
        return GetViewport().GetScreenTransform() * canvas;
    }

    /// <summary>A finger (the mouse Godot emulates from a touch): down at uv, dragged <paramref name="move"/> millimetres, up.</summary>
    private async Task FingerTap(FootPlayer me, Vector2 uv, bool cover, Vector2 move = default)
    {
        float s = GetViewport().GetScreenTransform().X.Length();
        float mm = TouchControls.MeasureScreen(GetViewport(), GetViewport().GetVisibleRect().Size).PxPerMm * s;
        var from = WindowAt(me, uv, cover);
        void Send(InputEvent e) => Input.ParseInputEvent(e);
        Send(new InputEventMouseMotion { Device = (int)InputEvent.DeviceIdEmulation, Position = from, GlobalPosition = from });
        Send(new InputEventMouseButton { Device = (int)InputEvent.DeviceIdEmulation, ButtonIndex = MouseButton.Left, Pressed = true, Position = from, GlobalPosition = from });
        await Wait(0.05);
        foreach (float f in new[] { 0.5f, 1f })
        {
            var at = from + move * mm * f;
            Send(new InputEventMouseMotion { Device = (int)InputEvent.DeviceIdEmulation, Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left });
            await Wait(0.03);
        }
        var end = from + move * mm;
        Send(new InputEventMouseButton { Device = (int)InputEvent.DeviceIdEmulation, ButtonIndex = MouseButton.Left, Pressed = false, Position = end, GlobalPosition = end });
        await Wait(0.3);
    }

    private static void Pad(JoyButton button)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = button, Pressed = down });
    }

    /// <summary>A finger counts in millimetres (#1253): 2 mm of wobble is still a tap, 10 mm sideways is Back; Photo pages sideways.</summary>
    private async Task FingerChecks(FootPlayer me, PhoneController phone)
    {
        phone.Screens.ShowPage(PhoneScreens.Page.Home);
        await Wait(0.5);
        if (phone.Screens.FocusUv() is not { } tile) { Log("skip (no focused tile to touch)"); return; }
        await FingerTap(me, tile, cover: false, new Vector2(1.2f, 0.6f));
        Check(phone.Screens.Shown == PhoneScreens.Page.Map, $"a finger that wobbles 1.3 mm still taps the tile ({phone.Screens.Shown})");
        await FingerTap(me, new Vector2(0.5f, 0.5f), cover: false, new Vector2(6f, 0f));
        Check(phone.Screens.Shown == PhoneScreens.Page.Map, $"a 6 mm sideways stroke is not Back ({phone.Screens.Shown})");
        await FingerTap(me, new Vector2(0.5f, 0.5f), cover: false, new Vector2(12f, 0f));
        Check(phone.Screens.Shown == PhoneScreens.Page.Home, $"a 12 mm swipe right is Back ({phone.Screens.Shown})");
        if (!Rendered) return;
        phone.Screens.ShowPage(PhoneScreens.Page.Photos);
        await Wait(0.4);
        if (phone.Screens.GridCount < 2) return;
        await Key(Godot.Key.Enter, 0.4);
        string? first = phone.Screens.PhotoShown;
        await FingerTap(me, new Vector2(0.5f, 0.5f), cover: false, new Vector2(-12f, 0f));
        Check(phone.Screens.Shown == PhoneScreens.Page.Photo && phone.Screens.PhotoShown != first, "a swipe left in the viewer is the next photo, not Back");
        await FingerTap(me, new Vector2(0.5f, 0.5f), cover: false, new Vector2(12f, 0f));
        Check(phone.Screens.Shown == PhoneScreens.Page.Photo && phone.Screens.PhotoShown == first, "a swipe right is the previous one");
        phone.Screens.ShowPage(PhoneScreens.Page.Home);
        await Wait(0.3);
    }

    /// <summary>Headless draws no frame, so the camera has nothing to shoot: its checks run windowed only (as the Polaroid's).</summary>
    private static bool Rendered => DisplayServer.GetName() != "headless";

    private void Shoots(bool ok, string what)
    {
        if (Rendered) Check(ok, what);
        else Log($"skip (headless: no frame to shoot): {what}");
    }

    /// <summary>Contacts (#1253): one row per player, a sheet of four asks, Cancel first in a confirm, a result line that stays.</summary>
    private async Task ContactsChecks(PhoneController phone)
    {
        var screens = phone.Screens;
        var sent = new List<string>();
        void Sent(string c) => sent.Add(c);
        screens.CommandPicked += Sent;
        screens.TestPeople = new List<string> { "Anna", "Ben" };
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.4);
        Check(screens.ContactRowCount == 2, $"Contacts has one row per player ({screens.ContactRowCount})");
        Check(screens.ContactsFocus?.StartsWith("Anna") == true && screens.ContactsFocus.Contains("Online"), $"a row shows name and status ({screens.ContactsFocus})");
        await Key(Godot.Key.Enter, 0.3);
        Check(screens.ContactsSheetOpen && screens.ContactsFocus == "Go to", $"Enter opens the sheet on Go to ({screens.ContactsFocus})");
        await Key(Godot.Key.Down, 0.2); await Key(Godot.Key.Down, 0.2); await Key(Godot.Key.Down, 0.2);
        Check(screens.ContactsFocus == "Fight", $"the sheet has Go to, Bring here, Party invite, Fight ({screens.ContactsFocus})");
        await Key(Godot.Key.Enter, 0.3);
        Check(screens.ContactsConfirmOpen && screens.ContactsFocus == "Cancel" && sent.Count == 0, $"Fight asks first, Cancel focused, nothing sent ({screens.ContactsFocus}, {sent.Count} sent)");
        await Key(Godot.Key.Enter, 0.3);
        Check(screens.ContactsSheetOpen && !screens.ContactsConfirmOpen && sent.Count == 0, "Cancel goes back to the sheet, nothing sent");
        await Key(Godot.Key.Escape, 0.3);
        Check(!screens.ContactsSheetOpen && phone.Screens.Shown == PhoneScreens.Page.Contacts, "Esc closes the sheet, the list stays");
        await Key(Godot.Key.Enter, 0.3);
        await Key(Godot.Key.Down, 0.2); await Key(Godot.Key.Down, 0.2);
        await Key(Godot.Key.Enter, 0.3);
        Check(sent.Count == 1 && sent[0] == "/party invite Anna", $"Party invite sends the party command ({string.Join("|", sent)})");
        Check(screens.ContactsResult.StartsWith("Party invite · Anna"), $"the result line stays after the action ({screens.ContactsResult})");
        screens.OnResolved("party", "Anna", "declined");
        Check(screens.ContactsResult == "Party invite · Anna: Declined", $"Declined shows ({screens.ContactsResult})");
        screens.OnResolved("party", "Anna", "lapsed");
        Check(screens.ContactsResult.EndsWith("Timed out"), $"Timed out shows ({screens.ContactsResult})");
        screens.OnResolved("party", "Anna", "accepted");
        Check(screens.ContactsResult.EndsWith("Accepted"), $"Accepted shows ({screens.ContactsResult})");
        await Key(Godot.Key.Down, 0.2);
        await Key(Godot.Key.Enter, 0.3);
        await Key(Godot.Key.Down, 0.2);
        await Key(Godot.Key.Enter, 0.3);
        await Key(Godot.Key.Down, 0.2);
        await Key(Godot.Key.Enter, 0.3);
        Check(sent.Count == 2 && sent[1] == "/tphere Ben", $"Bring here confirmed sends /tphere ({string.Join("|", sent)})");
        await Key(Godot.Key.Escape, 0.3);
        Check(phone.Screens.Shown == PhoneScreens.Page.Home, "Esc on the list still goes home");
        screens.CommandPicked -= Sent;
        screens.TestPeople = null;
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

    private async Task Wheel(MouseButton button, bool shift, double after, bool alt = false)
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = down, ShiftPressed = shift, AltPressed = alt });
        await Wait(after);
    }

    /// <summary>
    /// The hotbar's wheel and the holster (#1369): Alt + wheel steps the slot (the plain wheel, Shift or not, does not),
    /// the number of the slot in hand puts its item away and takes it out, the holster key does the same, Use draws
    /// and uses, and an item left alone for 8 s goes away by itself. A holstered hand reads as empty to the other peers.
    /// </summary>
    private async Task HolsterChecks(FootPlayer me)
    {
        if (Items.ItemController.Instance is not { } items) { Log("skip (no items)"); return; }
        var inv = items.Inventory;
        int slot = -1;
        for (int i = 0; i < Items.Inventory.HotbarSize && slot < 0; i++) if (!inv[i].IsEmpty) slot = i;
        if (slot < 0) { Log("skip (empty hotbar)"); return; }
        GameSettings.Current.AutoHolster = true;
        inv.Select(slot);
        await Wait(0.3);
        Check(me.HeldItemId != 0 && !inv.Holstered, $"slot {slot + 1} is in hand ({me.HeldItemId})");

        int before = inv.Selected;
        await Wheel(MouseButton.WheelDown, shift: false, 0.2);
        await Wheel(MouseButton.WheelDown, shift: true, 0.2);
        Check(inv.Selected == before, $"the plain wheel, Shift or not, does not step the hotbar ({before} -> {inv.Selected})");
        await Wheel(MouseButton.WheelDown, shift: false, 0.2, alt: true);
        Check(inv.Selected == (before + 1) % Items.Inventory.HotbarSize, $"Alt + wheel steps it ({before} -> {inv.Selected})");
        await Wheel(MouseButton.WheelUp, shift: false, 0.2, alt: true);
        Check(inv.Selected == before, $"Alt + wheel up steps back ({inv.Selected})");

        await Key(Godot.Key.Key1 + slot, 0.3);
        Check(inv.Holstered && me.HeldItemId == 0, $"the number of the slot in hand holsters it: the hand is empty ({me.HeldItemId})");
        await Key(Godot.Key.Key1 + slot, 0.3);
        Check(!inv.Holstered && me.HeldItemId != 0, $"and again draws it ({me.HeldItemId})");
        await Press(PlayerInput.Holster, 0.3);
        Check(inv.Holstered && me.HeldItemId == 0, "the holster key puts it away");
        items.ForceAim = true;
        await Wait(0.2);
        items.ForceAim = false;
        Check(!inv.Holstered, "Aim draws it");
        inv.SetHolstered(true);
        await Press(PlayerInput.UseItem, 0.1);
        Check(!inv.Holstered, "Use draws it (and uses it on the same press)");
        inv.SetHolstered(true);
        await Press(PlayerInput.NextItem, 0.3);
        Check(!inv.Holstered, "picking another slot draws");
        inv.Select(slot);
        await Wait(0.3);

        double t0 = GameClock.Now;
        while (!inv.Holstered && GameClock.Now - t0 < 12) await Wait(0.2);
        double idle = GameClock.Now - t0;
        Check(inv.Holstered && idle is > 7.0 and < 10.0, $"left alone, it is holstered after about 8 s ({idle:F1} s)");
        Check(me.HeldItemId == 0, "and replicated as an empty hand");
        // no item type is exempt: a hammer left alone goes away too
        var keep = inv[slot];
        int hamSlot = (slot + 1) % Items.Inventory.HotbarSize;
        var keepHam = inv[hamSlot];
        inv.Put(hamSlot, new Items.ItemStack(Items.ItemId.Hammer, 1));
        inv.Select(hamSlot);
        await Wait(0.3);
        t0 = GameClock.Now;
        while (!inv.Holstered && GameClock.Now - t0 < 12) await Wait(0.2);
        idle = GameClock.Now - t0;
        Check(inv.Holstered && idle is > 7.0 and < 10.0, $"a hammer (once exempt) is holstered after about 8 s too ({idle:F1} s)");

        // a new sprint start holsters at once; drawing on the run stays out; the next sprint holsters again
        inv.Select(hamSlot);
        await Wait(0.3);
        Check(!inv.Holstered, "hammer in hand before the sprint");
        Input.ActionPress(PlayerInput.MoveForward);
        Input.ActionPress(PlayerInput.Sprint);
        for (double w0 = GameClock.Now; !me.SprintIntent && GameClock.Now - w0 < 2;) await Wait(0.1);
        await Wait(0.3);
        Check(me.SprintIntent && inv.Holstered && me.HeldItemId == 0, "starting a sprint puts the item in the pocket");
        await Wait(0.5);
        Check(inv.Holstered, "and it is not drawn back by itself");
        await Press(PlayerInput.UseItem, 0.1);
        await Wait(0.5);
        Check(!inv.Holstered && me.SprintIntent, "Use draws it on the run, and it stays out while the sprint goes on");
        Input.ActionRelease(PlayerInput.Sprint);
        await Wait(0.3);
        Input.ActionPress(PlayerInput.Sprint);
        await Wait(0.4);
        Check(inv.Holstered, "a NEW sprint holsters again");
        Input.ActionRelease(PlayerInput.Sprint);
        Input.ActionRelease(PlayerInput.MoveForward);
        inv.Put(hamSlot, keepHam);
        inv.Put(slot, keep);

        GameSettings.Current.AutoHolster = false;
        inv.Select(slot);
        t0 = GameClock.Now;
        await Wait(9.5);
        Check(!inv.Holstered, "Auto holster off: it stays out");
        GameSettings.Current.AutoHolster = true;
        inv.Select(slot);
        await Wait(0.3);
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
