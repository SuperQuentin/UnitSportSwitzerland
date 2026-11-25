using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Phone;

/// <summary>The camera over the shoulder onto the open phone (#1825, third person): shots and the framing it must keep.</summary>
public partial class PhoneProbe
{
    private const string ShoulderDir = "phone_shoulder/";

    /// <summary>Where the open inner screen's centre and its corners are in the view, as shares of it.</summary>
    private (Vector2 Centre, Rect2 Box, bool Inside) ScreenInView(FootPlayer me)
    {
        var size = GetViewport().GetVisibleRect().Size;
        var cam = me.CameraOrNull;
        if (cam == null) return (Vector2.Zero, default, false);
        bool inside = true;
        var lo = new Vector2(float.MaxValue, float.MaxValue);
        var hi = new Vector2(float.MinValue, float.MinValue);
        foreach (var uv in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) })
        {
            var world = me.PhoneScreenToWorld(uv, false, 0f) ?? Vector3.Zero;
            var at = cam.UnprojectPosition(world);
            inside &= !cam.IsPositionBehind(world) && at.X >= 0 && at.Y >= 0 && at.X <= size.X && at.Y <= size.Y;
            lo = lo.Min(at);
            hi = hi.Max(at);
        }
        return ((lo + hi) / 2f / size, new Rect2(lo / size, (hi - lo) / size), inside);
    }

    private async Task NextFrames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>The shoulder shots: folded walking, the opening through six frames, open Home standing and walking, Contacts with the cursor on a contact, the held right button.</summary>
    private async Task ShoulderShots(FootPlayer me, PhoneController phone)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        await Wait(0.5);
        await Press(PlayerInput.Phone, 1.3);
        Shot(ShoulderDir + "branch_folded.png");
        await HoldShot(Godot.Key.W, 1.2, ShoulderDir + "branch_folded_walking.png");
        await Wait(0.6);
        phone.Screens.ShowPage(PhoneScreens.Page.Home);
        phone.Open(PhoneScreens.Page.Home);
        double t = 0;
        foreach (double when in new[] { 0.1, 0.25, 0.4, 0.6, 0.9, 1.6 })
        {
            await Wait(when - t);
            t = when;
            Shot(ShoulderDir + $"branch_open_{when.ToString("0.00", inv)}.png");
        }
        await Wait(0.4);
        Shot(ShoulderDir + "branch_open_home_standing.png");
        var box = ScreenInView(me);
        Log($"shoulder: open Home screen centre {box.Centre}, width {box.Box.Size.X:P0}, height {box.Box.Size.Y:P0}, inside {box.Inside}, corner phone {me.PhoneViewShown}, shoulder {me.PhoneShoulder:F2}");
        await HoldShot(Godot.Key.W, 1.6, ShoulderDir + "branch_open_home_walking.png");
        await Wait(0.4);
        phone.Screens.TestPeople = new List<string> { "Anna", "Ben", "Chloe", "Dario" };
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.8);
        if (phone.Screens.FocusUv() is { } row)
        {
            var at = WindowAt(me, row, false);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await Wait(0.4);
        }
        Shot(ShoulderDir + "branch_open_contacts_standing_cursor.png");
        await HoldShot(Godot.Key.W, 1.2, ShoulderDir + "branch_open_contacts_walking.png");
        phone.Open(PhoneScreens.Page.Home);
        await Wait(0.5);
        Mouse(MouseButton.Right, true);
        await Wait(0.15);
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(260, 40) });
        await Wait(0.5);
        Shot(ShoulderDir + "branch_open_right_mouse_look.png");
        Mouse(MouseButton.Right, false);
        await Wait(0.4);
    }

    /// <summary>
    /// Third person, the phone open: the camera is over the shoulder, the corner phone is gone, the screen is in view, legible and
    /// off the middle, the horizon and the path ahead are in view, and walking does not shake it (#1825).
    /// </summary>
    private async Task ShoulderChecks(FootPlayer me, PhoneController phone)
    {
        if (Platform.IsTouch) { Log("skip the shoulder checks (touch is seen square on)"); return; }
        bool third = me.ZoomsView;
        if (phone.IsOut) { phone.PutAway(); for (int i = 0; i < 60 && phone.IsOut; i++) await Wait(0.1); }
        await Wait(1.0);
        var cam = me.CameraOrNull!;
        float fov0 = cam.Fov;
        var before = cam.GlobalTransform.Origin;
        await Press(PlayerInput.Phone, 0.7);
        Check(me.PhoneShoulder == 0f && me.PhoneViewShown, "folded: the gameplay camera, the corner phone shown");
        phone.Open(PhoneScreens.Page.Home);   // not the last app: it may be the Map, which has its own camera
        // the ease is seen between: some frame has it neither at 0 nor at 1
        float mid = 0f;
        double started = GameClock.Now;
        for (int i = 0; i < 90 && me.PhoneShoulder < 0.99f; i++)
        {
            await NextFrames(1);
            if (me.PhoneShoulder is > 0f and < 1f) mid = me.PhoneShoulder;
        }
        double easeSeconds = GameClock.Now - started;
        await Wait(1.0);
        phone.Screens.ShowPage(PhoneScreens.Page.Home);
        await Wait(0.3);
        if (!third)
        {
            Check(me.PhoneShoulder == 0f && me.PhoneViewShown, "first person: no shoulder view, the corner phone stays (unchanged)");
            Check(Mathf.Abs(cam.Fov - fov0) < 0.01f, "first person: the field of view is the game's");
            return;
        }
        Check((mid > 0f || easeSeconds >= 0.3) && easeSeconds is >= 0.3 and < 1.0 && me.PhoneShoulder >= 0.99f, $"opening eases the camera over the shoulder (a frame at {mid:F2}, in {easeSeconds:F2} s, then {me.PhoneShoulder:F2})");
        Check(!me.PhoneViewShown, "third person open: no corner phone, the in-world phone over the shoulder");
        Check(Mathf.Abs(cam.Fov - FootPlayer.PhoneShoulderFov) < 0.5f && cam.GlobalTransform.Origin.DistanceTo(before) > 0.5f, $"the camera is in close ({cam.GlobalTransform.Origin.DistanceTo(before):F2} m from the gameplay one, fov {fov0:F0} to {cam.Fov:F0})");
        var view = GetViewport().GetVisibleRect().Size;
        var home = ScreenInView(me);
        Log($"shoulder: screen centre {home.Centre}, box {home.Box}");
        Check(home.Inside && home.Box.Size.X * view.X >= 250f, $"the screen is wholly in view and {home.Box.Size.X * view.X:F0} px wide ({home.Box.Size.X:P0})");
        await RayCheck(me, phone, false, "shoulder");

        // walking: the body goes on, the phone stays framed and does not shake
        var from = me.GlobalPosition;
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.W, PhysicalKeycode = Godot.Key.W, Pressed = true });
        await Wait(0.5);
        var centres = new List<Vector2>();
        for (int i = 0; i < 90; i++)
        {
            await NextFrames(1);
            centres.Add(ScreenInView(me).Centre * view);
        }
        var walkBox = ScreenInView(me);
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.W, PhysicalKeycode = Godot.Key.W, Pressed = false });
        double sum = 0;
        float worst = 0;
        for (int i = 1; i + 1 < centres.Count; i++)
        {
            float d2 = (centres[i + 1] - 2f * centres[i] + centres[i - 1]).Length();
            sum += d2 * d2;
            worst = Mathf.Max(worst, d2);
        }
        double rms = Math.Sqrt(sum / Math.Max(1, centres.Count - 2));
        Log($"shoulder: walking, the screen's centre moves by {rms:F2} px rms (worst {worst:F2} px) frame to frame");
        Check(me.GlobalPosition.DistanceTo(from) > 1f && walkBox.Inside && walkBox.Box.Size.X * view.X >= 220f, $"walking ({me.GlobalPosition.DistanceTo(from):F1} m) the screen stays in view ({walkBox.Box.Size.X:P0} wide)");
        Check(rms < 3.0 && worst < 10f, $"and does not shake ({rms:F2} px rms, worst {worst:F1} px)");
        await Wait(0.4);

        // closing eases back to the gameplay camera and brings the corner phone back
        await Key(Godot.Key.Escape, 0.1);
        await Wait(0.9);
        Check(me.PhoneShoulder == 0f && me.PhoneViewShown && Mathf.Abs(cam.Fov - fov0) < 0.5f, $"folded again: the gameplay camera is back (fov {cam.Fov:F1}), the corner phone shown");
    }
}
