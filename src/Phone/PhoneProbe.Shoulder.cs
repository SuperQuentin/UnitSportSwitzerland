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

    /// <summary>The shoulder shots: folded walking, open Home standing and walking, another app, the held right button, a frame mid-ease.</summary>
    private async Task ShoulderShots(FootPlayer me, PhoneController phone)
    {
        await Press(PlayerInput.Phone, 0.8);
        await HoldShot(Godot.Key.W, 1.2, ShoulderDir + "folded_walking.png");
        await Wait(0.3);
        phone.Screens.ShowPage(PhoneScreens.Page.Home);
        phone.Open(PhoneScreens.Page.Home);
        await Wait(0.17);
        Shot(ShoulderDir + "open_mid_ease.png");
        await Wait(1.0);
        Shot(ShoulderDir + "open_home_standing.png");
        if (me.PhoneModel is { } pm)
        {
            var bodyBasis = new Basis(Vector3.Up, me.LookYaw);
            var rel = bodyBasis.Inverse() * (pm.GlobalPosition - me.GlobalPosition);
            var n = bodyBasis.Inverse() * pm.GlobalBasis.Z.Normalized();
            var c = bodyBasis.Inverse() * (me.PhoneScreenToWorld(new Vector2(0.5f, 0.5f), false, 0f)!.Value - me.GlobalPosition);
            Log($"shoulder: phone origin in the body frame (right, up, back) {rel}, screen centre {c}, screen normal {n}");
        }
        var box = ScreenInView(me);
        Log($"shoulder: open Home screen centre {box.Centre}, width {box.Box.Size.X:P0}, height {box.Box.Size.Y:P0}, inside {box.Inside}, corner phone {me.PhoneViewShown}, shoulder {me.PhoneShoulder:F2}");
        await HoldShot(Godot.Key.W, 1.6, ShoulderDir + "open_home_walking.png");
        phone.Open(PhoneScreens.Page.Contacts);
        await Wait(0.5);
        await HoldShot(Godot.Key.W, 1.2, ShoulderDir + "open_contacts_walking.png");
        phone.Open(PhoneScreens.Page.Activities);
        await Wait(0.5);
        Shot(ShoulderDir + "open_activities_standing.png");
        phone.Open(PhoneScreens.Page.Home);
        await Wait(0.5);
        Mouse(MouseButton.Right, true);
        await Wait(0.15);
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(260, 40) });
        await Wait(0.5);
        Shot(ShoulderDir + "open_right_mouse_look.png");
        Mouse(MouseButton.Right, false);
        await Wait(0.4);
        Shot(ShoulderDir + "open_after_look.png");
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
        double started = RealClock.Now;
        for (int i = 0; i < 90 && me.PhoneShoulder < 0.99f; i++)
        {
            await NextFrames(1);
            if (me.PhoneShoulder is > 0f and < 1f) mid = me.PhoneShoulder;
        }
        double easeSeconds = RealClock.Now - started;
        await Wait(1.0);
        phone.Screens.ShowPage(PhoneScreens.Page.Home);
        await Wait(0.3);
        if (!third)
        {
            Check(me.PhoneShoulder == 0f && me.PhoneViewShown, "first person: no shoulder view, the corner phone stays (unchanged)");
            Check(Mathf.Abs(cam.Fov - fov0) < 0.01f, "first person: the field of view is the game's");
            return;
        }
        Check((mid > 0f || easeSeconds >= 0.2) && easeSeconds < 0.8 && me.PhoneShoulder >= 0.99f, $"opening eases the camera over the shoulder (a frame at {mid:F2}, in {easeSeconds:F2} s, then {me.PhoneShoulder:F2})");
        Check(!me.PhoneViewShown, "third person open: no corner phone, the in-world phone over the shoulder");
        Check(Mathf.Abs(cam.Fov - FootPlayer.Tune.Fov) < 0.5f && cam.GlobalTransform.Origin.DistanceTo(before) > 0.5f, $"the camera is in close ({cam.GlobalTransform.Origin.DistanceTo(before):F2} m from the gameplay one, fov {fov0:F0} to {cam.Fov:F0})");
        var view = GetViewport().GetVisibleRect().Size;
        var home = ScreenInView(me);
        Log($"shoulder: screen centre {home.Centre}, box {home.Box}");
        Check(home.Inside && home.Box.Size.X >= 0.30f, $"the screen is wholly in view and {home.Box.Size.X:P0} of the width ({home.Box.Size.X * view.X:F0} px)");
        Check(home.Box.End.X <= 0.53f, "the view's centre is not behind the screen");
        // the horizon and the path ahead: a far point straight ahead of the body, at the eye's height, is in view and above the screen
        // (the horizon in the middle of the picture: 40 m along the lens's own heading at its height; the body's heading is turned toward the phone, so a point straight ahead of it sits higher and nearer the edge)
        var flatLens = new Vector3(-cam.GlobalBasis.Z.X, 0f, -cam.GlobalBasis.Z.Z).Normalized();
        var ahead = cam.GlobalPosition + flatLens * 40f;
        var horizon = cam.UnprojectPosition(ahead);
        var pathAt = me.GlobalPosition + new Vector3(Mathf.Sin(me.LookYaw + Mathf.Pi), 0f, Mathf.Cos(me.LookYaw + Mathf.Pi)) * 6f;
        var path = cam.UnprojectPosition(pathAt);
        Check(horizon.X > 0 && horizon.X < view.X && horizon.Y > 0 && horizon.Y < view.Y && !cam.IsPositionBehind(ahead),
            $"the horizon straight ahead is in view ({horizon.X / view.X:P0}, {horizon.Y / view.Y:P0})");
        Check(path.X > 0 && path.X < view.X && path.Y > 0 && path.Y < view.Y && !cam.IsPositionBehind(pathAt),
            $"and the path 6 m ahead ({path.X / view.X:P0}, {path.Y / view.Y:P0})");
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
        Check(me.GlobalPosition.DistanceTo(from) > 1f && walkBox.Inside && walkBox.Box.Size.X >= 0.28f, $"walking ({me.GlobalPosition.DistanceTo(from):F1} m) the screen stays in view ({walkBox.Box.Size.X:P0} wide)");
        Check(rms < 3.0 && worst < 10f, $"and does not shake ({rms:F2} px rms, worst {worst:F1} px)");
        await Wait(0.4);

        // closing eases back to the gameplay camera and brings the corner phone back
        await Key(Godot.Key.Escape, 0.1);
        await Wait(0.9);
        Check(me.PhoneShoulder == 0f && me.PhoneViewShown && Mathf.Abs(cam.Fov - fov0) < 0.5f, $"folded again: the gameplay camera is back (fov {cam.Fov:F1}), the corner phone shown");
    }
}
