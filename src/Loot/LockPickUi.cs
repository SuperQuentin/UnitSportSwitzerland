using Godot;
using UnitSport.Audio;
using UnitSport.Core;

namespace UnitSport.Loot;

/// <summary>
/// The dial of a gun locker or safe (#165): turn it until a tumbler clicks, stop on the click and
/// let it settle, then turn the other way for the next one. Three tumblers on a gun locker, four
/// on a safe (narrower windows). A/D, the arrows or the left stick turn it (Shift / Sprint slows
/// it), dragging the dial with the mouse works too; E, B or Esc walks away. The mouse wheel is
/// left alone on purpose: it belongs to the hotbar.
///
/// <para>
/// Feedback: a faint tick per number, a grittier one near the next number when turning the right
/// way, a loud click (and the ring flashing) on it; overshoot before it settles and the click is
/// lost. Every tumbler set, the whole combination goes to the server
/// (<see cref="LootService.SubmitCombination"/>), which checks it and opens the door for everyone.
/// Notes: <c>docs/notes/loot/locked-containers.md</c>.
/// </para>
/// </summary>
public partial class LockPickUi : CanvasLayer
{
    /// <summary>Seconds the dial must rest on a click for the tumbler to drop.</summary>
    public const float SettleSeconds = 0.35f;
    private const float Speed = 14f, SlowSpeed = 3.5f;   // numbers per second
    private const float NearWindow = 7f;

    private sealed partial class View : Control
    {
        public Action<View>? Drawer;
        public override void _Draw() => Drawer?.Invoke(this);
    }

    private readonly LootService _service;
    private readonly View _view = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private string _what = "";
    private int[] _combo = Array.Empty<int>();
    private float _tolerance = 1.5f;
    private int _stage;
    private float _dial;
    private bool _clicking;
    private float _settle;
    private bool _waiting;
    private float _flash, _shake;
    private bool _dragging;
    private float _dragDelta;
    private float _lastDial;

    public LockPickUi(LootService service) => _service = service;
    public LockPickUi() : this(null!) { }

    public bool IsOpen => _view.Visible;
    /// <summary>Tumblers dropped so far (for probes).</summary>
    public int Stage => _stage;
    public int Tumblers => _combo.Length;
    public float Dial => _dial;
    /// <summary>A click is held and settling (for probes).</summary>
    public bool Clicking => _clicking;

    public override void _Ready()
    {
        Layer = 12;
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _view.Drawer = Draw;
        _view.Visible = false;
        _view.GuiInput += OnGuiInput;
        AddChild(_view);
    }

    public override void _ExitTree() => UiFocus.Set(this, false);

    /// <summary>Clockwise (numbers rising) for the first tumbler, then alternating, like a real safe.</summary>
    public static int Direction(int stage) => stage % 2 == 0 ? 1 : -1;

    public void Open(string what, int[] combo, float tolerance)
    {
        _what = what;
        _combo = combo;
        _tolerance = tolerance;
        _stage = 0;
        _dial = _lastDial = (float)GD.RandRange(0, 99);
        _clicking = false;
        _waiting = false;
        _settle = _flash = _shake = 0;
        _view.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        _service?.PlayAt(SfxSynth.Tick, 0.6f, -6);
    }

