using Godot;

namespace UnitSport.Core;

/// <summary>
/// The phone's More sheet (#1043, docs/notes/core/touch-controls.md): every action of this mode that
/// has no control on screen, as big pills, from the <see cref="ControlsHelp"/> groups for the mode
/// (a control added to F1 shows up here by itself), and a few that apply everywhere. A tap sends the
/// action; a long press pins it to the mode's pills. Drawn and hit-tested by <see cref="TouchControls"/>,
/// which keeps the stick live beside it.
/// </summary>
internal sealed class TouchMore
{
    /// <summary>One pill: what it says, the action it sends, and how.</summary>
    public sealed record Entry(string Label, string Action, bool Hold, bool Wheel)
    {
        public Rect2 Rect;
    }

    private sealed record Heading(string Text, Vector2 At);

    private readonly List<Entry> _entries = new();
    private readonly List<Heading> _headings = new();
    private readonly HashSet<string> _seen = new();
    private float _scroll, _contentHeight;
    private int _finger = -1;
    private Vector2 _downAt;
    private double _downTime;
    private bool _dragged, _pressHandled;
    private Entry? _pressed;

    public bool Open { get; private set; }
    public Rect2 Rect { get; private set; }
    public Rect2 CloseRect { get; private set; }
    public TouchState State { get; private set; }

    /// <summary>A pill was tapped (the sheet closes itself first).</summary>
    public Action<Entry>? Tapped;
    /// <summary>A pill was held: pin or unpin it.</summary>
    public Action<Entry>? Held;

    /// <summary>The actions every mode can reach from here.</summary>
    private static readonly (string Label, string Action)[] Always =
    {
        ("Travel", PlayerInput.RideMenu),
        ("Creative / survival", PlayerInput.ToggleMode),
        ("Spectator camera", PlayerInput.Spectator),
        ("Quick menu", PlayerInput.QuickMenu),
        ("Emotes", PlayerInput.EmoteWheel),
        ("Drop item", PlayerInput.DropItem),
        ("Bird journal", PlayerInput.BirdJournal),
        ("All controls", PlayerInput.Help),
    };

    /// <summary>Which F1 groups a mode shows, by the start of their titles.</summary>
    private static string[] GroupsFor(TouchState s)
    {
        string[] ride = s.Ride switch
        {
            "Truck" => new[] { "Trucks and buses", "Farm machines" },
            "Forklift" => new[] { "Forklift" },
            "Excavator" => new[] { "Excavator", "Mini excavator" },
            "WheelLoader" => new[] { "Wheel loader" },
            "MiniDumper" => new[] { "Mini dumper" },
            "CompactRoller" => new[] { "Compact roller" },
            "Telehandler" => new[] { "Telehandler" },
            "Steamer" => new[] { "Paddle steamer" },
            "Airliner" => new[] { "Airliners" },
            "Ufo" => new[] { "UFO" },
            _ => Array.Empty<string>(),
        };
        string[] mode = s.Mode switch
        {
            TouchMode.Foot => new[] { "On foot", "Items", "Stadiums" },
            TouchMode.Drive or TouchMode.Ride or TouchMode.Snow or TouchMode.Dig => new[] { "Riding and driving" },
            TouchMode.Fly or TouchMode.Hover or TouchMode.Glide => new[] { "Flying" },
            TouchMode.Crane => new[] { "Tower crane" },
            TouchMode.Passenger => new[] { "Passengers" },
            TouchMode.Fight => new[] { "Fist fight" },
            TouchMode.Aura => new[] { "Aura battle" },
            TouchMode.Spectator or TouchMode.CreativeFly => new[] { "Creative mode" },
            _ => Array.Empty<string>(),
        };
        return ride.Concat(mode).ToArray();
    }

