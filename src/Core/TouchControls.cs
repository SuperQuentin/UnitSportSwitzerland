using Godot;

namespace UnitSport.Core;

/// <summary>
/// The phone's controls (#63): an on-screen pad. A floating left stick, a right-hand drag that
/// turns the camera 1:1, and the pad's buttons (A/B/X/Y, shoulders, triggers, d-pad, stick
/// clicks, Start, Back). It plays them as a virtual gamepad on <see cref="Device"/>, the way
/// <c>XR.XrPad</c> replays the Touch controllers, so every action in every mode works through
/// the pad bindings <see cref="PlayerInput"/> already has, and the prompts name the same buttons.
///
/// <para>
/// Each button is labelled with what it does here: the first prompt (<see cref="Source"/>, the
/// same pairs as the prompt bar) whose action that button is bound to. The overlay is shown on a
/// touchscreen while the game holds the pointer (no menu open, no text field). Touching a real pad
/// hides it until the screen is touched again. Rule note: docs/notes/core/touch-controls.md.
/// </para>
/// </summary>
public partial class TouchControls : CanvasLayer
{
    /// <summary>The virtual pad's device id: never a real pad's (XrPad is 7).</summary>
    public const int Device = 8;

    /// <summary>Whether the overlay is on screen now (prompts, rumble and look ask).</summary>
    public static bool Shown { get; private set; }

    /// <summary>What applies right now, as the prompt bar has it: labels the buttons.</summary>
    public Func<IEnumerable<(string Action, string Text)>>? Source { get; set; }

    private const float StickRadius = 70f, KnobRadius = 30f;
    // the stick takes any touch in the lower left; the look drag any other touch off a button
    private const float StickZoneWidth = 0.42f, StickZoneTop = 0.3f;
    private const float LookRadPerPixel = 0.0022f; // the mouse's rate (FootPlayer), scaled by the setting

    private sealed class Key
    {
        public string Pad = "";
        public JoyButton Button = JoyButton.Invalid;
        public JoyAxis Trigger = JoyAxis.Invalid;
        public Vector2 Centre;
        public float Radius;
        public string Label = "";
        public bool Live;  // some prompt here uses it
        public int Finger = -1;
    }

    private readonly List<Key> _keys = new();
    private Control _canvas = null!;
    private int _stickFinger = -1, _lookFinger = -1;
    private Vector2 _stickOrigin, _stickAt;
    private bool _padInUse;
    private double _labelTimer;
    private Font _font = null!;

    public static TouchControls Create() => new() { Name = "TouchControls" };

    /// <summary>Whether this device should get the overlay at all.</summary>
    public static bool Wanted => Platform.IsMobile && DisplayServer.GetName() != "headless";

    public override void _Ready()
    {
        Layer = 13; // over the prompts (11), under the inventory (12+) only by being hidden then, and the menus (40)
        ProcessMode = ProcessModeEnum.Always;
        _font = ThemeDB.FallbackFont;
        _canvas = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _canvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _canvas.Draw += Paint;
        AddChild(_canvas);
        // a desktop --mobile run: the mouse plays the finger
        if (!OS.HasFeature("mobile")) Input.EmulateTouchFromMouse = true;
        GetViewport().SizeChanged += Layout;
        Layout();
    }

    public override void _ExitTree()
    {
        ReleaseAll();
        Shown = false;
    }

    private void Layout()
    {
        var size = _canvas.Size == Vector2.Zero ? GetViewport().GetVisibleRect().Size : _canvas.Size;
        float w = size.X, h = size.Y;
        _keys.Clear();
        // face buttons, bottom right, the Xbox diamond
        var face = new Vector2(w - 118, h - 118);
        AddButton("A", JoyButton.A, face + new Vector2(0, 54), 32);
        AddButton("B", JoyButton.B, face + new Vector2(54, 0), 32);
        AddButton("X", JoyButton.X, face + new Vector2(-54, 0), 32);
        AddButton("Y", JoyButton.Y, face + new Vector2(0, -54), 32);
        // triggers left of the diamond, where the thumb rolls onto them (throttle and brake)
        AddTrigger("RT", JoyAxis.TriggerRight, new Vector2(w - 262, h - 150), 36);
        AddTrigger("LT", JoyAxis.TriggerLeft, new Vector2(w - 262, h - 62), 36);
        // shoulders above
        AddButton("RB", JoyButton.RightShoulder, new Vector2(w - 70, h - 248), 26);
        AddButton("LB", JoyButton.LeftShoulder, new Vector2(w - 150, h - 248), 26);
        // stick clicks: sprint / fly boost, and the camera
        AddButton("L3", JoyButton.LeftStick, new Vector2(w - 352, h - 62), 24);
        AddButton("R3", JoyButton.RightStick, new Vector2(w - 352, h - 140), 24);
        // d-pad, top left: lights, horn, engine, wheels
        var pad = new Vector2(84, 150);
        AddButton("↑", JoyButton.DpadUp, pad + new Vector2(0, -40), 21);
        AddButton("↓", JoyButton.DpadDown, pad + new Vector2(0, 40), 21);
        AddButton("←", JoyButton.DpadLeft, pad + new Vector2(-40, 0), 21);
        AddButton("→", JoyButton.DpadRight, pad + new Vector2(40, 0), 21);
        // Back (inventory) and Start (pause), top right
        AddButton("II", JoyButton.Start, new Vector2(w - 46, 46), 24);
        AddButton("Bag", JoyButton.Back, new Vector2(w - 110, 46), 24);
        _labelTimer = 0;
        _canvas.QueueRedraw();
    }

