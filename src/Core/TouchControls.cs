using Godot;

namespace UnitSport.Core;

/// <summary>
/// The phone's controls (#63, reworked in #1043): a floating left stick, a drag that turns the
/// camera 1:1, and a few big buttons picked by what the player is doing (<see cref="TouchLayout"/>),
/// with labelled pills for what applies right now (the prompts) and a More sheet for the rest
/// (<see cref="TouchMore"/>). Everything is played as a virtual gamepad on <see cref="Device"/>,
/// the way <c>XR.XrPad</c> replays the Touch controllers, so every action works through the pad
/// bindings <see cref="PlayerInput"/> already has; actions with no pad button (the map, the bag,
/// the hotbar's slots) are sent as themselves.
///
/// <para>
/// The overlay is shown on a touchscreen while the game holds the pointer (no menu open, no text
/// field). Touching a real pad hides it until the screen is touched again. Rule note:
/// docs/notes/core/touch-controls.md.
/// </para>
/// </summary>
public partial class TouchControls : CanvasLayer
{
    /// <summary>The virtual pad's device id: never a real pad's (XrPad is 7).</summary>
    public const int Device = 8;

    /// <summary>Whether the overlay is on screen now (prompts, rumble and look ask).</summary>
    public static bool Shown { get; private set; }

    private static TouchControls? _current;

    /// <summary>
    /// Hints name the on-screen controls (#1054, <see cref="InputDevice.Touch"/>): the overlay exists
    /// and no real pad is in hand. Menus too, though the overlay hides there.
    /// </summary>
    public static bool Hinting => _current is { _padInUse: false };

    /// <summary>What applies right now, as the prompt bar has it: labels the buttons and makes the pills.</summary>
    public Func<IEnumerable<(string Action, string Text)>>? Source { get; set; }

    /// <summary>What the player is doing: picks the layout.</summary>
    public Func<TouchState>? State { get; set; }

    /// <summary>Where the hotbar goes on a phone, its bag's cell first (<see cref="Items.InventoryUi"/> reads it), or null.</summary>
    public static Rect2? HotbarPlace { get; private set; }

    /// <summary>The mode on screen now (for <see cref="TouchCheck"/>).</summary>
    public TouchMode Mode => _state.Mode;

    // the stick takes any touch in the lower 70 % of the left 42 % (the right, left-handed)
    private const float StickZoneWidth = 0.42f, StickZoneTop = 0.3f;
    private const double LongPress = 0.5, PillLinger = 0.4, TapUseSeconds = 0.2;
    private const int MaxPills = 4, MaxPinsPerMode = 2;

    /// <summary>One control on screen, with the finger on it.</summary>
    private sealed class Btn
    {
        public required TouchKey Spec;
        public Rect2 Rect;
        public string Label = "";
        public string Prompt = "";
        public int Finger = -1;
        public ulong PressFrame;
        public bool Locked, LockArmed, Hidden;
        public Vector2 Value;
    }

    private sealed class Pill
    {
        public string Action = "", Text = "";
        public TouchSend Send;
        public Rect2 Rect;
        public int Finger = -1;
        public ulong PressFrame;
        /// <summary>When a pill whose prompt has gone disappears; −1 while its prompt lasts.</summary>
        public double GoneUntil = -1;
    }

    private readonly List<Btn> _keys = new();
    private readonly List<Pill> _pills = new();
    private readonly TouchMore _more = new();
    private Control _canvas = null!;
    private Font _font = null!;
    private readonly StyleBoxFlat _box = new() { AntiAliasing = true };

    private TouchState _state = new(TouchMode.Foot, TouchFlags.None);
    private bool _built;
    private Rect2 _safe;
    private float _pxPerMm = 10f, _k = 10f;
    private bool _left;

    private int _stickFinger = -1, _lookFinger = -1, _wheelFinger = -1, _hotFinger = -1;
    private Vector2 _stickOrigin, _stickAt;
    private bool _runHeld, _runLockArmed, _runLocked;
    private double _tapUntil;
    private float _lookTravel;
    private int _hotSlot = -1;
    private double _hotDropAt;
    private bool _hotDropped;
    private string? _wheelAction;
    private bool _padInUse;
    private double _labelTimer;
    private readonly List<(TouchSend Send, double At, ulong Frame, bool Pressed)> _scheduled = new();

    public static TouchControls Create() => new() { Name = "TouchControls" };

    /// <summary>Whether this device should get the overlay at all.</summary>
    public static bool Wanted => Platform.IsMobile && (DisplayServer.GetName() != "headless" || TouchCheck.Requested);