    /// <summary>Opens the sheet over <paramref name="rect"/>; <paramref name="onScreen"/> says an action already has a control.</summary>
    public void Show(TouchState state, Func<string, bool> onScreen, Rect2 rect)
    {
        Open = true;
        State = state;
        Rect = rect;
        _scroll = 0;
        _finger = -1;
        _entries.Clear();
        _headings.Clear();
        _seen.Clear();
        var groups = new List<(string Title, List<Entry> Entries)>();
        foreach (var prefix in GroupsFor(state))
            foreach (var (title, rows) in ControlsHelp.Groups)
            {
                if (!title.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var list = new List<Entry>();
                foreach (var row in rows)
                    if (ActionOf(row) is { } action && Wanted(action, onScreen))
                        list.Add(new Entry(Short(row.What), action, row.What.Contains("(hold", StringComparison.Ordinal),
                            action is PlayerInput.EmoteWheel or PlayerInput.QuickWheel));
                if (list.Count > 0) groups.Add((Short(title), list));
            }
        var always = new List<Entry>();
        foreach (var (label, action) in Always)
            if (Wanted(action, onScreen))
                always.Add(new Entry(label, action, false, action is PlayerInput.EmoteWheel or PlayerInput.QuickWheel));
        groups.Add(("Always", always));
        _layoutGroups = groups;
    }

    private List<(string Title, List<Entry> Entries)> _layoutGroups = new();

    public void Hide()
    {
        Open = false;
        _finger = -1;
        _pressed = null;
    }

    /// <summary>The row's action: its own, or the one key it names (<c>{cruise}</c>).</summary>
    private static string? ActionOf(ControlsHelp.Row row)
    {
        if (row.Action != null) return row.Action;
        if (row.Keys is { Length: > 2 } keys && keys[0] == '{' && keys[^1] == '}' && keys.IndexOf('}') == keys.Length - 1)
            return keys[1..^1];
        return null;
    }

    private bool Wanted(string action, Func<string, bool> onScreen)
    {
        if (!InputMap.HasAction(action) || onScreen(action) || !_seen.Add(action)) return false;
        // a stick's axis is the stick, on screen or not: a tap could never hold it
        foreach (var e in InputMap.ActionGetEvents(action))
            if (e is InputEventJoypadMotion { Axis: JoyAxis.LeftX or JoyAxis.LeftY or JoyAxis.RightX or JoyAxis.RightY })
                return false;
        return true;
    }

    /// <summary>F1's long sentence cut to a pill: before any ';' or ' (', at most 30 characters.</summary>
    internal static string Short(string what)
    {
        int cut = what.IndexOf(';');
        if (cut > 0) what = what[..cut];
        cut = what.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) what = what[..cut];
        cut = what.IndexOf(": at ", StringComparison.Ordinal);
        if (cut > 0) what = what[..cut];
        what = what.Trim();
        if (what.Length > 30) what = what[..29].TrimEnd() + "…";
        return what.Length == 0 ? what : char.ToUpperInvariant(what[0]) + what[1..];
    }

    /// <summary>Flows the pills into the sheet: headings, then pills left to right.</summary>
    public void Layout(Font font, float k)
    {
        float pad = 3f * k, pillH = 8f * k, gap = 1.5f * k, headH = 4.5f * k;
        int size = (int)(2.6f * k);
        float x0 = Rect.Position.X + pad, x1 = Rect.End.X - pad;
        float y = Rect.Position.Y + pad + 7f * k;
        CloseRect = new Rect2(Rect.End.X - pad - 7f * k, Rect.Position.Y + pad - 1f * k, 7f * k, 7f * k);
        _entries.Clear();
        _headings.Clear();
        foreach (var (title, list) in _layoutGroups)
        {
            _headings.Add(new Heading(title, new Vector2(x0, y + headH * 0.75f)));
            y += headH;
            float x = x0;
            foreach (var e in list)
            {
                float w = Mathf.Min(font.GetStringSize(e.Label, HorizontalAlignment.Left, -1, size).X + 5f * k, x1 - x0);
                if (x + w > x1 && x > x0)
                {
                    x = x0;
                    y += pillH + gap;
                }
                e.Rect = new Rect2(x, y, w, pillH);
                _entries.Add(e);
                x += w + gap;
            }
            y += pillH + gap * 2;
        }
        _contentHeight = y - (Rect.Position.Y + pad + 7f * k);
    }

    private float MaxScroll => Mathf.Max(0, _contentHeight - (Rect.Size.Y - 12f));

    public void Down(int finger, Vector2 at, double now)
    {
        if (_finger >= 0) return;
        _finger = finger;
        _downAt = at;
        _downTime = now;
        _dragged = false;
        _pressHandled = false;
        _pressed = EntryAt(at);
    }

