using Godot;

namespace UnitSport.Core;

/// <summary>
/// The touch layout editor (#1048, docs/notes/core/touch-controls.md): a mode's controls drawn
/// where they are, to drag somewhere else, resize by the handle at a corner, or hide (a hidden one
/// stays in More). A move applies to every mode with the same control unless "This mode only" is
/// picked (<see cref="TouchLayout.SharedKey"/> / <see cref="TouchLayout.ModeKey"/>). A drop that
/// would cover another control in any mode and layout it applies to, leave the screen or run under
/// the hotbar is refused, and the control goes back. Its host feeds it input with
/// <see cref="Handle"/>: the touch overlay over the live game (More › Edit layout), or
/// <see cref="TouchLayoutScreen"/> from Settings, with a mode picker.
/// </summary>
public partial class TouchEditor : Control
{
    /// <summary>The overrides to keep (Done), or null (Cancel).</summary>
    public Action<Dictionary<string, TouchOverride>?>? Finished;

    /// <summary>The mode being edited.</summary>
    public TouchMode Mode { get; private set; }

    private TouchFlags _flags;
    private readonly bool _picker;
    private readonly Dictionary<string, TouchOverride> _work;
    private bool _allModes = true;

    private Rect2 _safe;
    private Vector2 _size;
    private float _titleY, _columnX, _columnW;
    private float _pxPerMm = 10f, _k = 10f, _scale = 1f;
    private bool _left;
    private Font _font = null!;
    private readonly StyleBoxFlat _box = new() { AntiAliasing = true };

    private List<TouchKey> _all = new(), _shown = new();
    private readonly List<(string Text, Rect2 Rect, Action Act, bool On)> _buttons = new();
    private string? _selected;
    private enum Drag { None, Move, Resize }
    private Drag _drag;
    private Vector2 _grab;
    private float _startSize, _startDist;
    private TouchOverride? _before;
    private bool _beforeOwn;
    private string? _conflict, _message;
    private double _messageUntil;

    /// <summary>The modes the Settings picker offers: every one with controls of its own to place.</summary>
    private static readonly TouchMode[] Picker =
    {
        TouchMode.Foot, TouchMode.Drive, TouchMode.Ride, TouchMode.Snow, TouchMode.Fly, TouchMode.Hover, TouchMode.Glide,
        TouchMode.Dig, TouchMode.Crane, TouchMode.Fight, TouchMode.Spectator,
    };

    public TouchEditor(TouchMode mode, TouchFlags flags, bool picker)
    {
        Name = "TouchEditor";
        Mode = mode;
        _flags = flags;
        _picker = picker;
        _work = GameSettings.Current.TouchOverrides.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public TouchEditor() : this(TouchMode.Foot, TouchFlags.None, false) { }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        _font = ThemeDB.FallbackFont;
        Resized += Relayout;
        Relayout();
    }

    private void Step(int by)
    {
        int i = Array.IndexOf(Picker, Mode);
        SetMode(Picker[((i < 0 ? 0 : i) + by + Picker.Length) % Picker.Length]);
    }

    /// <summary>Edits another mode (the picker), with the flags that show most of its controls.</summary>
    public void SetMode(TouchMode mode)
    {
        Mode = mode;
        _flags = TouchLayout.Fullest(mode);
        _selected = null;
        Relayout();
    }

    private void Relayout()
    {
        _size = Size == Vector2.Zero ? GetViewportRect().Size : Size;
        (_safe, _pxPerMm) = TouchControls.MeasureScreen(GetViewport(), _size);
        _scale = GameSettings.Current.TouchScale;
        _k = _pxPerMm * _scale;
        _left = GameSettings.Current.TouchLeftHanded;
        // on foot the hotbar's own bag stands in for the top bar's
        _all = TouchLayout.For(Mode, _flags).FindAll(k => !(Mode == TouchMode.Foot && k.Has(TouchFeel.NotWithHotbar)));
        _shown = TouchLayout.Apply(_all, Mode, _work);
        LayoutButtons();
        QueueRedraw();
    }

    // ---- the toolbar: in the middle of the screen, where no control lives ------------------

