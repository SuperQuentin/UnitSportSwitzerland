using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Ui;
using UnitSport.World;

namespace UnitSport.Items;

/// <summary>
/// A traffic generator's settings (#971, the user), opened by the <see cref="TrafficTool"/>: whether it makes cars and how many a
/// minute, whether it is a destination, where its cars go (along the road at random, or a share to each destination), and which way
/// it faces. A new one is placed on OK (Cancel: nothing placed); a placed one is saved, or removed. Everything goes to the server
/// (<see cref="TrafficGenerators.RequestSet"/>), which checks the admin. Mouse, pad (focus, B closes) and the VR laser (the UI layer
/// is mirrored onto its panel) alike.
/// </summary>
public partial class TrafficGeneratorUi : CanvasLayer
{
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private CheckButton _generates = null!, _destination = null!;
    private SpinBox _rate = null!;
    private OptionButton _mode = null!;
    private VBoxContainer _shares = null!;
    private Button _remove = null!, _ok = null!;
    private readonly List<(int Id, HSlider Slider)> _sliders = new();
    private TrafficEndpoint? _ep;
    private bool _isNew;

    public bool IsOpen => _panel.Visible;

    /// <summary>The new one the window will place (drawn by the tool where it was put), and the ground there; null when editing.</summary>
    public TrafficEndpoint? Pending => IsOpen && _isNew ? _ep : null;
    public float PendingY { get; private set; }