    /// <summary>Touch holds are real time (docs/notes/core/three-clocks.md): read as deadlines.</summary>
    private static double Now => RealClock.Now;

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
        GetViewport().SizeChanged += Relayout;
        _more.Tapped = MoreTapped;
        _more.Held = TogglePin;
        _current = this;
        PlayerInput.HintsChanged();
    }

    public override void _ExitTree()
    {
        ReleaseAll();
        Shown = false;
        HotbarPlace = null;
        if (_current == this) _current = null;
    }

    // ---- layout --------------------------------------------------------------------------

    /// <summary>Millimetres to canvas pixels, and the safe area, for this screen.</summary>
    private void Measure()
    {
        var size = _canvas.Size == Vector2.Zero ? GetViewport().GetVisibleRect().Size : _canvas.Size;
        var toWindow = GetViewport().GetFinalTransform();
        float windowPerCanvas = Mathf.Max(0.01f, toWindow.X.Length());
        // a phone knows its density; a desktop --mobile preview is a 6-inch phone of the window's height
        bool phone = OS.HasFeature("mobile");
        float dpi = phone ? DisplayServer.ScreenGetDpi() : 0;
        _pxPerMm = dpi >= 100 ? dpi / 25.4f / windowPerCanvas : size.Y / TouchLayout.PhoneHeightMm;
        _safe = new Rect2(Vector2.Zero, size);
        if (phone)
        {
            var area = DisplayServer.GetDisplaySafeArea();
            if (area.Size.X > 0 && area.Size.Y > 0)
            {
                var inverse = toWindow.AffineInverse();
                var a = inverse * (Vector2)area.Position;
                var b = inverse * (Vector2)area.End;
                _safe = new Rect2(a, b - a).Intersection(_safe);
            }
        }
        _safe = _safe.Grow(-1.5f * _pxPerMm);
        var settings = GameSettings.Current;
        _k = _pxPerMm * settings.TouchScale;
        _left = settings.TouchLeftHanded;
    }

    private void Relayout()
    {
        Measure();
        foreach (var key in _keys)
            key.Rect = TouchLayout.Place(key.Spec, _safe, _pxPerMm, GameSettings.Current.TouchScale, _left);
        HotbarPlace = TouchLayout.Hotbar(_safe, _pxPerMm, GameSettings.Current.TouchScale, _left, Items.Inventory.HotbarSize);
        LayoutPills();
        if (_more.Open) OpenMore();
        _canvas.QueueRedraw();
    }

    private static TouchFlags SettingFlags()
    {
        var s = GameSettings.Current;
        var f = TouchFlags.None;
        if (!s.TouchRunPastRing) f |= TouchFlags.RunButton;
        if (s.TouchTapToUse) f |= TouchFlags.TapToUse;
        return f;
    }

    /// <summary>A new mode: let go of everything held, then lay its controls out.</summary>
    private void Rebuild(TouchState state)
    {
        ReleaseAll();
        _state = state;
        _built = true;
        _keys.Clear();
        foreach (var spec in TouchLayout.For(state.Mode, state.Flags))
            _keys.Add(new Btn { Spec = spec, Label = spec.Label });
        _pills.Clear();
        _more.Hide();
        _labelTimer = 0;
        Relayout();
        // what a hint calls a control follows the layout
        PlayerInput.HintsChanged();
    }

    /// <summary>Where a control is drawn (for <see cref="TouchCheck"/>), by its id, or the middle of the screen.</summary>
    public Vector2 CentreOf(string id)
    {
        foreach (var k in _keys)
            if (k.Spec.Id == id) return k.Rect.GetCenter();
        return _canvas.Size / 2;
    }

    /// <summary>Whether this mode has a control with that id.</summary>
    public bool Has(string id) => _keys.Exists(k => k.Spec.Id == id && !k.Hidden);

    /// <summary>Where the pill for an action is, or null.</summary>
    public Vector2? PillOf(string action)
    {
        foreach (var p in _pills)
            if (p.Action == action && p.GoneUntil < 0) return p.Rect.GetCenter();
        return null;
    }

    /// <summary>The pills' texts now, top to bottom (for <see cref="TouchCheck"/>).</summary>
    public IEnumerable<string> PillTexts => _pills.Where(p => p.GoneUntil < 0).Select(p => p.Text);

    /// <summary>Where the stick waits (for <see cref="TouchCheck"/>).</summary>
    public Vector2 StickHome => TouchLayout.StickHome(_safe, _pxPerMm, GameSettings.Current.TouchScale, _left);

    public bool MoreOpen => _more.Open;

    /// <summary>Where a More entry for this action is, with the sheet open; null if it has none.</summary>
    public Vector2? MoreEntryOf(string action) => _more.CentreOf(action);

    // ---- per frame -----------------------------------------------------------------------

    public override void _Process(double delta)
    {
        // in game = the pointer is held; a probe, --nocapture or headless (--touchcheck) run never
        // holds it, so it counts as in game
        bool inGame = MouseCapture.Mode != Input.MouseModeEnum.Visible || MouseCapture.Disabled
            || DisplayServer.GetName() == "headless";
        bool show = !_padInUse && inGame && !UiFocus.TextEntryActive;
        if (show != Shown)
        {
            Shown = show;
            Visible = show;
            if (!show)
            {
                ReleaseAll();
                _more.Hide();
            }
            else _built = false;   // back from a menu: the settings may have changed size, side or flags
            PlayerInput.HintsChanged();
        }
        double now = Now;
        // in the order they were scheduled: a press before its release, and never in the frame it
        // was scheduled, or a slow frame (the emulator, a hitch) eats a tap a poller never sees
        ulong frame = Engine.GetProcessFrames();
        for (int i = 0; i < _scheduled.Count; i++)
            if (now >= _scheduled[i].At && frame >= _scheduled[i].Frame)
            {
                var (send, _, _, pressed) = _scheduled[i];
                _scheduled.RemoveAt(i--);
                Send(send, pressed);
            }
        if (!show) return;

        var state = State?.Invoke() ?? new TouchState(TouchMode.Foot, TouchFlags.None);
        state = state with { Flags = state.Flags | SettingFlags() };
        if (!_built || state.Mode != _state.Mode || state.Flags != _state.Flags) Rebuild(state);
        else if (state.Ride != _state.Ride) _state = state;

        bool redraw = false;
        // a hotbar slot held: its item is dropped
        if (_hotFinger >= 0 && !_hotDropped && now >= _hotDropAt)
        {
            _hotDropped = true;
            Tap(TouchSend.Act(PlayerInput.Slots[_hotSlot]), 0.03);
            Later(TouchSend.Act(PlayerInput.DropItem), 0.08, pressed: true);
            Later(TouchSend.Act(PlayerInput.DropItem), 0.13);
            Buzz();
        }
        if (_hotFinger >= 0 || _more.Holding) redraw = true;
        _more.Process(now);
        // the bag of the top bar gives way to the hotbar's own
        bool hotbar = Items.ItemController.Instance?.Ui.HotbarRect != null;
        foreach (var k in _keys)
        {
            bool hidden = k.Spec.Has(TouchFeel.NotWithHotbar) && hotbar;
            if (hidden != k.Hidden)
            {
                k.Hidden = hidden;
                redraw = true;
            }
        }

        _labelTimer -= delta;
        if (_labelTimer <= 0)
        {
            _labelTimer = 0.15;
            if (Relabel(now)) redraw = true;
        }
        if (redraw) _canvas.QueueRedraw();
    }

    // ---- labels and pills ----------------------------------------------------------------

    private readonly List<(string Action, string Text)> _prompts = new();
    private readonly List<(string Action, string Text, TouchSend Send)> _wanted = new();
    private static readonly Dictionary<string, Godot.Collections.Array<InputEvent>> Events = new();

    private static Godot.Collections.Array<InputEvent> EventsOf(string action)
    {
        if (!Events.TryGetValue(action, out var events)) Events[action] = events = InputMap.ActionGetEvents(action);
        return events;
    }

    /// <summary>Whether this control sends something the action is bound to.</summary>
    private static bool BoundTo(string action, TouchSend send)
    {
        if (send.Kind == TouchSendKind.Action) return send.Action == action;
        if (!InputMap.HasAction(action)) return false;
        foreach (var e in EventsOf(action))
            switch (send.Kind)
            {
                case TouchSendKind.Button when e is InputEventJoypadButton b && b.ButtonIndex == send.Button:
                case TouchSendKind.Trigger when e is InputEventJoypadMotion m && m.Axis == send.Axis:
                case TouchSendKind.Rocker when e is InputEventJoypadMotion r && (r.Axis == send.Axis || r.Axis == send.Axis2):
                case TouchSendKind.RightStick when e is InputEventJoypadMotion { Axis: JoyAxis.RightX or JoyAxis.RightY }:
                    return true;
            }
        return false;
    }

    private static bool OnLeftStick(string action)
    {
        if (!InputMap.HasAction(action)) return false;
        foreach (var e in EventsOf(action))
            if (e is InputEventJoypadMotion { Axis: JoyAxis.LeftX or JoyAxis.LeftY }) return true;
        return false;
    }

    /// <summary>Some control of this mode already does it (the top bar aside: its keys are generic).</summary>
    private bool OnScreen(string action)
    {
        foreach (var k in _keys)
            if (k.Spec.Shape != TouchShape.Top && BoundTo(action, k.Spec.Send)) return true;
        return TouchLayout.HasStick(_state.Mode) && OnLeftStick(action);
    }

    /// <summary>Prompts with a place of their own that never become pills: the top bar, the bag, More.</summary>
    private static bool Homed(string action, string text) => action switch
    {
        PlayerInput.Inventory or PlayerInput.Teleport or PlayerInput.ToggleMode or PlayerInput.Spectator
            or PlayerInput.QuickMenu or PlayerInput.Help or PlayerInput.Menu => true,
        PlayerInput.RideMenu => text == "Travel",
        PlayerInput.CameraToggle => text == "Camera",
        _ => false,
    };

    /// <summary>What a pill for this action presses: its pad button, else its trigger, else the action.</summary>
    private static TouchSend SendFor(string action)
    {
        if (!InputMap.HasAction(action)) return TouchSend.Act(action);
        foreach (var e in EventsOf(action))
            if (e is InputEventJoypadButton b) return TouchSend.Pad(b.ButtonIndex);
        foreach (var e in EventsOf(action))
            if (e is InputEventJoypadMotion { Axis: JoyAxis.TriggerLeft or JoyAxis.TriggerRight } m) return TouchSend.Trigger(m.Axis);
        return TouchSend.Act(action);
    }

    private string PinKey(string action) => $"{_state.Mode}:{action}";
    private bool Pinned(string action) => GameSettings.Current.TouchPins.Contains(PinKey(action));

    /// <summary>Labels the keys from the prompts and works out the pills. True if anything changed.</summary>
    private bool Relabel(double now)
    {
        bool changed = false;
        _prompts.Clear();
        if (Source != null) _prompts.AddRange(Source());

        foreach (var k in _keys)
        {
            if (k.Spec.Shape is TouchShape.Top or TouchShape.Stick) continue;
            string prompt = "";
            foreach (var (action, text) in _prompts)
                if (BoundTo(action, k.Spec.Send)) { prompt = text; break; }
            if (prompt != k.Prompt)
            {
                k.Prompt = prompt;
                changed = true;
            }
        }

        // the pills: prompts with no control of their own, the interact one first, then the pins
        _wanted.Clear();
        foreach (var (action, text) in _prompts)
        {
            if (Homed(action, text) || OnScreen(action)) continue;
            var send = SendFor(action);
            if (_wanted.Exists(w => w.Action == action || (send.Kind == TouchSendKind.Button && w.Send == send))) continue;
            if (action == PlayerInput.InteractMount) _wanted.Insert(0, (action, text, send));
            else _wanted.Add((action, text, send));
        }
        string prefix = $"{_state.Mode}:";
        foreach (var pin in GameSettings.Current.TouchPins)
        {
            if (!pin.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string action = pin[prefix.Length..];
            if (!_wanted.Exists(w => w.Action == action) && InputMap.HasAction(action) && !OnScreen(action))
                _wanted.Add((action, PinLabel(action), TouchSend.Act(action)));
        }

        // each pill keeps its slot while its prompt lasts; a gone one lingers a moment
        foreach (var p in _pills)
        {
            int i = _wanted.FindIndex(w => w.Action == p.Action);
            if (i >= 0)
            {
                if (p.Text != _wanted[i].Text || p.GoneUntil >= 0) changed = true;
                p.Text = _wanted[i].Text;
                p.GoneUntil = -1;
            }
            else if (p.GoneUntil < 0)
            {
                p.GoneUntil = now + PillLinger;
                changed = true;
            }
        }
        if (_pills.RemoveAll(p => p.GoneUntil >= 0 && p.Finger < 0 && now > p.GoneUntil) > 0) changed = true;
        foreach (var w in _wanted)
        {
            if (_pills.Count >= MaxPills) break;
            if (_pills.Exists(p => p.Action == w.Action)) continue;
            _pills.Add(new Pill { Action = w.Action, Text = w.Text, Send = w.Send });
            changed = true;
        }
        if (changed) LayoutPills();
        return changed;
    }

    /// <summary>A pinned action's words, from F1's list.</summary>
    private static string PinLabel(string action)
    {
        foreach (var (_, rows) in ControlsHelp.Groups)
            foreach (var row in rows)
                if (row.Action == action) return TouchMore.Short(row.What);
        return action.Replace('_', ' ');
    }

    /// <summary>
    /// The pills stack up the right edge, above the right thumb's controls and below the top bar, in
    /// as many rows as fit; more go in a second column inside the first.
    /// </summary>
    private void LayoutPills()
    {
        if (_pills.Count == 0) return;
        float h = TouchLayout.PillHeightMm * _k, gap = TouchLayout.PillGapMm * _k, edge = 4f * _k, columnW = 40f * _k;
        float colRight = _left ? _safe.Position.X + edge : _safe.End.X - edge;
        float baseY = _safe.End.Y, topY = _safe.Position.Y;
        foreach (var k in _keys)
        {
            if (k.Spec.Shape == TouchShape.Top)
            {
                topY = Mathf.Max(topY, k.Rect.End.Y);
                continue;
            }
            float inward = _left ? k.Rect.Position.X - _safe.Position.X : _safe.End.X - k.Rect.End.X;
            if (inward < columnW) baseY = Mathf.Min(baseY, k.Rect.Position.Y);
        }
        baseY -= 2f * _k;
        topY += 2f * _k;
        int rows = Math.Max(1, (int)((baseY - topY + gap) / (h + gap)));
        int size = FontPx(2.6f);
        for (int i = 0; i < _pills.Count; i++)
        {
            int column = i / rows, row = i % rows;
            var p = _pills[i];
            float w = Mathf.Min(_font.GetStringSize(p.Text, HorizontalAlignment.Left, -1, size).X + 5f * _k, columnW - gap);
            float edgeX = colRight + (_left ? 1 : -1) * column * columnW;
            float x = _left ? edgeX : edgeX - w;
            float y = baseY - (row + 1) * h - row * gap;
            p.Rect = new Rect2(x, y, w, h);
        }
    }

    // ---- input ---------------------------------------------------------------------------

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            // a real pad in hand: get out of the way until the screen is touched again
            case InputEventJoypadButton b when b.Device != Device && b.Device != XR.XrPad.DeviceId:
            case InputEventJoypadMotion m when m.Device != Device && m.Device != XR.XrPad.DeviceId && Mathf.Abs(m.AxisValue) > 0.5f:
                PadInUse(true);
                return;
            case InputEventScreenTouch t:
                PadInUse(false);
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

    private void PadInUse(bool on)
    {
        if (on == _padInUse) return;
        _padInUse = on;
        PlayerInput.HintsChanged();
    }

    /// <summary>
    /// What a hint calls the control that does an action here (#1054), or null: "[Gas]" for a key,
    /// "the stick", "a drag", "[MAP]", "[More › Travel menu]".
    /// </summary>
    public static string? NameOf(string action) => _current?.ControlFor(action);

    private string? ControlFor(string action)
    {
        if (!InputMap.HasAction(action)) return null;
        bool stick = TouchLayout.HasStick(_state.Mode);
        if (stick && OnLeftStick(action)) return "the stick";
        if (stick && TouchLayout.StickRuns(_state.Mode) && (_state.Flags & TouchFlags.RunButton) == 0
            && action is PlayerInput.Sprint or PlayerInput.FlyBoost)
            return "the stick past its ring";
        foreach (var k in _keys)
            if (k.Spec.Shape != TouchShape.Top && !k.Hidden && BoundTo(action, k.Spec.Send))
                return k.Spec.Shape == TouchShape.Stick ? $"the {k.Spec.Label} stick" : $"[{k.Spec.Label}]";
        if (action is PlayerInput.LookLeft or PlayerInput.LookRight or PlayerInput.LookUp or PlayerInput.LookDown)
            return "a drag";
        if (action == PlayerInput.Inventory) return "[BAG]";
        foreach (var k in _keys)
            if (k.Spec.Shape == TouchShape.Top && BoundTo(action, k.Spec.Send)) return $"[{k.Spec.Label}]";
        return TouchMore.LabelFor(_state, action) is { } more ? $"[More › {more}]" : null;
    }

    private float Margin => TouchLayout.HitMarginMm * _pxPerMm;

    private void Down(int finger, Vector2 at)
    {
        double now = Now;
        _canvas.QueueRedraw();
        if (_more.Open && _more.Rect.HasPoint(at))
        {
            _more.Down(finger, at, now);
            return;
        }
        // a wheel opened from More: this finger aims it and lets go of it
        if (_wheelAction != null && _wheelFinger < 0)
        {
            _wheelFinger = finger;
            return;
        }
        foreach (var k in _keys)
            if (!k.Hidden && k.Finger < 0 && Hit(k, at))
            {
                PressKey(k, finger, at);
                return;
            }
        foreach (var p in _pills)
            if (p.Finger < 0 && p.GoneUntil < 0 && p.Rect.Grow(Margin).HasPoint(at))
            {
                p.Finger = finger;
                p.PressFrame = Engine.GetProcessFrames();
                Send(p.Send, true);
                Buzz();
                return;
            }
        if (Items.ItemController.Instance?.Ui is { } ui)
        {
            int slot = ui.HotbarSlotAt(at);
            if (slot >= 0 && _hotFinger < 0)
            {
                _hotFinger = finger;
                _hotSlot = slot;
                _hotDropAt = now + LongPress;
                _hotDropped = false;
                return;
            }
            if (BagRect(ui) is { } bag && bag.Grow(Margin).HasPoint(at))
            {
                Tap(TouchSend.Act(PlayerInput.Inventory));
                Buzz();
                return;
            }
        }
        var size = _canvas.Size;
        bool inZone = (_left ? at.X > size.X * (1 - StickZoneWidth) : at.X < size.X * StickZoneWidth) && at.Y > size.Y * StickZoneTop;
        if (_stickFinger < 0 && inZone && TouchLayout.HasStick(_state.Mode))
        {
            if (_runLocked) UnlockRun();
            _stickFinger = finger;
            _stickOrigin = _stickAt = at;
            return;
        }
        if (_lookFinger < 0)
        {
            _lookFinger = finger;
            _tapUntil = now + TapUseSeconds;
            _lookTravel = 0;
        }
    }

    private bool Hit(Btn k, Vector2 at)
    {
        if (k.Spec.Shape == TouchShape.Stick) return k.Rect.GetCenter().DistanceTo(at) <= k.Rect.Size.X * 0.65f;
        return k.Rect.Grow(Margin).HasPoint(at);
    }

    private void PressKey(Btn k, int finger, Vector2 at)
    {
        k.Finger = finger;
        switch (k.Spec.Send.Kind)
        {
            case TouchSendKind.More:
                if (_more.Open) _more.Hide();
                else OpenMore();
                break;
            case TouchSendKind.Chat:
                Input.ParseInputEvent(new InputEventKey { Device = Device, Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = true });
                Input.ParseInputEvent(new InputEventKey { Device = Device, Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = false });
                break;
            case TouchSendKind.Rocker:
            case TouchSendKind.RightStick:
                MoveKey(k, at);
                break;
            default:
                // a locked gas pedal touched again: the finger holds it now
                if (k.Locked)
                {
                    k.Locked = false;
                    break;
                }
                if (k.Spec.Has(TouchFeel.Unlocks)) UnlockPedals();
                k.PressFrame = Engine.GetProcessFrames();
                Send(k.Spec.Send, true);
                break;
        }
        Buzz();
    }

    /// <summary>A rocker or a right stick follows the finger.</summary>
    private static void MoveKey(Btn k, Vector2 at)
    {
        var c = k.Rect.GetCenter();
        if (k.Spec.Send.Kind == TouchSendKind.Rocker)
        {
            float v = Mathf.Clamp((c.Y - at.Y) / (k.Rect.Size.Y * 0.5f), -1f, 1f);
            k.Value = new Vector2(0, v);
            Axis(JoyAxis.TriggerRight, Mathf.Max(v, 0f));
            Axis(JoyAxis.TriggerLeft, Mathf.Max(-v, 0f));
        }
        else
        {
            var v = ((at - c) / (k.Rect.Size.X * 0.5f)).LimitLength(1f);
            k.Value = v;
            Axis(JoyAxis.RightX, v.X);
            Axis(JoyAxis.RightY, v.Y);
        }
    }

    private void Up(int finger)
    {
        double now = Now;
        _canvas.QueueRedraw();
        if (_more.Up(finger)) return;
        if (finger == _wheelFinger)
        {
            if (_wheelAction != null) SendAction(_wheelAction, false);
            _wheelAction = null;
            _wheelFinger = -1;
            return;
        }
        foreach (var k in _keys)
            if (k.Finger == finger)
            {
                k.Finger = -1;
                ReleaseKey(k);
            }
        foreach (var p in _pills)
            if (p.Finger == finger)
            {
                p.Finger = -1;
                Release(p.Send, p.PressFrame);
            }
        if (finger == _hotFinger)
        {
            if (!_hotDropped) Tap(TouchSend.Act(PlayerInput.Slots[_hotSlot]));
            _hotFinger = -1;
        }
        if (finger == _stickFinger)
        {
            _stickFinger = -1;
            if (_runLockArmed) LockRun();
            else
            {
                Stick(Vector2.Zero);
                Run(false);
            }
            _runLockArmed = false;
        }
        if (finger == _lookFinger)
        {
            _lookFinger = -1;
            // a short still tap on the world uses the item in hand (the setting)
            if ((_state.Flags & TouchFlags.TapToUse) != 0 && (_state.Flags & TouchFlags.ItemInHand) != 0
                && now < _tapUntil && _lookTravel < 2f * _pxPerMm)
                Tap(TouchSend.Pad(JoyButton.RightShoulder));
        }
    }

    private void ReleaseKey(Btn k)
    {
        switch (k.Spec.Send.Kind)
        {
            case TouchSendKind.More:
            case TouchSendKind.Chat:
                break;
            case TouchSendKind.Rocker:
                k.Value = Vector2.Zero;
                Axis(JoyAxis.TriggerRight, 0);
                Axis(JoyAxis.TriggerLeft, 0);
                break;
            case TouchSendKind.RightStick:
                k.Value = Vector2.Zero;
                Axis(JoyAxis.RightX, 0);
                Axis(JoyAxis.RightY, 0);
                break;
            default:
                // slid off the top and let go: it stays down until touched or braked
                if (k.LockArmed)
                {
                    k.LockArmed = false;
                    k.Locked = true;
                    Buzz();
                    break;
                }
                Release(k.Spec.Send, k.PressFrame);
                break;
        }
    }

    /// <summary>Lets go of a press, a couple of frames late if it was this quick: a poller must see it down.</summary>
    private void Release(TouchSend s, ulong pressFrame)
    {
        if (Engine.GetProcessFrames() >= pressFrame + 2) Send(s, false);
        else Later(s, 0);
    }

    private void Drag(int finger, Vector2 at, Vector2 relative)
    {
        if (_more.Open)
        {
            _more.Drag(finger, at, relative, 2f * _pxPerMm);
            _canvas.QueueRedraw();
        }
        if (finger == _wheelFinger)
        {
            Look(at, relative);
            return;
        }
        if (finger == _stickFinger)
        {
            _stickAt = at;
            float r = TouchLayout.StickRadiusMm * _k;
            var off = at - _stickOrigin;
            Stick(off / r);
            if (TouchLayout.StickRuns(_state.Mode))
            {
                if ((_state.Flags & TouchFlags.RunButton) == 0) Run(off.Length() > r * 1.15f);
                _runLockArmed = at.DistanceTo(RunChip) < 5f * _k;
            }
            _canvas.QueueRedraw();
            return;
        }
        if (finger == _lookFinger)
        {
            _lookTravel += relative.Length();
            Look(at, relative);
            return;
        }
        foreach (var k in _keys)
            if (k.Finger == finger)
            {
                if (k.Spec.Send.Kind is TouchSendKind.Rocker or TouchSendKind.RightStick) MoveKey(k, at);
                else if (k.Spec.Has(TouchFeel.Lockable)) k.LockArmed = at.Y < k.Rect.Position.Y - 5f * _k;
                if (k.Spec.Has(TouchFeel.DragLook)) Look(at, relative);
                _canvas.QueueRedraw();
                return;
            }
        foreach (var p in _pills)
            if (p.Finger == finger)
            {
                // a pinned wheel is aimed by the finger that holds it
                Look(at, relative);
                return;
            }
    }

    /// <summary>The camera reads mouse motion: this one is marked as ours (<see cref="PlayerInput.IsLookMotion"/>).</summary>
    private void Look(Vector2 at, Vector2 relative)
    {
        // a parsed event is in window pixels and the viewport scales it to the canvas, as it did the
        // drag: send it back out to window pixels, or the phone's UI scale divides it
        var toWindow = GetViewport().GetFinalTransform();
        float scale = GameSettings.Current.TouchLookSpeed;
        Input.ParseInputEvent(new InputEventMouseMotion
        {
            Device = Device, Relative = toWindow.BasisXform(relative * scale), Position = toWindow * at,
        });
    }

    // ---- run lock, gas lock --------------------------------------------------------------

    /// <summary>The chip above a held stick: let go there and the run goes on, hands free.</summary>
    private Vector2 RunChip => _stickOrigin + new Vector2(0, -(TouchLayout.StickRadiusMm + 9f) * _k);

    private void LockRun()
    {
        _runLocked = true;
        Stick(new Vector2(0, -1));
        if ((_state.Flags & TouchFlags.RunButton) == 0) Run(true);
        Buzz();
    }

    private void UnlockRun()
    {
        _runLocked = false;
        Stick(Vector2.Zero);
        Run(false);
    }

    private void Run(bool on)
    {
        if (on == _runHeld) return;
        _runHeld = on;
        Pad(JoyButton.LeftStick, on);
    }

    private void UnlockPedals()
    {
        foreach (var k in _keys)
            if (k.Locked)
            {
                k.Locked = false;
                Send(k.Spec.Send, false);
            }
    }

    // ---- More ----------------------------------------------------------------------------

    private void OpenMore()
    {
        // under the top bar, so its More key closes the sheet again
        float w = _safe.Size.X * 0.46f, top = _safe.Position.Y;
        foreach (var k in _keys)
            if (k.Spec.Shape == TouchShape.Top) top = Mathf.Max(top, k.Rect.End.Y + 1f * _pxPerMm);
        var rect = new Rect2(_left ? _safe.Position.X : _safe.End.X - w, top, w, _safe.End.Y - top);
        _more.Show(_state, OnScreen, rect);
        _more.Layout(_font, _pxPerMm);
        _canvas.QueueRedraw();
    }

    private void MoreTapped(TouchMore.Entry e)
    {
        _canvas.QueueRedraw();
        if (e.Wheel)
        {
            // a wheel opens on the press and picks on the release: the next finger aims and lets go
            SendAction(e.Action, true);
            _wheelAction = e.Action;
            _wheelFinger = -1;
            return;
        }
        Tap(TouchSend.Act(e.Action), e.Hold ? 0.6 : 0.05);
    }

    private void TogglePin(TouchMore.Entry e)
    {
        var pins = GameSettings.Current.TouchPins;
        string key = PinKey(e.Action);
        if (!pins.Remove(key))
        {
            string prefix = $"{_state.Mode}:";
            var mine = pins.FindAll(p => p.StartsWith(prefix, StringComparison.Ordinal));
            if (mine.Count >= MaxPinsPerMode) pins.Remove(mine[0]);
            pins.Add(key);
        }
        GameSettings.SaveOnly(nameof(GameSettings.TouchPins),
            new System.Text.Json.Nodes.JsonArray(pins.Select(s => (System.Text.Json.Nodes.JsonNode?)s).ToArray()));
        Buzz();
        _labelTimer = 0;
        _canvas.QueueRedraw();
    }

    // ---- sending -------------------------------------------------------------------------

    private static void Stick(Vector2 v)
    {
        v = v.LimitLength(1f);
        Axis(JoyAxis.LeftX, v.X);
        Axis(JoyAxis.LeftY, v.Y);
    }

    private static void Pad(JoyButton b, bool pressed) =>
        Input.ParseInputEvent(new InputEventJoypadButton { Device = Device, ButtonIndex = b, Pressed = pressed });

    private static void Axis(JoyAxis axis, float value) =>
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = Device, Axis = axis, AxisValue = value });

    private static void SendAction(string action, bool pressed) =>
        Input.ParseInputEvent(new InputEventAction { Device = Device, Action = action, Pressed = pressed, Strength = pressed ? 1f : 0f });

    private static void Send(TouchSend s, bool pressed)
    {
        switch (s.Kind)
        {
            case TouchSendKind.Button: Pad(s.Button, pressed); break;
            case TouchSendKind.Trigger: Axis(s.Axis, pressed ? 1f : 0f); break;
            case TouchSendKind.Action: SendAction(s.Action, pressed); break;
        }
    }

    /// <summary>A press now and its release a moment later (a frame or more, so polled readers see it).</summary>
    private void Tap(TouchSend s, double hold = 0.05)
    {
        Send(s, true);
        Later(s, hold);
    }

    /// <summary>A release (or a press) a while from now.</summary>
    private void Later(TouchSend s, double after, bool pressed = false) =>
        _scheduled.Add((s, Now + after, Engine.GetProcessFrames() + 2, pressed));

    private static void Buzz()
    {
        if (GameSettings.Current.Vibration) Input.VibrateHandheld(12, 0.3f);
    }

    private void ReleaseAll()
    {
        foreach (var k in _keys)
            if (k.Finger >= 0 || k.Locked)
            {
                k.Finger = -1;
                k.Locked = k.LockArmed = false;
                ReleaseKey(k);
            }
        foreach (var p in _pills)
            if (p.Finger >= 0)
            {
                p.Finger = -1;
                Send(p.Send, false);
            }
        if (_stickFinger >= 0 || _runLocked) Stick(Vector2.Zero);
        Run(false);
        _runLocked = _runLockArmed = false;
        if (_wheelAction != null) SendAction(_wheelAction, false);
        _wheelAction = null;
        _stickFinger = _lookFinger = _wheelFinger = _hotFinger = -1;
    }

    // ---- drawing -------------------------------------------------------------------------

    private int FontPx(float mm) => Math.Max(8, (int)(mm * _k));

    /// <summary>The hotbar's own bag: the first cell of its place.</summary>
    private static Rect2? BagRect(Items.InventoryUi ui) =>
        ui.HotbarRect != null && HotbarPlace is { } place ? new Rect2(place.Position, new Vector2(place.Size.Y, place.Size.Y)) : null;

    private static float Opacity => GameSettings.Current.TouchOpacity;
    private static Color Fill(bool down) => down ? new Color(1f, 0.78f, 0.3f, 0.92f) : new Color(0.06f, 0.07f, 0.09f, 0.85f * Opacity);
    private static Color Ring(bool primary) => new(1f, 1f, 1f, Mathf.Min(1f, Opacity + (primary ? 0.45f : 0.3f)));
    private static Color Ink(bool down) => down ? new Color(0.1f, 0.07f, 0.02f) : new Color(1f, 1f, 1f, Mathf.Min(1f, Opacity + 0.5f));
    private static readonly Color Ctx = new(0.25f, 0.84f, 0.9f, 0.95f), CtxFill = new(0.04f, 0.24f, 0.27f, 0.7f), Amber = new(1f, 0.78f, 0.3f, 0.95f);

    private void Box(Rect2 r, float radius, Color fill, Color border, float width)
    {
        _box.BgColor = fill;
        _box.BorderColor = border;
        _box.SetBorderWidthAll((int)Mathf.Ceil(width));
        _box.SetCornerRadiusAll((int)radius);
        _canvas.DrawStyleBox(_box, r);
    }

    private void Paint()
    {
        if (!Shown) return;
        foreach (var k in _keys)
            if (!k.Hidden) PaintKey(k);
        foreach (var p in _pills)
        {
            bool down = p.Finger >= 0;
            var a = new Color(1, 1, 1, p.GoneUntil >= 0 ? 0.4f : 1f);
            Box(p.Rect, p.Rect.Size.Y / 2, down ? Fill(true) : CtxFill * a, Ctx * a, 2f);
            Text(p.Text, p.Rect, FontPx(2.6f), down ? Ink(true) : a);
        }
        PaintStick();
        if (Items.ItemController.Instance?.Ui is { } ui && BagRect(ui) is { } bag)
        {
            Box(bag, 6, Fill(false), Ring(false), 2f);
            Text("BAG", bag, FontPx(2.2f), Ink(false));
            if (_hotFinger >= 0 && !_hotDropped && ui.HotbarRect is { } bar)
            {
                // the long press fills a ring round the slot about to be dropped
                float slotW = bar.Size.X / Items.Inventory.HotbarSize;
                var c = new Vector2(bar.Position.X + slotW * (_hotSlot + 0.5f), bar.GetCenter().Y);
                float f = (float)Math.Clamp(1 - (_hotDropAt - Now) / LongPress, 0, 1);
                _canvas.DrawArc(c, bar.Size.Y * 0.62f, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * f, 40, Amber, 4f, true);
            }
        }
        if (_more.Open)
        {
            _more.Draw(_canvas, _font, _pxPerMm, Pinned, Box);
            if (_more.Holding && _more.PressedRect is { } held)
            {
                float f = _more.HeldFraction(Now);
                _canvas.DrawRect(new Rect2(held.Position.X + held.Size.Y / 2, held.End.Y - 3, (held.Size.X - held.Size.Y) * f, 3), Ctx);
            }
        }
    }

    private void PaintKey(Btn k)
    {
        bool down = k.Finger >= 0 || k.Locked;
        bool primary = k.Spec.Has(TouchFeel.Primary);
        var r = k.Rect;
        switch (k.Spec.Shape)
        {
            case TouchShape.Round:
            {
                float radius = r.Size.X / 2;
                _canvas.DrawCircle(r.GetCenter(), radius, Fill(down));
                _canvas.DrawArc(r.GetCenter(), radius, 0, Mathf.Tau, 48, down ? Amber : Ring(primary), primary ? 3f : 2f, true);
                KeyText(k, r);
                break;
            }
            case TouchShape.Pedal:
                Box(r, 2f * _k, Fill(down), down ? Amber : Ring(primary), primary ? 3f : 2f);
                KeyText(k, r);
                if (k.LockArmed || k.Locked)
                    Text(k.Locked ? "locked" : "lock", new Rect2(r.Position.X, r.Position.Y - 5f * _k, r.Size.X, 4f * _k), FontPx(2f), Amber);
                break;
            case TouchShape.Rocker:
            {
                Box(r, 2f * _k, Fill(false), Ring(primary), 2f);
                var c = r.GetCenter();
                _canvas.DrawLine(new Vector2(r.Position.X + 2f * _k, c.Y), new Vector2(r.End.X - 2f * _k, c.Y), Ring(false), 1.5f);
                float y = c.Y - k.Value.Y * r.Size.Y * 0.5f;
                Box(new Rect2(r.Position.X + 1f * _k, y - 2f * _k, r.Size.X - 2f * _k, 4f * _k), 1f * _k, down ? Amber : Ring(false), Ring(true), 1f);
                Text(k.Prompt.Length > 0 ? k.Prompt : k.Label, new Rect2(r.Position.X - 6f * _k, r.End.Y + 0.5f * _k, r.Size.X + 12f * _k, 4f * _k),
                    FontPx(2.2f), Ink(false));
                break;
            }
            case TouchShape.Stick:
            {
                var c = r.GetCenter();
                float radius = r.Size.X / 2;
                _canvas.DrawCircle(c, radius, Fill(false));
                _canvas.DrawArc(c, radius, 0, Mathf.Tau, 48, Ring(true), 2f, true);
                _canvas.DrawCircle(c + k.Value * radius, radius * 0.36f, down ? Amber : Ring(false));
                Text(k.Label, new Rect2(r.Position.X, r.End.Y + 0.5f * _k, r.Size.X, 4f * _k), FontPx(2.2f), Ink(false));
                break;
            }
            case TouchShape.Top:
                Box(r, 1.2f * _k, down ? Fill(true) : new Color(0.04f, 0.05f, 0.07f, 0.55f + 0.3f * Opacity), new Color(1, 1, 1, 0.55f), 1.5f);
                Text(k.Label, r, FontPx(2f), Ink(down));
                break;
        }
    }

    /// <summary>A key's words: the prompt's when they fit inside, else its own inside and the prompt under it.</summary>
    private void KeyText(Btn k, Rect2 r)
    {
        bool down = k.Finger >= 0 || k.Locked;
        int size = FontPx(r.Size.X > 11f * _k ? 2.8f : 2.3f);
        if (k.Prompt.Length > 0 && _font.GetStringSize(k.Prompt, HorizontalAlignment.Left, -1, size).X <= r.Size.X * 0.88f)
        {
            Text(k.Prompt, r, size, Ink(down));
            return;
        }
        Text(k.Label, r, size, Ink(down));
        if (k.Prompt.Length > 0)
            Text(k.Prompt, new Rect2(r.Position.X - 10f * _k, r.End.Y + 0.3f * _k, r.Size.X + 20f * _k, 3.6f * _k), FontPx(2f), Ink(false));
    }

    private void PaintStick()
    {
        if (!TouchLayout.HasStick(_state.Mode)) return;
        float r = TouchLayout.StickRadiusMm * _k;
        bool active = _stickFinger >= 0 || _runLocked;
        var origin = active ? _stickOrigin : StickHome;
        _canvas.DrawCircle(origin, r, active ? Fill(false) : new Color(0, 0, 0, 0.12f * Opacity));
        _canvas.DrawArc(origin, r, 0, Mathf.Tau, 56, active ? Ring(true) : Ring(false) * new Color(1, 1, 1, 0.6f), 2f, true);
        if (active)
        {
            var knob = _runLocked ? origin + new Vector2(0, -r) : origin + (_stickAt - origin).LimitLength(r);
            _canvas.DrawCircle(knob, r * 0.38f, Amber);
            if (TouchLayout.StickRuns(_state.Mode))
            {
                var box = new Rect2(RunChip - new Vector2(7f, 2.5f) * _k, new Vector2(14f, 5f) * _k);
                bool lit = _runLockArmed || _runLocked;
                Box(box, 2.5f * _k, lit ? Amber : new Color(0, 0, 0, 0.35f), Amber, 1.5f);
                Text(_runLocked ? "running" : "lock run", box, FontPx(2f), lit ? Ink(true) : Amber);
            }
        }
        // its word under the ring, unless the hotbar is there
        else if (Items.ItemController.Instance?.Ui.HotbarRect == null)
            Text(TouchLayout.StickLabel(_state.Mode), new Rect2(origin.X - r, origin.Y + r + 0.5f * _k, 2 * r, 4f * _k), FontPx(2.2f),
                new Color(1, 1, 1, 0.35f + 0.4f * Opacity));
    }

    private void Text(string text, Rect2 r, int size, Color color)
    {
        var at = new Vector2(r.Position.X, r.GetCenter().Y + size * 0.36f);
        _canvas.DrawStringOutline(_font, at, text, HorizontalAlignment.Center, r.Size.X, size, Math.Max(2, size / 5), new Color(0, 0, 0, 0.75f));
        _canvas.DrawString(_font, at, text, HorizontalAlignment.Center, r.Size.X, size, color);
    }
}