    private void LayoutButtons()
    {
        _buttons.Clear();
        float h = 8f * _pxPerMm, gap = 1.5f * _pxPerMm;
        int size = FontPx(2.6f);
        var rows = new List<List<(string, Action, bool)>>
        {
            new()
            {
                (_allModes ? "Moves: all modes" : "Moves: this mode only", () => { _allModes = !_allModes; LayoutButtons(); }, true),
                ("Hide", HideSelected, _selected != null),
                ("Reset", ResetMode, true),
            },
            new() { ("Cancel", () => Finished?.Invoke(null), true), ("Done", () => Finished?.Invoke(_work), true) },
        };
        var hidden = _all.Where(k => TouchLayout.OverrideFor(Mode, k, _work) is { Hidden: true }).ToList();
        foreach (var chunk in hidden.Chunk(3))
            rows.Add(chunk.Select(k => ($"Show {k.Label}", (Action)(() => Show(k)), true)).ToList());
        // Settings: a stepper through the modes (a row of every mode is wider than a phone)
        if (_picker)
            rows.Insert(0, new() { ("◀", () => Step(-1), true), (TouchLayout.ModeName(Mode), () => Step(1), true), ("▶", () => Step(1), true) });

        // under the top bar: the title, then the rows
        float top = _safe.Position.Y;
        foreach (var k in _all)
            if (k.Shape == TouchShape.Top) top = Mathf.Max(top, TouchLayout.Place(k, _safe, _pxPerMm, _scale, _left).End.Y);
        _titleY = top + 7f * _pxPerMm;
        float y = _titleY + 3f * _pxPerMm;
        // the stick's side of the screen: no mode has a control there
        _columnW = _safe.Size.X * 0.56f;
        _columnX = _left ? _safe.End.X - _columnW : _safe.Position.X;
        foreach (var row in rows)
        {
            var widths = row.Select(b => _font.GetStringSize(b.Item1, HorizontalAlignment.Left, -1, size).X + 5f * _pxPerMm).ToList();
            float total = widths.Sum() + gap * (row.Count - 1);
            float x = _columnX + (_columnW - total) / 2;
            for (int i = 0; i < row.Count; i++)
            {
                _buttons.Add((row[i].Item1, new Rect2(x, y, widths[i], h), row[i].Item2, row[i].Item3));
                x += widths[i] + gap;
            }
            y += h + gap;
        }
    }

    /// <summary>Where a toolbar button is (for <see cref="TouchCheck"/>), or null.</summary>
    public Vector2? ButtonAt(string text)
    {
        foreach (var b in _buttons)
            if (b.Text == text) return b.Rect.GetCenter();
        return null;
    }

    /// <summary>What the editor says at the bottom now: a refusal, a reset (for <see cref="TouchCheck"/>).</summary>
    public string? Note => _conflict ?? (RealClock.Now < _messageUntil ? _message : null);

    /// <summary>Canvas pixels a millimetre here (for <see cref="TouchCheck"/>).</summary>
    public float PxPerMm => _pxPerMm;

    // ---- editing ---------------------------------------------------------------------------

    private string TargetKey(TouchKey key) => _allModes ? TouchLayout.SharedKey(key) : TouchLayout.ModeKey(Mode, key);

    private TouchKey? Find(string id) => _all.Find(k => k.Id == id);

    /// <summary>The override being edited for a control, made from where it is now if it has none.</summary>
    private TouchOverride Editing(TouchKey key)
    {
        string target = TargetKey(key);
        if (_work.TryGetValue(target, out var o)) return o;
        var now = TouchLayout.OverrideFor(Mode, key, _work);
        o = now?.Copy() ?? new TouchOverride { X = key.Mm.X, Y = key.Mm.Y, Size = 1f };
        o.Hidden = false;
        _work[target] = o;
        // a shared edit shows here only if this mode has no override of its own
        if (_allModes) _work.Remove(TouchLayout.ModeKey(Mode, key));
        return o;
    }

    private void HideSelected()
    {
        if (_selected == null || Find(_selected) is not { } key) return;
        Editing(key).Hidden = true;
        _selected = null;
        Relayout();
    }

    private void Show(TouchKey key)
    {
        foreach (var k in new[] { TouchLayout.ModeKey(Mode, key), TouchLayout.SharedKey(key) })
            if (_work.TryGetValue(k, out var o)) o.Hidden = false;
        if (Conflict(key.Id) is { } why)
        {
            // where it was would now cover something: back to its own place
            _work.Remove(TouchLayout.ModeKey(Mode, key));
            _work.Remove(TouchLayout.SharedKey(key));
            Say($"{key.Label} is back in its own place: {why}");
        }
        Relayout();
    }

    /// <summary>Everything this mode's controls take from the player's layout, here and shared.</summary>
    private void ResetMode()
    {
        foreach (var key in _all)
        {
            _work.Remove(TouchLayout.ModeKey(Mode, key));
            _work.Remove(TouchLayout.SharedKey(key));
        }
        _selected = null;
        Say($"The {TouchLayout.ModeName(Mode)} controls are back where they were");
        Relayout();
    }

