using Godot;
using UnitSport.Ui;

namespace UnitSport.XR;

/// <summary>
/// The game's screen-space UI (menus, HUD, prompts, inventory, chat) on a panel in front of the
/// headset, pointed at and clicked with the right hand.
///
/// <para>
/// Nothing is rebuilt: every <see cref="CanvasLayer"/>'s canvas is drawn by one SubViewport as
/// well as by the root (<see cref="Redirect"/>), and the SubViewport also draws the root canvas
/// (it shares the root's <see cref="World2D"/>), which catches the Controls that sit outside any
/// layer. Input stays with the root viewport, which still owns the Controls: the SubViewport is
/// the root's size, so a point on the panel is a point on the root's canvas, and the hand's ray
/// becomes ordinary mouse events there. Pad navigation (D-pad, A, B) works as it always did.
/// </para>
/// </summary>
public partial class XrUi : Node3D
{
    /// <summary>Panel width in metres, and how far ahead of the eyes it floats.</summary>
    private const float Width = 1.5f, Distance = 1.7f;
    /// <summary>The panel stays put until the head has turned this far from it, then glides round.</summary>
    private const float FollowAngle = 0.6f;

    /// <summary>Screen-effect layers that mean nothing on a floating panel (speed lines, flashes).</summary>
    private static readonly HashSet<string> Hidden = new() { "FeelScreen", "LensLayer", XrMonitor.LayerName };

    private readonly XRCamera3D _head;
    private readonly XRController3D _hand;
    private SubViewport _view = null!;
    private MeshInstance3D _panel = null!;
    private MeshInstance3D _ray = null!;
    private MeshInstance3D _dot = null!;
    private StandardMaterial3D _panelMat = null!;
    private StandardMaterial3D _dotMat = null!;
    private Vector2 _size;
    private float _yaw = float.NaN;
    private bool _clickWas;
    private Vector2 _lastPoint;

    /// <summary>The right hand is on the panel while the mouse is free (a menu is open).</summary>
    public bool Pointing { get; private set; }

    public XrUi(XRCamera3D head, XRController3D hand)
    {
        _head = head;
        _hand = hand;
    }

    public override void _Ready()
    {
        Name = "XrUi";
        TopLevel = true;
        var root = GetTree().Root;
        _size = root.GetVisibleRect().Size;

        _view = new SubViewport
        {
            Name = "UiView",
            Size = (Vector2I)_size,
            TransparentBg = true,
            Disable3D = true,
            World2D = root.World2D,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            GuiDisableInput = true,
        };
        AddChild(_view);

        _panelMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            DisableFog = true,   // UI, not scenery: never hazed by the distance fog
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,   // never sunk into a wall or the terrain
            AlbedoTexture = _view.GetTexture(),
            RenderPriority = (int)Material.RenderPriorityMax - 1,
        };
        _panel = new MeshInstance3D
        {
            Name = "Panel",
            Mesh = new QuadMesh { Size = new Vector2(Width, Width * _size.Y / _size.X) },
            MaterialOverride = _panelMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = XrSession.HeadsetOnlyLayer,
        };
        AddChild(_panel);

