using Godot;

namespace UnitSport.Core;

/// <summary>
/// Settings → Steering wheel (issue #68): which device, the rotation range set in its driver, and
/// the bindings, each with a live bar. "Assign" waits for the control to move — turn the wheel
/// right, press the pedal, press the button — and binds whatever moved, so an unknown wheel is set
/// up without knowing its axis numbers. Known wheels start from a <see cref="WheelPresets"/> entry.
/// </summary>
public partial class WheelPanel : VBoxContainer
{
    private enum Target { None, Steer, Throttle, Brake, Clutch, Handbrake, Button }

    /// <summary>What a wheel button can be bound to, in the order the panel lists them.</summary>
    private static readonly (string Label, string Action)[] ButtonTargets =
    {
        ("Handbrake (button)", WheelSettings.HandbrakeButton),
        ("Look behind", PlayerInput.LookBehind),
        ("Camera view", PlayerInput.CameraToggle),
        ("Headlights", PlayerInput.LightsToggle),
        ("Soft top", PlayerInput.RoofToggle),
        ("Get in / out", PlayerInput.InteractMount),
        ("Engine on / off", PlayerInput.EngineToggle),
        ("Menu", PlayerInput.Menu),
    };

    private const float AssignSeconds = 8f;

    private Label _status = null!;
    private OptionButton _devices = null!;
    private string[] _listed = Array.Empty<string>();
    private readonly List<Action> _refresh = new();

    private Target _target;
    private string _targetAction = "";
    private Button? _targetButton;
    private string _targetText = "";
    private float[] _baseline = Array.Empty<float>();
    private HashSet<int> _baselineButtons = new();
    private float _timer;

    private static WheelSettings W => GameSettings.Current.Wheel;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeColorOverride("font_color", new Color(0.55f, 0.59f, 0.65f));
        AddChild(_status);

        var deviceRow = new HBoxContainer();
        deviceRow.AddChild(new Label { Text = "Device", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _devices = new OptionButton { CustomMinimumSize = new Vector2(260, 0), ClipText = true };
        _devices.ItemSelected += i =>
        {
            if (i >= 0 && i < _listed.Length) SteeringWheel.Select(_listed[i]);
            Refresh();
        };
        deviceRow.AddChild(_devices);
        AddChild(deviceRow);

        SettingsMenu.ToggleRow(this, "Use steering wheel", W.Enabled, on => W.Enabled = on);
        SettingsMenu.SliderRow(this, "Rotation range", WheelSettings.MinRangeDeg, WheelSettings.MaxRangeDeg, 10,
            W.RangeDeg, v => W.RangeDeg = (float)v, v => $"{v:F0}° (the same as in the wheel's driver)");

        AxisRow("Steering", Target.Steer);
        var invert = new CheckBox { Text = "Invert steering", ButtonPressed = W.SteerInvert };
        invert.Toggled += on => { W.SteerInvert = on; GameSettings.Current.Commit(); };
        _refresh.Add(() => invert.SetPressedNoSignal(W.SteerInvert));
        AddChild(invert);
        AxisRow("Throttle", Target.Throttle);
        AxisRow("Brake", Target.Brake);
        AxisRow("Clutch", Target.Clutch);
        AxisRow("Handbrake", Target.Handbrake);
        foreach (var (label, action) in ButtonTargets) ButtonRow(label, action);

        var reset = new Button { Text = "Reset to the device's preset" };
        reset.Pressed += () =>
        {
            if (SteeringWheel.DeviceName is { } name) WheelPresets.Reset(W, name);
            GameSettings.Current.Commit();
            Refresh();
        };
        AddChild(reset);

        VisibilityChanged += () => { if (!IsVisibleInTree()) Cancel(); };
        Refresh();
    }

    public override void _ExitTree() => Cancel();

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;

        var devices = SteeringWheel.Devices;
        string? claimed = SteeringWheel.DeviceName;
        if (!devices.SequenceEqual(_listed) || _devices.Selected != Array.IndexOf(_listed, claimed ?? W.Device))
            ListDevices(devices, claimed);
        _status.Text = SteeringWheel.Unavailable
            ? "Steering wheels need SDL3, which did not load."
            : claimed == null
                ? (W.Enabled ? "No wheel in use. Plug one in, or pick it above." : "Off: wheels are left to the game as joypads.")
                : $"{claimed}" + (W.Preset.Length > 0 ? $" — preset: {W.Preset}" : " — no preset, assign the controls below");

        if (_target != Target.None) Assign((float)delta);
        foreach (var refresh in _refresh) refresh();
    }

    private void ListDevices(IReadOnlyList<string> devices, string? claimed)
    {
        _listed = devices.ToArray();
        _devices.Clear();
        foreach (string d in _listed) _devices.AddItem(d);
        _devices.Selected = Array.IndexOf(_listed, claimed ?? W.Device);
    }

    // ---------------------------------------------------------------------------------------
    // rows
    // ---------------------------------------------------------------------------------------