    private string? Conflict(string id) =>
        TouchLayout.Conflict(_work, _safe, _pxPerMm, _scale, _left, id);

    private void Say(string text)
    {
        _message = text;
        _messageUntil = RealClock.Now + 3;
        QueueRedraw();
    }

    // ---- input -------------------------------------------------------------------------------

    /// <summary>One input event from the host; true if it was the editor's.</summary>
    public bool Handle(InputEvent e)
    {
        switch (e)
        {
            case InputEventScreenTouch { Index: 0 } t:
                if (t.Pressed) Down(t.Position);
                else Up();
                return true;
            case InputEventScreenDrag { Index: 0 } d:
                Move(d.Position);
                return true;
            case InputEventScreenTouch or InputEventScreenDrag:
                return true;
            // a mouse that is not a finger in disguise (a desktop --mobile Settings, no touch emulation)
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b when !Input.EmulateTouchFromMouse && b.Device != InputEvent.DeviceIdEmulation:
                if (b.Pressed) Down(b.Position);
                else Up();
                return true;
            case InputEventMouseMotion m when !Input.EmulateTouchFromMouse && m.Device != InputEvent.DeviceIdEmulation:
                Move(m.Position);
                return true;
        }
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu))
        {
            Finished?.Invoke(null);
            return true;
        }
        return false;
    }

    private Rect2 RectOf(TouchKey key) => TouchLayout.Place(key, _safe, _pxPerMm, _scale, _left);

    /// <summary>The resize handle: the corner of a control nearest the middle of the screen.</summary>
    private Vector2 ResizeHandle(Rect2 r)
    {
        var c = _safe.GetCenter();
        return new Vector2(c.X < r.GetCenter().X ? r.Position.X : r.End.X, c.Y < r.GetCenter().Y ? r.Position.Y : r.End.Y);
    }

    private void Down(Vector2 at)
    {
        foreach (var b in _buttons)
            if (b.Rect.HasPoint(at))
            {
                if (b.On) b.Act();
                QueueRedraw();
                return;
            }
        if (_selected != null && _shown.Find(k => k.Id == _selected) is { } sel && ResizeHandle(RectOf(sel)).DistanceTo(at) < 4f * _pxPerMm)
        {
            Begin(sel, Drag.Resize);
            var o = Editing(Find(sel.Id)!);
            _startSize = o.Size;
            _startDist = Mathf.Max(1f, RectOf(sel).GetCenter().DistanceTo(at));
            return;
        }
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            var key = _shown[i];
            if (!RectOf(key).Grow(1.5f * _pxPerMm).HasPoint(at)) continue;
            _selected = key.Id;
            Begin(key, Drag.Move);
            _grab = at - RectOf(key).GetCenter();
            LayoutButtons();
            QueueRedraw();
            return;
        }
        _selected = null;
        LayoutButtons();
        QueueRedraw();
    }

    /// <summary>Remembers what the control had, to put it back if the drop is refused.</summary>
    private void Begin(TouchKey key, Drag drag)
    {
        _drag = drag;
        string target = TargetKey(Find(key.Id)!);
        _beforeOwn = _work.TryGetValue(target, out var had);
        _before = had?.Copy();
        _conflict = null;
    }

    private void Move(Vector2 at)
    {
        if (_drag == Drag.None || _selected == null || Find(_selected) is not { } key) return;
        var o = Editing(key);
        if (_drag == Drag.Move)
        {
            var c = at - _grab;
            o.X = (TouchLayout.FromLeft(key, _left) ? c.X - _safe.Position.X : _safe.End.X - c.X) / _k;
            o.Y = (key.Corner == TouchCorner.TopRight ? c.Y - _safe.Position.Y : _safe.End.Y - c.Y) / _k;
        }
        else
        {
            var placed = RectOf(_shown.Find(k => k.Id == key.Id) ?? key);
            o.Size = Mathf.Clamp(_startSize * placed.GetCenter().DistanceTo(at) / _startDist,
                TouchLayout.MinOverrideSize, TouchLayout.MaxOverrideSize);
        }
        _shown = TouchLayout.Apply(_all, Mode, _work);
        _conflict = Conflict(key.Id);
        QueueRedraw();
    }

    private void Up()
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        if (_selected != null && Find(_selected) is { } key && _conflict != null)
        {
            // refused: back where it was
            string target = TargetKey(key);
            if (_beforeOwn && _before != null) _work[target] = _before;
            else _work.Remove(target);
            Say(_conflict);
            _conflict = null;
        }
        Relayout();
    }

    // ---- drawing -------------------------------------------------------------------------------

    private int FontPx(float mm) => Math.Max(8, (int)(mm * _pxPerMm));

    private static readonly Color Amber = new(1f, 0.78f, 0.3f), Red = new(1f, 0.35f, 0.3f), Line = new(1f, 1f, 1f, 0.85f);

    private void Box(Rect2 r, float radius, Color fill, Color border, float width)
    {
        _box.BgColor = fill;
        _box.BorderColor = border;
        _box.SetBorderWidthAll((int)Mathf.Ceil(width));
        _box.SetCornerRadiusAll((int)radius);
        DrawStyleBox(_box, r);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, _size), new Color(0.02f, 0.03f, 0.05f, 0.45f));
        DrawString(_font, new Vector2(_columnX, _titleY),
            _picker ? "Edit the touch layout" : $"Edit the touch layout: {TouchLayout.ModeName(Mode)}", HorizontalAlignment.Center, _columnW, FontPx(3.4f), Colors.White);
        foreach (var key in _shown)
        {
            var r = RectOf(key);
            bool sel = key.Id == _selected;
            var border = sel && _conflict != null ? Red : sel ? Amber : Line;
            var fill = new Color(0.06f, 0.07f, 0.09f, sel ? 0.75f : 0.55f);
            if (key.Shape is TouchShape.Round or TouchShape.Stick)
            {
                DrawCircle(r.GetCenter(), r.Size.X / 2, fill);
                DrawArc(r.GetCenter(), r.Size.X / 2, 0, Mathf.Tau, 48, border, sel ? 4f : 2f, true);
            }
            else Box(r, Mathf.Min(2f * _k, r.Size.Y / 2), fill, border, sel ? 4f : 2f);
            int fs = TouchControls.FitFont(_font, key.Label, FontPx(2.3f), r.Size.X * 0.9f);
            DrawString(_font, new Vector2(r.Position.X, r.GetCenter().Y + fs * 0.36f), key.Label,
                HorizontalAlignment.Center, r.Size.X, fs, Colors.White);
            if (sel)
            {
                var h = ResizeHandle(r);
                DrawCircle(h, 2.6f * _pxPerMm, Amber);
                DrawString(_font, h + new Vector2(-2.6f * _pxPerMm, 1f * _pxPerMm), "⤡", HorizontalAlignment.Center, 5.2f * _pxPerMm,
                    FontPx(2.6f), new Color(0.1f, 0.07f, 0.02f));
            }
        }
        // the hotbar's place, in Settings where the game's own is not on screen
        if (Mode == TouchMode.Foot && _picker)
        {
            var bar = TouchLayout.Hotbar(_safe, _pxPerMm, _scale, _left, Items.Inventory.HotbarSize);
            Box(bar, 6, new Color(1, 1, 1, 0.06f), new Color(1, 1, 1, 0.3f), 1.5f);
            DrawString(_font, new Vector2(bar.Position.X, bar.GetCenter().Y + FontPx(2f) * 0.36f), "hotbar",
                HorizontalAlignment.Center, bar.Size.X, FontPx(2f), new Color(1, 1, 1, 0.5f));
        }
        foreach (var (text, rect, _, on) in _buttons)
        {
            bool pressed = text.StartsWith("Moves", StringComparison.Ordinal) ? false : text == "Done";
            Box(rect, rect.Size.Y / 2, pressed ? Amber : new Color(0.1f, 0.12f, 0.15f, 0.92f),
                on ? new Color(1, 1, 1, 0.6f) : new Color(1, 1, 1, 0.2f), 1.5f);
            DrawString(_font, new Vector2(rect.Position.X, rect.GetCenter().Y + FontPx(2.6f) * 0.36f), text, HorizontalAlignment.Center,
                rect.Size.X, FontPx(2.6f), pressed ? new Color(0.1f, 0.07f, 0.02f) : on ? Colors.White : new Color(1, 1, 1, 0.35f));
        }
        string? note = _conflict ?? (RealClock.Now < _messageUntil ? _message : null)
            ?? (_selected == null ? "Drag a control to move it. Tap it, then drag its corner to resize." : null);
        if (note != null && _buttons.Count > 0)
        {
            float y = _buttons.Max(b => b.Rect.End.Y) + 4f * _pxPerMm;
            DrawMultilineString(_font, new Vector2(_columnX, y), note, HorizontalAlignment.Center, _columnW, FontPx(2.4f), 3,
                _conflict != null ? Red : new Color(1, 1, 1, 0.8f));
        }
    }

    public override void _Process(double delta)
    {
        if (_message != null && RealClock.Now >= _messageUntil)
        {
            _message = null;
            QueueRedraw();
        }
    }
}
