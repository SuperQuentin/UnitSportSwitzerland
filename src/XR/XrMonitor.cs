using Godot;
using UnitSport.Player;
using UnitSport.Core;

namespace UnitSport.XR;

/// <summary>What the computer screen shows while someone plays in the headset.</summary>
public enum MonitorView
{
    /// <summary>Nothing 3D: the window keeps only the menus and HUD, and the GPU all for the headset.</summary>
    Off,
    /// <summary>One picture from where the head is: the game's own camera, with the head written into it.</summary>
    FirstPerson,
    /// <summary>The two eyes side by side, each from its own eye with the headset's projection.</summary>
    BothEyes,
    /// <summary>A chase camera behind and above the VR player, who is drawn whole for it.</summary>
    ThirdPerson,
}

/// <summary>
/// The monitor view in VR (#186). The headset renders its own viewport (<see cref="XrRig"/>), so
/// the window is free. In <see cref="MonitorView.FirstPerson"/> it shows the game's camera, which
/// the rig moves to the head every frame. The two other views are drawn by cameras of their own in
/// SubViewports on a full-window layer under every HUD, with the window's own 3D switched off so
/// nothing is rendered twice. Each extra view is an extra render of the world on the GPU the
/// headset needs, which is why Off exists.
/// </summary>
public partial class XrMonitor : CanvasLayer
{
    public const string LayerName = "XrMonitor";

    /// <summary>Half the distance between the eyes, for the simulated (untracked) headset.</summary>
    private const float SimEye = 0.032f;

    private readonly XrRig _rig;
    private readonly SubViewportContainer[] _boxes = new SubViewportContainer[2];
    private readonly SubViewport[] _views = new SubViewport[2];
    private readonly Camera3D[] _cams = new Camera3D[2];
    private readonly Dictionary<RideKind, Rideable?> _rides = new();
    private MonitorView _mode = (MonitorView)(-1);
    private Vector3 _chase;
    private bool _chaseSet;
    private bool _announce;

    /// <summary>The name a view has in Settings.</summary>
    public static string Label(MonitorView v) => v switch
    {
        MonitorView.Off => "Off",
        MonitorView.BothEyes => "Both eyes",
        MonitorView.ThirdPerson => "Third person",
        _ => "First person",
    };

    public XrMonitor(XrRig rig) => _rig = rig;

