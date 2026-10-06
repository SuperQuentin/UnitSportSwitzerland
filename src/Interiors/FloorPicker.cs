using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Interiors;

/// <summary>
/// An elevator cabin's buttons (#557): the floors, top one first, the one you are on greyed. The
/// arrows or the D-pad move between them and Enter / A presses one, the mouse clicks, and in VR the
/// panel is mirrored where the right hand's pointer reaches it (<c>XrUi</c>); Esc / B or E again
/// leaves without going anywhere.
/// </summary>
public partial class FloorPicker : CanvasLayer
{
    private Control _root = null!;
    private VBoxContainer _list = null!;
    private Action<int>? _chosen;
    private double _openedAt;

    public bool IsOpen => _root.Visible;

    public override void _Ready()
    {
        Layer = 12;
        _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Theme = UiTheme.Get() };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(centre);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(280, 0) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.94f, 14, 22));
        centre.AddChild(panel);
        var body = UiKit.VBox(8);
        panel.AddChild(body);
        body.AddChild(UiKit.Text("Elevator", UiTheme.FontHeading, UiTheme.Text, bold: true));
        _list = UiKit.VBox(6);
        body.AddChild(_list);
    }

    public void Open(IReadOnlyList<(int Floor, string Name)> floors, int here, Action<int> chosen)
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        _chosen = chosen;
        Button? first = null;
        foreach (var (floor, name) in floors)
        {
            var b = UiKit.Button(floor == here ? $"{name}  (here)" : name, primary: floor != here, minWidth: 240);
            b.Disabled = floor == here;
            int f = floor;
            b.Pressed += () => Choose(f);
            _list.AddChild(b);
            // the floor next to this one has the focus: up or down one is the usual ride
            if (!b.Disabled && (first == null || Math.Abs(floor - here) < Math.Abs((int)first.GetMeta("floor") - here))) first = b;
            b.SetMeta("floor", floor);
        }
        var cancel = UiKit.Button("Stay here", minWidth: 240);
        cancel.Pressed += Close;
        _list.AddChild(cancel);
        _root.Visible = true;
        _openedAt = Time.GetTicksMsec() / 1000.0;
        UiFocus.Set(this, true);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        (first ?? cancel).CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>Presses a floor's button, as a click would (for probes).</summary>
    public void Press(int floor) => Choose(floor);

    private void Choose(int floor)
    {
        var chosen = _chosen;
        Close();
        chosen?.Invoke(floor);
    }

    public void Close()
    {
        if (!_root.Visible) return;
        _root.Visible = false;
        _chosen = null;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_root.Visible) return;
        // the E that opened it is still being handed round: only a later one closes it
        bool fresh = Time.GetTicksMsec() / 1000.0 - _openedAt < 0.25;
        if (e.IsActionPressed("ui_cancel") || !fresh && e.IsActionPressed(PlayerInput.InteractMount)
            || e.IsActionPressed(PlayerInput.Menu))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
