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
        await Key(Godot.Key.S, 0.4);
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

        // the map: closer first, then the world fades to black behind the phone
        phone.Open(PhoneScreens.Page.Map);
        await Wait(0.3);
        Check(me.PhoneBackdropShown < 0.5f, $"on the way in, the world still shows ({me.PhoneBackdropShown:F2})");
        await Wait(1.2);
        Check(me.PhoneCover >= 1f && me.PhoneBackdropShown >= 1f, $"the map square on, the world black behind ({me.PhoneCover:F2}, {me.PhoneBackdropShown:F2})");
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
            await Key(Godot.Key.S, 0.3);
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
        Teleporter.Instance?.TeleportTo(stay.E + 500, stay.N, "probe cancelled");
        await Wait(0.1);
        Check(phone.Screens.Shown == PhoneScreens.Page.Travel, "the curtain is up while the jump waits (#1253)");
        await Key(Godot.Key.Escape, 2.5);
        var stayed = me.Global;
        Check(Math.Abs(stayed.E - stay.E) < 2 && phone.IsOut && phone.Screens.Shown == PhoneScreens.Page.Home, "Esc on the curtain cancels the travel and stays on the phone (#1253)");

        await FingerChecks(me, phone);

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