    private void AxisRow(string label, Target target)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(110, 0) });
        var bar = new ProgressBar
        {
            MinValue = target == Target.Steer ? -1 : 0, MaxValue = 1, ShowPercentage = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 14),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        row.AddChild(bar);
        var info = new Label { CustomMinimumSize = new Vector2(96, 0), HorizontalAlignment = HorizontalAlignment.Right };
        info.AddThemeColorOverride("font_color", new Color(0.55f, 0.59f, 0.65f));
        row.AddChild(info);
        var assign = new Button { Text = "Assign" };
        assign.Pressed += () => Begin(target, "", assign, target == Target.Steer ? "Turn right…" : "Press it…");
        row.AddChild(assign);
        if (target != Target.Steer)
        {
            var clear = new Button { Text = "Clear" };
            clear.Pressed += () => { Axis(target)!.Axis = -1; GameSettings.Current.Commit(); };
            row.AddChild(clear);
        }
        AddChild(row);

        _refresh.Add(() =>
        {
            var raw = SteeringWheel.RawAxes;
            if (target == Target.Steer)
            {
                float v = W.SteerAxis < raw.Count ? raw[W.SteerAxis] : 0f;
                if (W.SteerInvert) v = -v;
                bar.Value = v;
                info.Text = W.SteerAxis < raw.Count || raw.Count == 0 ? $"{v * W.RangeDeg * 0.5f:F0}°  axis {W.SteerAxis}" : "none";
                return;
            }
            var a = Axis(target)!;
            bar.Value = a.Bound && a.Axis < raw.Count ? a.Read(raw[a.Axis]) : 0;
            info.Text = a.Bound ? $"axis {a.Axis}" : "none";
        });
    }

    private void ButtonRow(string label, string action)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var info = new Label { CustomMinimumSize = new Vector2(96, 0), HorizontalAlignment = HorizontalAlignment.Right };
        info.AddThemeColorOverride("font_color", new Color(0.55f, 0.59f, 0.65f));
        row.AddChild(info);
        var assign = new Button { Text = "Assign" };
        assign.Pressed += () => Begin(Target.Button, action, assign, "Press it…");
        row.AddChild(assign);
        var clear = new Button { Text = "Clear" };
        clear.Pressed += () => { Unbind(action); GameSettings.Current.Commit(); };
        row.AddChild(clear);
        AddChild(row);

        _refresh.Add(() =>
        {
            var bound = W.Buttons.Where(b => b.Value == action).Select(b => b.Key).ToList();
            bool lit = bound.Any(SteeringWheel.RawButton);
            info.Text = bound.Count == 0 ? "none" : string.Join(", ", bound.Select(ButtonName)) + (lit ? "  ●" : "");
        });
    }

    private static string ButtonName(int b)
    {
        if (b < SteeringWheel.HatBase) return $"button {b}";
        int hat = (b - SteeringWheel.HatBase) / 4, dir = (b - SteeringWheel.HatBase) % 4;
        return $"hat {hat} " + dir switch { 0 => "up", 1 => "right", 2 => "down", _ => "left" };
    }

    private static WheelAxis? Axis(Target t) => t switch
    {
        Target.Throttle => W.Throttle,
        Target.Brake => W.Brake,
        Target.Clutch => W.Clutch,
        Target.Handbrake => W.Handbrake,
        _ => null,
    };

    private static void Unbind(string action)
    {
        foreach (int b in W.Buttons.Where(p => p.Value == action).Select(p => p.Key).ToList()) W.Buttons.Remove(b);
    }

    private void Refresh()
    {
        foreach (var refresh in _refresh) refresh();
    }

    // ---------------------------------------------------------------------------------------
    // move-to-assign
    // ---------------------------------------------------------------------------------------

    private void Begin(Target target, string action, Button button, string prompt)
    {
        Cancel();
        if (SteeringWheel.DeviceName == null) return;
        _target = target;
        _targetAction = action;
        _targetButton = button;
        _targetText = button.Text;
        button.Text = prompt;
        _baseline = SteeringWheel.RawAxes.ToArray();
        _baselineButtons = new HashSet<int>(SteeringWheel.PressedButtons);
        _timer = AssignSeconds;
        SteeringWheel.Assigning = true;
    }

    private void Assign(float dt)
    {
        _timer -= dt;
        if (_timer <= 0f || SteeringWheel.DeviceName == null)
        {
            Cancel();
            return;
        }

        if (_target == Target.Button)
        {
            foreach (int b in SteeringWheel.PressedButtons)
            {
                if (_baselineButtons.Contains(b)) continue;
                Unbind(_targetAction);
                W.Buttons[b] = _targetAction;
                Done();
                return;
            }
            // a button held since before Assign counts once let go and pressed again
            _baselineButtons.IntersectWith(SteeringWheel.PressedButtons);
            return;
        }

        var raw = SteeringWheel.RawAxes;
        int best = -1;
        float moved = 0f;
        for (int i = 0; i < Math.Min(raw.Count, _baseline.Length); i++)
        {
            float d = MathF.Abs(raw[i] - _baseline[i]);
            if (d > moved) { moved = d; best = i; }
        }
        // a quarter turn of a 900° wheel is half its axis; a pedal is pressed well past half way
        if (best < 0 || moved < (_target == Target.Steer ? 0.3f : 0.6f)) return;

        float rest = _baseline[best];
        if (_target == Target.Steer)
        {
            W.SteerAxis = best;
            W.SteerInvert = raw[best] < rest;   // the player turned right
        }
        else
        {
            var a = Axis(_target)!;
            a.Axis = best;
            // rest at an end (a pedal on its own axis) or in the middle (half of a combined axis)
            a.From = MathF.Abs(rest) > 0.7f ? MathF.Sign(rest) : 0f;
            a.To = MathF.Sign(raw[best] - rest);
        }
        Done();
    }

    private void Done()
    {
        GameSettings.Current.Commit();
        Cancel();
        Refresh();
    }

    private void Cancel()
    {
        if (_targetButton != null) _targetButton.Text = _targetText;
        _targetButton = null;
        _target = Target.None;
        SteeringWheel.Assigning = false;
    }
}