    /// <summary>Where a button is drawn (for <see cref="TouchCheck"/>), or the middle of the screen.</summary>
    public Vector2 CentreOf(JoyButton button)
    {
        foreach (var k in _keys)
            if (k.Button == button) return k.Centre;
        return _canvas.Size / 2;
    }

    private void AddButton(string pad, JoyButton button, Vector2 centre, float radius) =>
        _keys.Add(new Key { Pad = pad, Button = button, Centre = centre, Radius = radius });

    private void AddTrigger(string pad, JoyAxis axis, Vector2 centre, float radius) =>
        _keys.Add(new Key { Pad = pad, Trigger = axis, Centre = centre, Radius = radius });

    public override void _Process(double delta)
    {
        // in game = the pointer is held; a probe or --nocapture run never holds it, so it counts as in game
        bool inGame = Input.MouseMode != Input.MouseModeEnum.Visible || MouseCapture.Disabled;
        bool show = !_padInUse && inGame && !UiFocus.TextEntryActive;
        if (show != Shown)
        {
            Shown = show;
            Visible = show;
            if (!show) ReleaseAll();
            PlayerInput.HintsChanged();
        }
        if (!show) return;
        _labelTimer -= delta;
        if (_labelTimer > 0) return;
        _labelTimer = 0.25;
        if (Relabel()) _canvas.QueueRedraw();
    }

    /// <summary>Each button takes the first prompt whose action it is bound to. True if any label changed.</summary>
    private bool Relabel()
    {
        bool changed = false;
        _prompts.Clear();
        if (Source != null) _prompts.AddRange(Source());
        foreach (var k in _keys)
        {
            string label = "";
            foreach (var (action, text) in _prompts)
                if (BoundTo(action, k)) { label = text; break; }
            bool live = label.Length > 0;
            if (label != k.Label || live != k.Live)
            {
                k.Label = label;
                k.Live = live;
                changed = true;
            }
        }
        return changed;
    }

    private readonly List<(string Action, string Text)> _prompts = new();
    private static readonly Dictionary<string, Godot.Collections.Array<InputEvent>> Events = new();

