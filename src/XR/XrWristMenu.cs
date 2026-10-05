using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.XR;

/// <summary>
/// The wrist menu (#437): look at the back of your left wrist for a moment, as at a watch, and a
/// short menu opens on the UI panel, for what has no button on the controllers: the travel picker,
/// the inventory, the map, the bird journal, the controls, the fly camera, dropping the held item,
/// recentring. Point and pull the trigger to pick; B, the menu button or a pick closes it.
///
/// <para>
/// Each entry presses the action it stands for (<see cref="XrPad.Tap"/>), so the screens it opens
/// are the ones the keys open, and nothing learns a second way in.
/// </para>
/// </summary>
public partial class XrWristMenu : CanvasLayer
{
    /// <summary>The wrist held in view this long opens the menu, s.</summary>
    private const float Dwell = 0.6f;
    /// <summary>The wrist counts as looked at within this angle of the view's centre, radians.</summary>
    private const float LookCone = 0.42f;
    /// <summary>And no further from the eyes than this, m.</summary>
    private const float Near = 0.7f;

    /// <summary>The actions the menu presses: a prompt for one with no controller input names the wrist.</summary>
    private static readonly string[] Actions =
    {
        PlayerInput.RideMenu, PlayerInput.Inventory, PlayerInput.Teleport, PlayerInput.BirdJournal,
        PlayerInput.DropItem, PlayerInput.ToggleMode, PlayerInput.Help,
    };

    /// <summary>True when the wrist menu has an entry for <paramref name="action"/>.</summary>
    public static bool Reaches(string action) => Array.IndexOf(Actions, action) >= 0;

    private readonly XrRig _rig;
    private PanelContainer _panel = null!;
    private VBoxContainer _entries = null!;
    private float _looked;
    /// <summary>Opened (or closed) with the wrist in view: it has to leave the view before it opens again.</summary>
    private bool _rearm = true;

    public bool IsOpen => _panel.Visible;

    public XrWristMenu(XrRig rig) => _rig = rig;

    public override void _Ready()
    {
        Name = "XrWristMenu";
        Layer = 40;
        ProcessMode = ProcessModeEnum.Always;

        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);
        _panel = new PanelContainer { Visible = false, Theme = UiTheme.Get(), CustomMinimumSize = new Vector2(380, 0) };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.92f, 12, 18));
        centre.AddChild(_panel);
        _entries = UiKit.VBox(2);
        _panel.AddChild(_entries);
        // --xrwrist opens it from boot, for a screenshot without a headset (--xrsim)
        if (CmdArgs.Has("--xrwrist")) OpenWhenInWorld();
    }

    private void OpenWhenInWorld()
    {
        if (!_rig.InWorld)
        {
            GetTree().CreateTimer(1.0).Timeout += OpenWhenInWorld;
            return;
        }
        // "--xrwrist <entry>[,<entry>…]" also picks those, 2 s apart, as pulls on them would
        var picks = (CmdArgs.Value("--xrwrist", notFlag: true) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        PickNext(picks, 0);
    }

    private void PickNext(string[] picks, int i)
    {
        Open(XrSession.Anchor?.GetParent() as FootPlayer);
        if (i >= picks.Length) return;
        foreach (var child in _entries.GetChildren())
            if (child is Button b && b.Text == picks[i])
            {
                GD.Print($"[xr] wrist: picked {picks[i]}");
                b.EmitSignal(BaseButton.SignalName.Pressed);
            }
        if (i + 1 < picks.Length) GetTree().CreateTimer(2.0).Timeout += () => PickNext(picks, i + 1);
    }

    /// <summary>Once a frame, from the rig: opens the menu when the left wrist is looked at.</summary>
    /// <param name="inWorld">Not the title's backdrop: the title and its screens have their own menus.</param>
    public void Watch(Transform3D head, XRController3D left, FootPlayer? player, bool inWorld, float dt)
    {
        if (IsOpen || !inWorld) return;
        bool looked = false;
        if (left.GetHasTrackingData() && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            var to = left.GlobalPosition - head.Origin;
            float d = to.Length();
            // in front of the eyes and near, and the back of the wrist (the controller's +X, the
            // left hand's outside) turned up toward them, as when reading a watch
            looked = d < Near && d > 0.1f && (-head.Basis.Z).AngleTo(to) < LookCone
                     && left.GlobalBasis.X.Dot(-to / d) > 0.4f;
        }
        if (!looked)
        {
            _looked = 0f;
            _rearm = true;
            return;
        }
        if (!_rearm) return;
        _looked += dt;
        if (_looked >= Dwell)
        {
            _rearm = false;
            Open(player);
            left.TriggerHapticPulse("haptic", 0.0, 0.3, 0.04, 0.0);
        }
    }

    private void Open(FootPlayer? player)
    {
        foreach (var child in _entries.GetChildren())
        {
            _entries.RemoveChild(child);
            child.QueueFree();
        }
        _entries.AddChild(UiKit.Text("Wrist", UiTheme.FontHeading, UiTheme.Text, bold: true));
        bool onFoot = player is { Ride: RideKind.OnFoot, RidingWith: 0 };
        if (onFoot) Entry("Travel: mounts and vehicles", PlayerInput.RideMenu);
        Entry("Inventory", PlayerInput.Inventory);
        Entry("Map and place search", PlayerInput.Teleport);
        if (onFoot) Entry("Map in your hand", null, () => _rig.ToggleHandMap());
        Entry("Bird journal", PlayerInput.BirdJournal);
        if (onFoot) Entry("Drop the item in hand", PlayerInput.DropItem);
        Entry(player == null ? "Walk" : "Fly camera", PlayerInput.ToggleMode);
        Entry("Controls", PlayerInput.Help);
        Entry("Recentre the view", null, () => _rig.Recentre());
        Entry("Close", null, null);

        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
    }

    private void Entry(string text, string? action, Action? run = null)
    {
        var b = UiKit.MenuButton(text, 18);
        b.Pressed += () =>
        {
            Close();
            // after the menu let the pointer go, so the screen the action opens takes it
            if (action != null) Callable.From(() => XrPad.Tap(action)).CallDeferred();
            run?.Invoke();
        };
        _entries.AddChild(b);
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
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

    public override void _ExitTree() => UiFocus.Set(this, false);
}