    public void Drag(int finger, Vector2 at, Vector2 relative, float slop)
    {
        if (finger != _finger) return;
        if (!_dragged && at.DistanceTo(_downAt) > slop) _dragged = true;
        if (_dragged) _scroll = Mathf.Clamp(_scroll - relative.Y, 0, MaxScroll);
    }

    /// <summary>A finger lifted; true if it was this sheet's.</summary>
    public bool Up(int finger)
    {
        if (finger != _finger) return false;
        _finger = -1;
        if (_dragged || _pressHandled) return true;
        if (CloseRect.HasPoint(_downAt))
        {
            Hide();
            return true;
        }
        if (_pressed is { } e)
        {
            Hide();
            Tapped?.Invoke(e);
        }
        return true;
    }

    /// <summary>A press held still for half a second pins the pill under it.</summary>
    public void Process(double now)
    {
        if (_finger < 0 || _dragged || _pressHandled || _pressed == null || now - _downTime < 0.5) return;
        _pressHandled = true;
        Held?.Invoke(_pressed);
    }

    public bool Holding => _finger >= 0 && !_dragged && !_pressHandled && _pressed != null;
    public double HeldFor(double now) => now - _downTime;

    /// <summary>Where the pill being pressed is drawn now, or null.</summary>
    public Rect2? PressedRect => _pressed is { } e ? new Rect2(e.Rect.Position - new Vector2(0, _scroll), e.Rect.Size) : null;

    /// <summary>Where the pill for an action is drawn now (for <see cref="TouchCheck"/>), or null.</summary>
    public Vector2? CentreOf(string action)
    {
        foreach (var e in _entries)
            if (e.Action == action) return e.Rect.GetCenter() - new Vector2(0, _scroll);
        return null;
    }

    private Entry? EntryAt(Vector2 at)
    {
        var p = at + new Vector2(0, _scroll);
        foreach (var e in _entries)
            if (e.Rect.HasPoint(p)) return e;
        return null;
    }

    public void Draw(Control canvas, Font font, float k, Func<string, bool> pinned, Action<Rect2, float, Color, Color, float> box)
    {
        box(Rect, 0, new Color(0.07f, 0.09f, 0.11f, 0.94f), new Color(1, 1, 1, 0.2f), 1f);
        int title = (int)(3.6f * k), size = (int)(2.6f * k), small = (int)(2.1f * k);
        float pad = 3f * k;
        canvas.DrawString(font, new Vector2(Rect.Position.X + pad, Rect.Position.Y + pad + 4f * k), "More",
            HorizontalAlignment.Left, -1, title, Colors.White);
        box(CloseRect, CloseRect.Size.X / 2, new Color(1, 1, 1, 0.08f), new Color(1, 1, 1, 0.6f), 1.5f);
        canvas.DrawString(font, CloseRect.Position + new Vector2(0, CloseRect.Size.Y * 0.68f), "✕",
            HorizontalAlignment.Center, CloseRect.Size.X, size, Colors.White);
        var clip = new Rect2(Rect.Position.X, Rect.Position.Y + pad + 6f * k, Rect.Size.X, Rect.Size.Y - pad - 6f * k);
        var shift = new Vector2(0, -_scroll);
        foreach (var h in _headings)
        {
            var at = h.At + shift;
            if (clip.HasPoint(at)) canvas.DrawString(font, at, h.Text.ToUpperInvariant(), HorizontalAlignment.Left, -1, small, new Color(1, 1, 1, 0.55f));
        }
        foreach (var e in _entries)
        {
            var r = new Rect2(e.Rect.Position + shift, e.Rect.Size);
            if (r.Position.Y < clip.Position.Y || r.End.Y > clip.End.Y) continue;
            bool down = ReferenceEquals(e, _pressed) && _finger >= 0 && !_dragged;
            bool pin = pinned(e.Action);
            box(r, r.Size.Y / 2, down ? new Color(1f, 0.78f, 0.3f, 0.9f) : new Color(1, 1, 1, 0.06f),
                pin ? new Color(0.25f, 0.84f, 0.9f, 0.95f) : new Color(1, 1, 1, 0.45f), 1.5f);
            canvas.DrawString(font, r.Position + new Vector2(0, r.Size.Y * 0.66f), e.Label, HorizontalAlignment.Center, r.Size.X, size,
                down ? new Color(0.1f, 0.07f, 0.02f) : Colors.White);
        }
    }
}