    private static bool BoundTo(string action, Key k)
    {
        if (!InputMap.HasAction(action)) return false;
        if (!Events.TryGetValue(action, out var events)) Events[action] = events = InputMap.ActionGetEvents(action);
        foreach (var e in events)
        {
            if (k.Button != JoyButton.Invalid && e is InputEventJoypadButton b && b.ButtonIndex == k.Button) return true;
            if (k.Trigger != JoyAxis.Invalid && e is InputEventJoypadMotion m && m.Axis == k.Trigger) return true;
        }
        return false;
    }

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            // a real pad in hand: get out of the way until the screen is touched again
            case InputEventJoypadButton b when b.Device != Device && b.Device != XR.XrPad.DeviceId:
            case InputEventJoypadMotion m when m.Device != Device && m.Device != XR.XrPad.DeviceId && Mathf.Abs(m.AxisValue) > 0.5f:
                _padInUse = true;
                return;
            case InputEventScreenTouch t:
                _padInUse = false;
                if (!Shown) return;
                if (t.Pressed) Down(t.Index, t.Position);
                else Up(t.Index);
                GetViewport().SetInputAsHandled();
                return;
            case InputEventScreenDrag d when Shown:
                Drag(d.Index, d.Position, d.Relative);
                GetViewport().SetInputAsHandled();
                return;
        }
    }

    private void Down(int finger, Vector2 at)
    {
        foreach (var k in _keys)
            if (k.Finger < 0 && at.DistanceTo(k.Centre) <= k.Radius + 10)
            {
                k.Finger = finger;
                Send(k, true);
                _canvas.QueueRedraw();
                return;
            }
        var size = _canvas.Size;
        if (_stickFinger < 0 && at.X < size.X * StickZoneWidth && at.Y > size.Y * StickZoneTop)
        {
            _stickFinger = finger;
            _stickOrigin = _stickAt = at;
            _canvas.QueueRedraw();
            return;
        }
        if (_lookFinger < 0) _lookFinger = finger;
    }

    private void Up(int finger)
    {
        foreach (var k in _keys)
            if (k.Finger == finger)
            {
                k.Finger = -1;
                Send(k, false);
                _canvas.QueueRedraw();
            }
        if (finger == _stickFinger)
        {
            _stickFinger = -1;
            Stick(Vector2.Zero);
            _canvas.QueueRedraw();
        }
        if (finger == _lookFinger) _lookFinger = -1;
    }

    private void Drag(int finger, Vector2 at, Vector2 relative)
    {
        if (finger == _stickFinger)
        {
            _stickAt = at;
            Stick((at - _stickOrigin) / StickRadius);
            _canvas.QueueRedraw();
        }
        else if (finger == _lookFinger)
        {
            // the camera reads mouse motion: this one is marked as ours (PlayerInput.IsLookMotion)
            float scale = GameSettings.Current.TouchLookSpeed;
            Input.ParseInputEvent(new InputEventMouseMotion { Device = Device, Relative = relative * scale, Position = at });
        }
    }

    private void Stick(Vector2 v)
    {
        v = v.LimitLength(1f);
        Axis(JoyAxis.LeftX, v.X);
        Axis(JoyAxis.LeftY, v.Y);
    }

    private static void Send(Key k, bool pressed)
    {
        if (k.Trigger != JoyAxis.Invalid) Axis(k.Trigger, pressed ? 1f : 0f);
        else Input.ParseInputEvent(new InputEventJoypadButton { Device = Device, ButtonIndex = k.Button, Pressed = pressed });
        if (pressed && GameSettings.Current.Vibration) Input.VibrateHandheld(12, 0.3f);
    }

    private static void Axis(JoyAxis axis, float value) =>
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = Device, Axis = axis, AxisValue = value });

    private void ReleaseAll()
    {
        foreach (var k in _keys)
            if (k.Finger >= 0)
            {
                k.Finger = -1;
                Send(k, false);
            }
        if (_stickFinger >= 0) Stick(Vector2.Zero);
        _stickFinger = _lookFinger = -1;
    }

    private static readonly Color Fill = new(0.08f, 0.09f, 0.11f, 0.38f), FillDown = new(1f, 0.78f, 0.3f, 0.55f),
        Ring = new(1f, 1f, 1f, 0.55f), RingDim = new(1f, 1f, 1f, 0.22f), Text = new(1f, 1f, 1f, 0.92f), TextDim = new(1f, 1f, 1f, 0.4f);

    private void Paint()
    {
        foreach (var k in _keys)
        {
            bool down = k.Finger >= 0;
            _canvas.DrawCircle(k.Centre, k.Radius, down ? FillDown : Fill);
            _canvas.DrawArc(k.Centre, k.Radius, 0, Mathf.Tau, 40, k.Live ? Ring : RingDim, 2f, true);
            CentredText(k.Pad, k.Centre + new Vector2(0, 6), 17, k.Live ? Text : TextDim);
            if (k.Live) CentredText(k.Label, k.Centre + new Vector2(0, k.Radius + 15), 12, Text);
        }
        if (_stickFinger >= 0)
        {
            _canvas.DrawCircle(_stickOrigin, StickRadius, Fill);
            _canvas.DrawArc(_stickOrigin, StickRadius, 0, Mathf.Tau, 48, Ring, 2f, true);
            var knob = _stickOrigin + (_stickAt - _stickOrigin).LimitLength(StickRadius);
            _canvas.DrawCircle(knob, KnobRadius, FillDown);
        }
        else
        {
            // where the stick lives, until a thumb lands there
            var size = _canvas.Size;
            var home = new Vector2(150, size.Y - 150);
            _canvas.DrawArc(home, StickRadius, 0, Mathf.Tau, 48, RingDim, 2f, true);
        }
    }

    private void CentredText(string text, Vector2 at, int size, Color color)
    {
        float width = _font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        _canvas.DrawStringOutline(_font, at - new Vector2(width / 2, 0), text, HorizontalAlignment.Left, -1, size, 4, new Color(0, 0, 0, 0.8f));
        _canvas.DrawString(_font, at - new Vector2(width / 2, 0), text, HorizontalAlignment.Left, -1, size, color);
    }
}