    public void Close()
    {
        if (!IsOpen) return;
        _view.Visible = false;
        _dragging = false;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    /// <summary>The server said no (a stale period): every tumbler falls back.</summary>
    public void Refused()
    {
        _waiting = false;
        _stage = 0;
        _clicking = false;
        _shake = 1f;
        _service?.PlayAt(SfxSynth.Impact, 0.7f, -4);
    }

    /// <summary>Turns the dial by some numbers (positive = clockwise). Input and probes both come through here.</summary>
    public void Turn(float numbers)
    {
        if (!IsOpen || _waiting || numbers == 0) return;
        float old = _dial;
        _dial = Mathf.PosMod(_dial + numbers, 100f);
        int dir = Math.Sign(numbers);
        if (_stage >= _combo.Length) return;

        int target = _combo[_stage];
        bool right = dir == Direction(_stage);
        bool inWindow = LootTables.DialDistance(_dial, target) <= _tolerance;
        // a tick per whole number passed; near the number (turning the right way) it rasps lower
        if ((int)Mathf.Floor(old) != (int)Mathf.Floor(_dial) && !inWindow)
        {
            bool near = right && LootTables.DialDistance(_dial, target) <= NearWindow;
            _service?.PlayAt(SfxSynth.Tick, near ? 1.15f : 1.9f, near ? -14 : -22);
        }
        if (inWindow && right && !_clicking)
        {
            _clicking = true;
            _settle = 0;
            _flash = 1f;
            _service?.PlayAt(SfxSynth.Tick, 0.55f, 0);   // the click
        }
        else if (_clicking && (!inWindow || !right))
        {
            _clicking = false;   // overshot or turned back: the click is lost
            _service?.PlayAt(SfxSynth.Tick, 2.6f, -12);
        }
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        float dt = (float)delta;
        float axis = Input.GetAxis(PlayerInput.MoveLeft, PlayerInput.MoveRight);
        if (Input.IsKeyPressed(Key.Left)) axis -= 1;
        if (Input.IsKeyPressed(Key.Right)) axis += 1;
        axis = Mathf.Clamp(axis, -1, 1);
        if (Mathf.Abs(axis) < 0.15f) axis = 0;
        bool slow = Input.IsKeyPressed(Key.Shift) || Input.IsActionPressed(PlayerInput.Sprint);
        Turn(axis * (slow ? SlowSpeed : Speed) * dt + _dragDelta);
        _dragDelta = 0;
        // how far the dial went since last frame, whoever turned it (keys, drag, a probe)
        float moved = LootTables.DialDistance(_dial, _lastDial);
        _lastDial = _dial;

        if (_clicking && !_waiting)
        {
            // only a dial at rest drops the tumbler: creeping on through the window does not count
            if (moved < 0.02f) _settle += dt;
            else _settle = Mathf.Max(0, _settle - dt);
            if (_settle >= SettleSeconds) Drop();
        }
        _flash = Mathf.Max(0, _flash - dt * 2.5f);
        _shake = Mathf.Max(0, _shake - dt * 2f);
        _view.QueueRedraw();
    }

    private void Drop()
    {
        _clicking = false;
        _settle = 0;
        _stage++;
        _flash = 1f;
        _service?.PlayAt(SfxSynth.Impact, 1.5f, -4);
        if (_stage < _combo.Length) return;
        _waiting = true;
        _service?.PlayAt(SfxSynth.Impact, 0.9f, -2);
        _service?.SubmitCombination((int[])_combo.Clone());
    }

    private Vector2 DialCentre => _view.Size * 0.5f + new Vector2(0, -10);

    private void OnGuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _dragging = mb.Pressed;
            _view.AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            // the angle swept around the dial's centre, in dial numbers (clockwise positive)
            var c = DialCentre;
            var a = mm.Position - mm.Relative - c;
            var b = mm.Position - c;
            if (a.LengthSquared() > 100 && b.LengthSquared() > 100)
                _dragDelta += a.AngleTo(b) / Mathf.Tau * 100f;
            _view.AcceptEvent();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)
            || e.IsActionPressed(PlayerInput.InteractMount) || e.IsActionPressed(PlayerInput.Inventory))
        {
            _service.StopPicking();
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- drawing --------------------------------------------------------------------------------

    private void Draw(View v)
    {
        var font = ThemeDB.FallbackFont;
        var size = v.Size;
        var c = DialCentre + new Vector2(Mathf.Sin(_shake * 40f) * 8f * _shake, 0);
        const float R = 120f;

        var panel = new Rect2(c - new Vector2(220, 200), new Vector2(440, 440));
        v.DrawRect(panel, new Color(0.05f, 0.06f, 0.07f, 0.92f));
        v.DrawRect(panel, new Color(0.35f, 0.38f, 0.42f), false, 2f);
        string title = $"Crack the {_what}";
        v.DrawString(font, new Vector2(panel.Position.X, panel.Position.Y + 30), title, HorizontalAlignment.Center, panel.Size.X, 20, Colors.White);

        // the dial: numbers turn under the fixed mark at the top
        var ring = _flash > 0 ? new Color(0.98f, 0.82f, 0.30f).Lerp(new Color(0.55f, 0.57f, 0.6f), 1 - _flash) : new Color(0.55f, 0.57f, 0.6f);
        v.DrawCircle(c, R + 10, new Color(0.16f, 0.17f, 0.19f));
        v.DrawArc(c, R + 10, 0, Mathf.Tau, 64, ring, 3f, true);
        v.DrawCircle(c, R - 26, new Color(0.10f, 0.10f, 0.11f));
        for (int n = 0; n < 100; n += 2)
        {
            float ang = -(n - _dial) / 100f * Mathf.Tau - Mathf.Pi / 2;   // turning clockwise raises the number under the mark
            var dirv = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            bool major = n % 10 == 0;
            v.DrawLine(c + dirv * (R - (major ? 14 : 7)), c + dirv * R, new Color(0.85f, 0.86f, 0.88f), major ? 2.5f : 1.2f);
            if (major)
            {
                var at = c + dirv * (R - 26);
                v.DrawString(font, at + new Vector2(-14, 6), n.ToString(), HorizontalAlignment.Center, 28, 13, new Color(0.8f, 0.82f, 0.85f));
            }
        }
        // the knob, with a grip line toward the mark
        v.DrawCircle(c, 34, new Color(0.30f, 0.31f, 0.34f));
        float knob = _dial / 100f * Mathf.Tau - Mathf.Pi / 2;
        v.DrawLine(c, c + new Vector2(Mathf.Cos(knob), Mathf.Sin(knob)) * 30, new Color(0.75f, 0.76f, 0.8f), 4f);
        // the fixed mark
        var top = c + new Vector2(0, -R - 14);
        v.DrawColoredPolygon(new[] { top + new Vector2(-9, -12), top + new Vector2(9, -12), top + new Vector2(0, 2) }, new Color(0.95f, 0.3f, 0.2f));
        v.DrawString(font, new Vector2(c.X - 30, top.Y - 18), $"{Mathf.RoundToInt(_dial) % 100:00}", HorizontalAlignment.Center, 60, 16, Colors.White);

        // tumblers
        float y = c.Y + R + 34;
        float x0 = c.X - (_combo.Length - 1) * 18f;
        for (int i = 0; i < _combo.Length; i++)
        {
            var col = i < _stage ? new Color(0.35f, 0.9f, 0.4f)
                : i == _stage && _clicking ? new Color(0.98f, 0.75f, 0.2f).Lerp(Colors.White, _flash)
                : new Color(0.35f, 0.36f, 0.4f);
            v.DrawCircle(new Vector2(x0 + i * 36f, y), 9, col);
            if (i == _stage && _clicking)   // how far the settle has got
                v.DrawArc(new Vector2(x0 + i * 36f, y), 13, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * _settle / SettleSeconds, 24, col, 2f);
        }

        string status = _waiting ? "Opening…"
            : _stage < _combo.Length ? (Direction(_stage) > 0 ? "Turn right (clockwise) until it clicks, then stop" : "Turn left (anticlockwise) until it clicks, then stop")
            : "";
        v.DrawString(font, new Vector2(panel.Position.X, y + 34), status, HorizontalAlignment.Center, panel.Size.X, 14, new Color(0.85f, 0.87f, 0.9f));
        v.DrawString(font, new Vector2(panel.Position.X, y + 54), (InputHints.Pad ? InputHints.Format("{move_right} to turn · {sprint} slow · {interact_mount} / {ui_cancel} leave")
            : InputHints.Format("{move_left}/{move_right}, arrows or drag to turn · Shift slow · {interact_mount} / {menu} leave")),
            HorizontalAlignment.Center, panel.Size.X, 12, new Color(0.6f, 0.62f, 0.66f));
    }
}