        // The pointer in the menus' look (docs/notes/ui/style-guide.md): a faint white beam, and
        // amber only where it marks something, the reticle on the panel.
        _ray = new MeshInstance3D
        {
            Name = "Pointer",
            Mesh = new CylinderMesh { TopRadius = 0.0015f, BottomRadius = 0.0025f, Height = 1f, RadialSegments = 6, Rings = 1 },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            DisableFog = true,   // UI, not scenery: never hazed by the distance fog
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(UiTheme.Text, 0.35f),
                NoDepthTest = true,
                RenderPriority = (int)Material.RenderPriorityMax - 1,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
            Layers = XrSession.HeadsetOnlyLayer,
        };
        AddChild(_ray);
        _dotMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            DisableFog = true,   // UI, not scenery: never hazed by the distance fog
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = UiTheme.Amber,
            NoDepthTest = true,
            RenderPriority = (int)Material.RenderPriorityMax,
        };
        _dot = new MeshInstance3D
        {
            Name = "Reticle",
            Mesh = new SphereMesh { Radius = 0.006f, Height = 0.012f, RadialSegments = 12, Rings = 6 },
            MaterialOverride = _dotMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
            Layers = XrSession.HeadsetOnlyLayer,
        };
        AddChild(_dot);

        foreach (var node in AllNodes(root)) Redirect(node);
        GetTree().NodeAdded += Redirect;
    }

    public override void _ExitTree() => GetTree().NodeAdded -= Redirect;

    private static IEnumerable<Node> AllNodes(Node n)
    {
        yield return n;
        foreach (var c in n.GetChildren())
            foreach (var d in AllNodes(c)) yield return d;
    }

    /// <summary>
    /// Draws a layer on the panel as well: its canvas is attached to the SubViewport straight in
    /// the RenderingServer. Setting <see cref="CanvasLayer.CustomViewport"/> on a layer already in
    /// the tree (and <see cref="SceneTree.NodeAdded"/> is only raised after it entered) leaves the
    /// layer disconnecting the wrong viewport when it leaves, so the node itself is not touched.
    /// </summary>
    private void Redirect(Node node)
    {
        if (node is not CanvasLayer layer || Hidden.Contains(layer.Name) || layer.CustomViewport != null) return;
        var view = _view.GetViewportRid();
        var canvas = layer.GetCanvas();
        RenderingServer.ViewportAttachCanvas(view, canvas);
        RenderingServer.ViewportSetCanvasStacking(view, canvas, layer.Layer, layer.GetIndex());
        // a layer taken out of the tree (not just hidden) stops showing on screen: and on the panel
        layer.TreeExiting += () => RenderingServer.ViewportRemoveCanvas(view, canvas);
    }

    public void UpdatePanel(float dt)
    {

        var eye = _head.GlobalTransform;
        float headYaw = XrRig.YawOf(eye.Basis);
        if (float.IsNaN(_yaw)) _yaw = headYaw;
        // glide toward the head's heading only once it has looked well away
        float off = Mathf.AngleDifference(_yaw, headYaw);
        if (Mathf.Abs(off) > FollowAngle) _yaw += off * (1f - Mathf.Exp(-3f * dt));

        var forward = new Basis(Vector3.Up, _yaw) * Vector3.Forward;
        var centre = eye.Origin + forward * Distance + Vector3.Down * 0.12f;
        // the quad faces +Z: turn it so its front looks back at the eyes
        _panel.GlobalTransform = new Transform3D(new Basis(Vector3.Up, _yaw), centre);

        UpdatePointer();
    }

    private void UpdatePointer()
    {
        Pointing = false;
        _ray.Visible = false;
        _dot.Visible = false;
        if (Input.MouseMode == Input.MouseModeEnum.Captured || !Aim(out var window))
        {
            // a click held as the hand left the panel (or the menu closed) still has to come up
            if (_clickWas) Click(false, _lastPoint);
            return;
        }
        Pointing = true;

        if (window.DistanceSquaredTo(_lastPoint) > 0.25f)
        {
            Input.ParseInputEvent(new InputEventMouseMotion { Position = window, GlobalPosition = window });
            _lastPoint = window;
        }
        bool click = _hand.GetFloat("trigger") > 0.6f;
        if (click != _clickWas)
        {
            Click(click, window);
            if (click) _hand.TriggerHapticPulse("haptic", 0.0, 0.2, 0.03, 0.0);
        }
    }

    private void Click(bool pressed, Vector2 at)
    {
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = pressed,
            Position = at,
            GlobalPosition = at,
            ButtonMask = pressed ? MouseButtonMask.Left : 0,
        });
        _clickWas = pressed;
    }

    /// <summary>Where the right hand's ray meets the panel, in window coordinates; draws the ray.</summary>
    private bool Aim(out Vector2 window)
    {
        window = default;
        if (!_hand.GetHasTrackingData()) return false;

        var aim = _hand.GlobalTransform;
        var from = aim.Origin;
        var dir = -aim.Basis.Z;
        var plane = new Plane(_panel.GlobalBasis.Z, _panel.GlobalPosition);
        if (plane.IntersectsRay(from, dir) is not { } hit) return false;

        var local = _panel.GlobalTransform.AffineInverse() * hit;
        var quad = ((QuadMesh)_panel.Mesh).Size;
        var uv = new Vector2(local.X / quad.X + 0.5f, 0.5f - local.Y / quad.Y);
        if (uv.X is < 0f or > 1f || uv.Y is < 0f or > 1f) return false;

        float length = (hit - from).Length();
        _ray.Visible = true;
        _ray.GlobalTransform = new Transform3D(
            Basis.LookingAt(dir, Vector3.Up) * new Basis(Vector3.Right, -Mathf.Pi / 2f) * Basis.FromScale(new Vector3(1f, length, 1f)),
            from + dir * length * 0.5f);
        // the reticle sits on the panel; pressed, it fills to the full amber, as a pressed button does
        _dot.Visible = true;
        _dot.GlobalPosition = hit;
        _dotMat.AlbedoColor = _clickWas ? UiTheme.Amber : UiTheme.AmberDim with { A = 0.75f };

        // a point on the panel is a point on the root canvas; the root's stretch takes it to the window
        window = GetTree().Root.GetFinalTransform() * (uv * _size);
        return true;
    }
}
