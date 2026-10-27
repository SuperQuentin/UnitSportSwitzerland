using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.XR;

namespace UnitSport.Phone;

/// <summary>
/// <c>--phonevrcheck --xrsim --world fixture</c> (#920): the phone in VR with scripted hands
/// (<see cref="XrPhone.SimHold"/>, <see cref="XrPhone.SimGrip"/>, <see cref="XrPhone.SimTip"/>,
/// <see cref="XrPhone.SimFree"/>, <see cref="XrPhone.SimTrigger"/>; no controller is tracked under --xrsim).
/// <list type="bullet">
/// <item>Phase 1: a grip at the front pocket takes it out into the hand; held up before the eyes, the
/// fingertip taps the cover's quick action (the camera app opens); a tap on a home tile opens the map;
/// the game's camera never moves.</item>
/// <item>Phase 2: the camera app's lens points where the hand does, tilted with it; the holding hand's
/// trigger shoots (windowed); the phone's back turned to the face is a selfie, turned back it ends; the
/// free hand swings the front half shut and open again about the hinge; the right trigger, its ray off
/// the phone, steps the zoom; the ray and the trigger press a tile from 40 cm off.</item>
/// <item>Phase 3: a teleport waits for the phone at the eyes and goes behind it (no blink), or behind a
/// blink when it does not come; a call is answered with the phone at the ear; let go away from the
/// pocket it hangs, then flies home; let go at the pocket it goes in. The VR layout has three apps a row.</item>
/// </list>
/// Windowed it saves <c>test_output/phonevr_*.png</c>. Read the <c>[phonevrcheck]</c> lines.
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

    private static bool Windowed => DisplayServer.GetName() != "headless";

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

        // ---- phase 1: out of the pocket, the fingertip on the glass ----
        await FromPocket();
        Check(phone.IsOut && me.PhoneState == FootPlayer.PhoneFoldedState, $"a grip at the front pocket takes it out (state {me.PhoneState})");
        var lens = me.CameraOrNull?.GlobalTransform;
        BeforeEyes();
        await Wait(0.8);
        float off = me.PhoneModel is { Visible: true } m ? m.GlobalPosition.DistanceTo(Head * new Vector3(-0.02f, -0.16f, -0.32f)) : -1f;
        Check(off is >= 0f and < 0.08f, $"it is in the hand, before the eyes ({off:F2} m off)");
        Shot("phonevr_1_folded.png");

        phone.SelectQuick("Camera");
        await Wait(0.2);
        await Tap(phone.Screens.CoverCardUv(), cover: true);
        await Wait(1.4);
        Check(me.PhoneState == FootPlayer.PhoneCameraState, $"a tap on the cover runs its quick action, the camera app (state {me.PhoneState})");

        // ---- phase 2: the camera app aimed by the arm, the trigger, a selfie by turning ----
        BeforeEyes(roll: 20f);
        await Wait(0.4);
        var lensNow = phone.Screens.Lens.GlobalBasis;
        var held = me.PhoneModel!.GlobalBasis.Orthonormalized();
        Check(lensNow.Z.Dot(held.Z) > 0.98f, $"the lens looks where the hand points it ({lensNow.Z.Dot(held.Z):F2})");
        Check(Mathf.Abs(lensNow.X.Y) > 0.25f, $"tilted with the hand, not levelled ({Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(lensNow.X.Y, -1f, 1f))):F0}°)");
        Shot("phonevr_2_camera.png");
        if (Windowed)
        {
            string? before = phone.LastPhoto;
            XrPhone.SimTrigger = true;
            await Wait(0.2);
            XrPhone.SimTrigger = false;
            for (int i = 0; i < 40 && phone.LastPhoto == before; i++) await Wait(0.1);
            Check(phone.LastPhoto != before, $"the holding hand's trigger shoots ({phone.LastPhoto})");
        }
        BeforeEyes(turned: true);
        await Wait(0.7);
        Check(phone.Selfie && me.PhoneState == FootPlayer.PhoneSelfieState, $"its back turned to the face is a selfie (state {me.PhoneState})");
        Shot("phonevr_3_selfie.png");
        BeforeEyes();
        await Wait(0.7);
        Check(!phone.Selfie && me.PhoneState == FootPlayer.PhoneCameraState, $"turned back, the selfie ends (state {me.PhoneState})");

        // the right trigger, its ray off the phone, steps the lens: 2x, 5x, round to 1x
        XrPhone.SimRay = Head * new Transform3D(Basis.Identity, new Vector3(0.25f, -0.3f, -0.2f));
        var zooms = new List<float>();
        for (int i = 0; i < 3; i++)
        {
            await Trigger();
            zooms.Add(phone.Screens.Zoom);
        }
        XrPhone.SimRay = null;
        Check(zooms is [2f, 5f, 1f], $"the right trigger off the phone steps the zoom ({string.Join(", ", zooms)})");

        await Button(JoyButton.B, 0.6);
        Check(me.PhoneState == FootPlayer.PhoneOpenState && phone.Screens.Shown == PhoneScreens.Page.Home, "B goes back to the home screen");
        Check(phone.Screens.HomeColumns == 3, $"the VR layout: three apps a row ({phone.Screens.HomeColumns})");
        Shot("phonevr_4_home.png");

        // the free hand swings the front half shut, then open again
        await Swing(me, from: 1f, to: 0.25f);
        float mid = me.PhoneModel!.Fold;
        Check(mid is > 0.1f and < 0.9f && me.PhoneState == FootPlayer.PhoneFoldedState, $"the hinge follows the free hand ({mid:F2})");
        await Swing(me, from: 0.25f, to: 0.02f, grip: false);
        XrPhone.SimFreeGrip = false;
        await Wait(0.6);
        Check(me.PhoneState == FootPlayer.PhoneFoldedState && me.PhoneModel!.Fold < 0.05f, $"swung shut by hand, it is folded ({me.PhoneModel!.Fold:F2})");
        await Swing(me, from: 0f, to: 0.7f);
        XrPhone.SimFreeGrip = false;
        XrPhone.SimFree = null;
        await Wait(0.8);
        Check(me.PhoneState == FootPlayer.PhoneOpenState && me.PhoneModel!.Fold >= 0.99f, $"let go past half open, it settles open ({me.PhoneModel!.Fold:F2})");

        await Wait(0.3);
        if (phone.Screens.FocusUv() is { } tile)
        {
            await Tap(tile, cover: false);
            await Wait(0.5);
        }
        Check(phone.Screens.Shown == PhoneScreens.Page.Map, $"a tap on a tile opens it ({phone.Screens.Shown})");
        Shot("phonevr_5_map.png");
        float drift = me.CameraOrNull?.GlobalTransform is { } now && lens is { } then ? now.Origin.DistanceTo(then.Origin) : -1f;
        Check(drift is >= 0f and < 0.05f, $"the game's camera never moved (the eye is the head's; {drift:F2} m)");

        // the ray: from 40 cm off, the right hand points at a tile and pulls the trigger
        await Button(JoyButton.B, 0.6);
        await Wait(0.3);
        if (phone.Screens.FocusUv() is { } far && me.PhoneModel is { } held2 && me.PhoneScreenToWorld(far, cover: false, 0f) is { } on)
        {
            var normal = held2.GlobalBasis.Orthonormalized().Z;
            var from = on + normal * 0.4f + Vector3.Right * 0.1f;
            XrPhone.SimRay = new Transform3D(Basis.LookingAt(on - from, Vector3.Up), from);
            await Wait(0.2);
            await Trigger();
            XrPhone.SimRay = null;
            await Wait(0.4);
        }
        Check(phone.Screens.Shown == PhoneScreens.Page.Map, $"the ray and the trigger press a tile from afar ({phone.Screens.Shown})");

        // ---- phase 3: travel behind the phone at the eyes, or a blink ----
        var at = me.Global;
        double asked = GameClock.Now;
        Teleporter.Instance?.TeleportTo(at.E + 250, at.N, "probe vr eyes");
        await Wait(0.6);
        AtEyes();
        for (int i = 0; i < 40 && Math.Abs(me.Global.E - (at.E + 250)) > 5; i++) await Wait(0.05);
        double took = GameClock.Now - asked;
        Check(Math.Abs(me.Global.E - (at.E + 250)) < 5 && took < 1.4, $"raised to the eyes, the travel goes behind it ({took:F1} s, before the blink)");
        for (int i = 0; i < 300 && phone.IsOut; i++) await Wait(0.1);
        Check(!phone.IsOut, "and back in the pocket there");

        await FromPocket();
        BeforeEyes(low: 0.7f);   // down at the hip, past the 40 cm reach
        await Wait(0.4);
        phone.Open(PhoneScreens.Page.Map);
        await Wait(0.8);
        at = me.Global;
        asked = GameClock.Now;
        Teleporter.Instance?.TeleportTo(at.E + 250, at.N, "probe vr blink");
        for (int i = 0; i < 100 && Math.Abs(me.Global.E - (at.E + 250)) > 5; i++) await Wait(0.1);
        took = GameClock.Now - asked;
        Check(Math.Abs(me.Global.E - (at.E + 250)) < 5 && took >= 1.2, $"not raised, it goes behind a blink after the wait ({took:F1} s)");
        for (int i = 0; i < 300 && phone.IsOut; i++) await Wait(0.1);

        // ---- phase 3: a call answered at the ear ----
        XrPhone.SimGrip = false;
        await Wait(0.3);
        XrPad.Tap(PlayerInput.Phone);
        await Wait(0.8);
        Check(phone.IsOut, "the wrist menu's Phone takes it out without a grip");
        phone.SimCall = 42;
        BeforeEyes();
        await Wait(0.5);
        Check(phone.SimAnswered != 42, "a ringing call is not answered before the eyes");
        AtEar();
        await Wait(0.4);
        Check(phone.SimAnswered == 42, $"raised to the ear, it answers ({phone.SimAnswered})");
        phone.SimCall = null;

        BeforeEyes();
        await Button(JoyButton.X, 1.0);
        Check(me.PhoneState == FootPlayer.PhoneOpenState, "X opens it");
        await Button(JoyButton.X, 0.8);
        Check(me.PhoneState == FootPlayer.PhoneFoldedState, "X folds it");
        await Button(JoyButton.B, 0.8);
        Check(!phone.IsOut, "B puts the folded phone away");

        // ---- phase 3: let go away from the pocket, it flies home; at the pocket, it goes in ----
        await FromPocket();
        BeforeEyes();
        await Wait(0.4);
        XrPhone.SimGrip = false;
        await Wait(0.25);
        var hanging = me.PhoneModel?.GlobalPosition;
        Check(phone.IsOut && hanging is { } h && h.DistanceTo(Head * new Vector3(-0.02f, -0.16f, -0.32f)) < 0.08f, "let go before the eyes, it hangs there a moment");
        await Wait(1.4);
        Check(!phone.IsOut, "then flies home to the pocket");

        await FromPocket();
        XrPhone.SimGrip = false;
        await Wait(1.2);
        Check(!phone.IsOut, "let go at the pocket, it goes in");

        XrPhone.SimHold = null;
        XrPhone.SimTip = null;
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAIL {_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>The pointing hand's trigger pulled and let go.</summary>
    private async Task Trigger()
    {
        XrPhone.SimRayTrigger = true;
        await Wait(0.15);
        XrPhone.SimRayTrigger = false;
        await Wait(0.2);
    }

    /// <summary>A grip closing at the left front pocket.</summary>
    private async Task FromPocket()
    {
        XrPhone.SimGrip = false;
        XrPhone.SimHold = Head * new Transform3D(Basis.Identity, new Vector3(-0.2f, -0.78f, -0.08f));
        await Wait(0.1);
        XrPhone.SimGrip = true;
        await Wait(0.4);
    }

    /// <summary>The hand placed so the phone's own frame is <paramref name="phone"/>.</summary>
    private static void Hold(Transform3D phone) =>
        XrPhone.SimHold = phone * XrPhone.InPalm(leftHanded: false).AffineInverse();

    /// <summary>
    /// The phone 32 cm before the eyes, a little low and left, its screen to them; rolled by
    /// <paramref name="roll"/> degrees, or <paramref name="turned"/> round so its back faces them.
    /// </summary>
    private void BeforeEyes(float roll = 0f, bool turned = false, float low = 0.16f)
    {
        var head = Head;
        var at = head * new Vector3(-0.02f, -low, -0.32f);
        var z = (head.Origin - at).Normalized();
        var y = (Vector3.Up - z * z.Dot(Vector3.Up)).Normalized();
        var basis = new Basis(y.Cross(z), y, z);
        if (turned) basis = new Basis(-basis.X, basis.Y, -basis.Z);
        if (roll != 0f) basis = new Basis(basis.Z, Mathf.DegToRad(roll)) * basis;
        Hold(new Transform3D(basis, at));
    }

    /// <summary>The open phone's inner screen 8 cm before the eyes, square to them.</summary>
    private void AtEyes()
    {
        var head = Head;
        var basis = head.Basis.Orthonormalized();
        var screen = head * new Vector3(0f, 0f, -0.08f);
        Hold(new Transform3D(basis, screen - basis * PhoneModel.InnerScreenCentre));
    }

    /// <summary>The folded phone's cover against the left ear, facing it.</summary>
    private void AtEar()
    {
        var head = Head;
        var b = head.Basis.Orthonormalized();
        var z = b.X;   // the cover faces the ear, from outside the head's left
        var basis = new Basis(b.Y.Cross(z), b.Y, z);
        var ear = head * new Vector3(-0.075f, -0.02f, 0.01f);
        Hold(new Transform3D(basis, ear - basis * PhoneModel.CoverScreenCentre - z * 0.01f));
    }

    /// <summary>
    /// The free hand on the front half's loose edge, swung from <paramref name="from"/> to
    /// <paramref name="to"/> (the fold, 0 shut, 1 flat) about the hinge; it grips as it starts
    /// unless it already holds.
    /// </summary>
    private async Task Swing(FootPlayer me, float from, float to, bool grip = true)
    {
        if (me.PhoneModel is not { } model) return;
        var frame = model.GlobalTransform.Orthonormalized();
        var hinge = new Vector3(-PhoneModel.HalfWidth * 0.5f, 0f, 0f);
        Vector3 Edge(float fold)
        {
            float a = Mathf.Pi * Mathf.SmoothStep(0f, 1f, fold);
            return frame * (hinge + new Vector3(PhoneModel.HalfWidth * Mathf.Cos(a), 0f, PhoneModel.HalfWidth * Mathf.Sin(a)));
        }
        XrPhone.SimFree = Edge(from);
        if (grip)
        {
            XrPhone.SimFreeGrip = false;
            await Wait(0.1);
            XrPhone.SimFreeGrip = true;
            await Wait(0.1);
        }
        for (int i = 1; i <= 12; i++)
        {
            XrPhone.SimFree = Edge(Mathf.Lerp(from, to, i / 12f));
            await Wait(0.04);
        }
        await Wait(0.1);
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
        if (!Windowed) return;
        string path = ProjectSettings.GlobalizePath("res://test_output/" + file);
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"screenshot {path}");
    }
}