    public override void _Ready()
    {
        Layer = 12;
        var root = new CenterContainer { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        _panel = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(460, 0) };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.96f, 14, 22));
        root.AddChild(_panel);
        var box = UiKit.VBox(10);
        _panel.AddChild(box);

        _title = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        box.AddChild(_title);

        var gen = UiKit.HBox(10);
        _generates = new CheckButton { Text = "Generates", FocusMode = Control.FocusModeEnum.All };
        _generates.Toggled += _ => Refresh();
        gen.AddChild(_generates);
        gen.AddChild(UiKit.Spacer(expand: true));
        gen.AddChild(UiKit.Text("cars / min", UiTheme.FontSmall, UiTheme.TextDim));
        _rate = new SpinBox { MinValue = 1, MaxValue = 120, Step = 1, CustomMinimumSize = new Vector2(96, 0), FocusMode = Control.FocusModeEnum.All };
        gen.AddChild(_rate);
        box.AddChild(gen);

        _destination = new CheckButton { Text = "Destination (takes the cars driving out past it)", FocusMode = Control.FocusModeEnum.All };
        box.AddChild(_destination);

        var go = UiKit.HBox(10);
        go.AddChild(UiKit.Text("Its cars go", UiTheme.FontBody, UiTheme.Text));
        _mode = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _mode.AddItem("along the road (random)");
        _mode.AddItem("to destinations");
        _mode.ItemSelected += _ => Refresh();
        go.AddChild(_mode);
        box.AddChild(go);

        _shares = UiKit.VBox(6);
        box.AddChild(_shares);

        var flip = UiKit.Button("Flip facing");
        flip.Pressed += () => { if (_ep is { } ep) _ep = ep with { Heading = ep.Heading + Mathf.Pi }; };
        box.AddChild(flip);

        box.AddChild(UiKit.Line());
        var buttons = UiKit.HBox(10);
        _remove = UiKit.Button("Remove");
        _remove.Pressed += Remove;
        buttons.AddChild(_remove);
        buttons.AddChild(UiKit.Spacer(expand: true));
        var cancel = UiKit.Button("Cancel");
        cancel.Pressed += Close;
        buttons.AddChild(cancel);
        _ok = UiKit.Button("OK", primary: true, minWidth: 96);
        _ok.Pressed += Save;
        buttons.AddChild(_ok);
        box.AddChild(buttons);
    }

    public override void _ExitTree() => UiFocus.Set(this, false);

    /// <summary>Opens on <paramref name="ep"/>: a new one (placed on OK) or a placed one; <paramref name="groundY"/>: where to draw a new one.</summary>
    public void Open(TrafficEndpoint ep, bool isNew, float groundY)
    {
        _ep = ep;
        _isNew = isNew;
        PendingY = groundY;
        _title.Text = isNew ? "New traffic generator" : $"Traffic generator #{ep.Id}";
        _generates.ButtonPressed = ep.Generates;
        _destination.ButtonPressed = ep.Destination;
        _rate.Value = ep.PerMinute;
        bool toDestinations = ep.Shares is { Length: > 0 } s && s.Any(x => x.To > 0);
        _mode.Selected = toDestinations ? 1 : 0;
        _remove.Visible = !isNew;
        _ok.Text = isNew ? "Place" : "Save";

        // a slider per destination there is (itself aside): its share of the cars, equal at first
        foreach (var c in _shares.GetChildren()) c.QueueFree();
        _sliders.Clear();
        foreach (var d in TrafficGenerators.Instance?.All.Where(x => x.Destination && x.Id != ep.Id) ?? [])
        {
            var row = UiKit.HBox(10);
            var name = UiKit.Text(string.Create(CultureInfo.InvariantCulture, $"#{d.Id} {Bearing(ep, d)}"), UiTheme.FontSmall, Traffic.DestinationColour(d.Id));
            name.CustomMinimumSize = new Vector2(110, 0);
            row.AddChild(name);
            float weight = ep.Shares?.FirstOrDefault(x => x.To == d.Id).Weight ?? 0f;
            var slider = new HSlider
            {
                MinValue = 0, MaxValue = 100, Step = 1, Value = toDestinations ? weight * 100f / Math.Max(1e-3f, ep.Shares!.Sum(x => x.Weight)) : 50,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                CustomMinimumSize = new Vector2(0, 22), FocusMode = Control.FocusModeEnum.All, Scrollable = false,
            };
            var value = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
            value.CustomMinimumSize = new Vector2(48, 0);
            slider.ValueChanged += _ => Percent();
            row.AddChild(slider);
            row.AddChild(value);
            _shares.AddChild(row);
            _sliders.Add((d.Id, slider));
        }
        if (_sliders.Count == 0) _shares.AddChild(UiKit.Text("No destination placed yet.", UiTheme.FontSmall, UiTheme.TextDim));

        _panel.Visible = true;
        MouseCapture.Mode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Refresh();
        Percent();
        _ok.CallDeferred(Control.MethodName.GrabFocus);
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        _ep = null;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Refresh()
    {
        _rate.Editable = _generates.ButtonPressed;
        _mode.Disabled = !_generates.ButtonPressed;
        _shares.Visible = _generates.ButtonPressed && _mode.Selected == 1;
    }

    /// <summary>Each slider's share of all of them, beside it.</summary>
    private void Percent()
    {
        double sum = _sliders.Sum(x => x.Slider.Value);
        foreach (var (_, slider) in _sliders)
            if (slider.GetParent().GetChild(2) is Label label)
                label.Text = sum > 0 ? string.Create(CultureInfo.InvariantCulture, $"{slider.Value * 100 / sum:0}%") : "-";
    }

    private void Save()
    {
        if (_ep is not { } ep || TrafficGenerators.Instance is not { } list) { Close(); return; }
        var shares = _mode.Selected == 1
            ? _sliders.Where(x => x.Slider.Value > 0).Select(x => new TrafficShare(x.Id, (float)x.Slider.Value)).ToArray()
            : null;
        list.RequestSet(ep with
        {
            Generates = _generates.ButtonPressed, Destination = _destination.ButtonPressed, PerMinute = (float)_rate.Value,
            Shares = shares is { Length: > 0 } ? shares : null,
        });
        Close();
    }

    private void Remove()
    {
        if (_ep is { Id: > 0 } ep) TrafficGenerators.Instance?.RequestRemove(ep.Id);
        Close();
    }

    /// <summary>Which way a destination lies from this one, as a compass word.</summary>
    private static string Bearing(TrafficEndpoint from, TrafficEndpoint to)
    {
        double a = Math.Atan2(to.N - from.N, to.E - from.E) * 180 / Math.PI;
        string[] names = ["east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east"];
        return names[(int)Math.Round(((a % 360) + 360) % 360 / 45) % 8];
    }
}