    public override void _Ready()
    {
        Name = LayerName;
        Layer = -100;   // under the HUD and the menus, which the monitor shows as well
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = 1001;   // after the rig has placed the head

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        row.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(row);
        for (int i = 0; i < 2; i++)
        {
            _boxes[i] = new SubViewportContainer
            {
                Stretch = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _views[i] = new SubViewport { GuiDisableInput = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
            _cams[i] = new Camera3D { Near = 0.05f, Current = true };
            _views[i].AddChild(_cams[i]);
            _boxes[i].AddChild(_views[i]);
            row.AddChild(_boxes[i]);
        }
        Core.GameSettings.Changed += Apply;
        Apply();
    }

    public override void _ExitTree()
    {
        Core.GameSettings.Changed -= Apply;
        GetTree().Root.Disable3D = false;
    }

    private void Apply()
    {
        var s = Core.GameSettings.Current;
        foreach (var c in _cams) c.Far = s.CameraFar;
        if (s.VrMonitor == _mode) return;
        _mode = s.VrMonitor;

        bool own = _mode is MonitorView.BothEyes or MonitorView.ThirdPerson;
        Visible = own;
        // the window's camera only draws in first person; otherwise it would be a wasted render
        GetTree().Root.Disable3D = _mode != MonitorView.FirstPerson;
        for (int i = 0; i < 2; i++)
        {
            bool on = own && (i == 0 || _mode == MonitorView.BothEyes);
            _boxes[i].Visible = on;
            _views[i].RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        }

        uint world = 0xFFFFFu & ~XrSession.HeadsetOnlyLayer & ~Interiors.DoorPortals.AllQuadLayers;
        if (_mode == MonitorView.ThirdPerson)
        {
            // the whole VR player, and no first-person viewmodel floating in front of nothing
            _cams[0].CullMask = world & ~Items.HeldItemVisual.ViewmodelLayer;
            _cams[0].Projection = Camera3D.ProjectionType.Perspective;
            _cams[0].Fov = 65f;
            _chaseSet = false;
        }
        else
        {
            // as the headset sees it: no own body, the doorways as the window's camera draws them
            foreach (var c in _cams)
                c.CullMask = (world | Interiors.DoorPortals.QuadLayers[0]) & ~XrSession.SpectatorOnlyLayer;
        }
        GD.Print($"[xr] monitor view: {_mode}");
        // said on a change, not at start: the monitor view at start is the one that was chosen
        if (_announce) _rig.Notice.Show("Monitor view", Label(_mode));
        _announce = true;
    }

    /// <summary>F8 steps through the monitor views (keyboard only: the hands are in the headset). Not F7, the airliner's flaps down (#436).</summary>
    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F8 }) return;
        var s = Core.GameSettings.Current;
        s.VrMonitor = (MonitorView)(((int)s.VrMonitor + 1) % 4);
        s.Commit();
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (_mode == MonitorView.BothEyes) UpdateEyes();
        else if (_mode == MonitorView.ThirdPerson) UpdateChase((float)delta);
    }

    /// <summary>
    /// Each eye from where the headset puts it, with the headset's own (asymmetric) frustum, so the
    /// pair is what the player sees, lens offsets included. The simulated headset has no eyes: a
    /// symmetric pair either side of the head.
    /// </summary>
    private void UpdateEyes()
    {
        var xr = XRServer.PrimaryInterface;
        bool live = !XrSession.Simulated && xr != null && xr.IsInitialized();
        for (int i = 0; i < 2; i++)
        {
            var cam = _cams[i];
            var size = _views[i].Size;
            double aspect = size.Y > 0 ? (double)size.X / size.Y : 1.0;
            if (!live)
            {
                cam.GlobalTransform = _rig.Head.GlobalTransform * Transform3D.Identity.Translated(new Vector3(i == 0 ? -SimEye : SimEye, 0, 0));
                cam.Projection = Camera3D.ProjectionType.Perspective;
                cam.Fov = 90f;
                continue;
            }
            cam.GlobalTransform = xr!.GetTransformForView((uint)i, _rig.Origin.GlobalTransform);
            var p = xr.GetProjectionForView((uint)i, aspect, cam.Near, cam.Far);
            // the projection as a frustum at the near plane: its height, and how far off centre it is
            float n = cam.Near;
            float height = 2f * n / p.Y.Y;
            var offset = new Vector2(p.Z.X * n / p.X.X, p.Z.Y * n / p.Y.Y);
            cam.SetFrustum(height, offset, n, cam.Far);
        }
    }

    /// <summary>Behind and above the VR player, eased, pulled in where the ground or a wall is in the way.</summary>
    private void UpdateChase(float dt)
    {
        var cam = _cams[0];
        var head = _rig.Head.GlobalPosition;
        var player = _rig.Anchor?.GetParent() as FootPlayer;
        float yaw = player != null ? player.GlobalRotation.Y : XrRig.YawOf(_rig.Head.GlobalBasis);

        float distance = 2.6f, height = 0.7f;
        if (player != null && player.Ride != RideKind.OnFoot)
        {
            if (!_rides.TryGetValue(player.Ride, out var ride)) _rides[player.Ride] = ride = Rideable.Create(player.Ride);
            if (ride != null)
            {
                distance = ride.ChaseDistance + 1.2f;
                height = ride.ChaseHeight;
            }
        }
        var behind = new Basis(Vector3.Up, yaw) * Vector3.Back;
        var wanted = head + behind * distance + Vector3.Up * height;

        // never inside a slope or a wall: stop the arm short of what it hits
        var exclude = new Godot.Collections.Array<Rid>();
        if (player != null) exclude.Add(player.GetRid());
        var hit = _rig.Head.GetWorld3D().DirectSpaceState.IntersectRay(
            PhysicsRayQueryParameters3D.Create(head, wanted, ~Hurtbox.Layer, exclude));
        if (hit.Count > 0)
        {
            var at = hit["position"].AsVector3();
            wanted = head + (at - head) * 0.85f;
        }

        if (!_chaseSet || _chase.DistanceTo(wanted) > 40f)
        {
            _chase = wanted;
            _chaseSet = true;
        }
        else _chase = _chase.Lerp(wanted, MathX.Damp(5f, dt));

        var look = head + Vector3.Down * 0.25f;
        if (_chase.DistanceSquaredTo(look) > 0.01f)
            cam.GlobalTransform = Transform3D.Identity.Translated(_chase).LookingAt(look, Vector3.Up);
    }
}
