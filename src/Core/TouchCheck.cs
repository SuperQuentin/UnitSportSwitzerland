using Godot;
using UnitSport.Player;

namespace UnitSport.Core;

/// <summary>
/// <c>--touchcheck --mobile --world fixture</c> (#63): the touch overlay drives the player through
/// the same screen events a phone sends. A thumb on the left stick pushed up walks forward, a drag on
/// the right turns the camera, and A jumps. Prints <c>[touchcheck] RESULT: ok</c> and quits 0, or
/// FAILED and 1. Touches are sent in window pixels, as a phone sends them, so any
/// <c>--resolution</c> works.
/// </summary>
public partial class TouchCheck : Node
{
    private const double Timeout = 60;
    private readonly Func<FootPlayer?> _local;
    private readonly TouchControls _touch;
    private double _t, _phaseT;
    private int _phase;
    private Vector3 _from, _look;
    private float _startY, _peakY;
    private readonly List<string> _fails = new();
    private bool _released;

    public static bool Requested => CmdArgs.Has("--touchcheck");

    public TouchCheck(Func<FootPlayer?> local, TouchControls touch)
    {
        Name = "TouchCheck";
        _local = local;
        _touch = touch;
    }

    public override void _Process(double delta)
    {
        _t += delta;
        _phaseT += delta;
        if (_t > Timeout) { Finish($"timed out in phase {_phase}"); return; }
        var cam = GetViewport().GetCamera3D();
        var size = GetViewport().GetVisibleRect().Size;
        var stick = new Vector2(150, size.Y - 150);
        var me = _local();
        if (_phase == 0)
        {
            // the fixture world starts in the fly camera: the overlay's d-pad down is "Walk"
            if (_phaseT < 3 || !TouchControls.Shown) return;
            var walk = _touch.CentreOf(JoyButton.DpadDown);
            Touch(3, walk, true);
            Touch(3, walk, false);
            Next();
            return;
        }
        if (me == null || cam == null) return;
        switch (_phase)
        {
            case 1: // on foot and on the ground: thumb down on the stick
                if (_phaseT < 2 || !me.IsOnFloor())
                {
                    if (_phaseT > 10) Finish($"never on foot: overlay {TouchControls.Shown}, on floor {me.IsOnFloor()}, mouse {Input.MouseMode}");
                    return;
                }
                _from = me.GlobalPosition;
                Touch(0, stick, true);
                Next();
                break;
            case 2: // thumb up on the stick: walk forward
                Drag(0, stick + new Vector2(0, -70), new Vector2(0, -2));
                if (_phaseT < 2) return;
                Touch(0, stick + new Vector2(0, -70), false);
                float walked = Flat(me.GlobalPosition - _from);
                GD.Print($"[touchcheck] stick: walked {walked:F2} m");
                if (walked < 1.5f) _fails.Add($"stick moved the player only {walked:F2} m");
                _look = -cam.GlobalBasis.Z;
                var at = new Vector2(size.X * 0.6f, size.Y * 0.4f);
                Touch(1, at, true);
                Drag(1, at + new Vector2(240, 0), new Vector2(240, 0));
                Touch(1, at + new Vector2(240, 0), false);
                Next();
                break;
            case 3: // the drag turned the camera
                if (_phaseT < 0.5) return;
                float turned = Mathf.RadToDeg(Flat2(_look).AngleTo(Flat2(-cam.GlobalBasis.Z)));
                GD.Print($"[touchcheck] look drag: turned {turned:F1} deg");
                if (Mathf.Abs(turned) < 10) _fails.Add($"a 240 px drag turned the camera only {turned:F1} deg");
                _startY = _peakY = me.GlobalPosition.Y;
                Touch(2, _touch.CentreOf(JoyButton.A), true);
                Next();
                break;
            case 4: // A: jump
                _peakY = Math.Max(_peakY, me.GlobalPosition.Y);
                if (_phaseT > 0.15 && !_released) { Touch(2, _touch.CentreOf(JoyButton.A), false); _released = true; }
                if (_phaseT < 1.2) return;
                GD.Print($"[touchcheck] A: rose {_peakY - _startY:F2} m");
                if (_peakY - _startY < 0.3f) _fails.Add($"A lifted the player only {_peakY - _startY:F2} m");
                Finish(null);
                break;
        }
    }

    private void Next()
    {
        _phase++;
        _phaseT = 0;
    }

    // a phone's touches come in window pixels, which the viewport scales to the UI's canvas: send
    // the overlay's canvas positions back through that scale, as a finger on them would arrive
    private Vector2 Window(Vector2 canvas) => GetViewport().GetFinalTransform() * canvas;

    private void Touch(int finger, Vector2 at, bool pressed) =>
        Input.ParseInputEvent(new InputEventScreenTouch { Index = finger, Position = Window(at), Pressed = pressed });

    private void Drag(int finger, Vector2 at, Vector2 relative) =>
        Input.ParseInputEvent(new InputEventScreenDrag { Index = finger, Position = Window(at), Relative = GetViewport().GetFinalTransform().BasisXform(relative) });

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
    private static Vector2 Flat2(Vector3 v) => new Vector2(v.X, v.Z).Normalized();

    private void Finish(string? error)
    {
        if (error != null) _fails.Add(error);
        foreach (var f in _fails) GD.PrintErr($"[touchcheck] {f}");
        bool ok = _fails.Count == 0;
        GD.Print(ok ? "[touchcheck] RESULT: ok" : "[touchcheck] RESULT: FAILED");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
