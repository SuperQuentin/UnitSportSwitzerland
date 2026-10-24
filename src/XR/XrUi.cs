using System.Linq;
using Godot;
using UnitSport.Ui;
using UnitSport.Core;

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
    /// <summary>
    /// The panel's pixels across on a standalone headset (#1112): it spans ~50 degrees, where a
    /// Quest 2 has ~20 pixels a degree. Its Android window is 3664 wide, and drawing the 2D that big
    /// (twice: the window and this panel) was half of a Quest 2's frame.
    /// </summary>
    private const float StandalonePixels = 1600f;
    /// <summary>The panel stays put until the head has turned this far from it, then glides round.</summary>
    private const float FollowAngle = 0.6f;

    /// <summary>Screen-effect layers that mean nothing on a floating panel (speed lines, flashes).</summary>
    private static readonly HashSet<string> Hidden = new() { "FeelScreen", "LensLayer" };

    private readonly XRCamera3D _head;
    private readonly XRController3D _hand;
    private SubViewport _view = null!;
    /// <summary>
    /// Where the panel is (and, without a composition layer, the panel itself). With a headset the
    /// picture goes on <see cref="_layer"/> instead: the compositor draws it at its own resolution,
    /// so text stays sharp; drawn into the 3D frame it was resampled at the eyes' resolution and
    /// unreadable on a Quest (#1112).
    /// </summary>
    private MeshInstance3D _panel = null!;
    private OpenXRCompositionLayerQuad? _layer;
    /// <summary>The reticle drawn into the panel's picture when it is a layer (a 3D one would be under it).</summary>
    private Control? _cursor;
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

        // the layout stays the root's (Size2DOverride); a standalone headset draws it smaller
        var pixels = Platform.IsStandaloneVr && _size.X > StandalonePixels ? _size * (StandalonePixels / _size.X) : _size;
        _view = new SubViewport
        {
            Name = "UiView",
            Size = (Vector2I)pixels,
            Size2DOverride = (Vector2I)_size,
            Size2DOverrideStretch = true,
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

        // the panel as a composition layer of its own, child of the tracking origin as OpenXR wants;
        // in front of the world like the NoDepthTest quad. Not with --xrsim: no compositor there
        if (!XrSession.Simulated && _head.GetParent() is XROrigin3D origin)
        {
            _layer = new OpenXRCompositionLayerQuad
            {
                Name = "UiLayer",
                LayerViewport = _view,
                QuadSize = ((QuadMesh)_panel.Mesh).Size,
                SortOrder = 1,
                AlphaBlend = true,
            };
            origin.AddChild(_layer);
            if (_layer.IsNativelySupported())
            {
                _panel.Visible = false;
                var cursorLayer = new CanvasLayer { Name = "XrCursor", Layer = 128, CustomViewport = _view };
                _cursor = new Control { Name = "Reticle", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
                _cursor.Draw += DrawCursor;
                cursorLayer.AddChild(_cursor);
                AddChild(cursorLayer);
            }
            else
            {
                _layer.QueueFree();
                _layer = null;
            }
        }

        // nobody sees a standalone headset's window: it draws no 2D (the panel, its own mask, shows it
        // all). Not the window inactive: the headset's viewport is its child and stopped with it
        if (Platform.IsStandaloneVr && _layer != null)
        {
            root.CanvasCullMask = 0;
            // nor anything at all: no clear, and straight to the screen, with no copy of it to blit
            // (the window's two passes were 4 ms of a Quest 2's frame)
            RenderingServer.ViewportSetClearMode(root.GetViewportRid(), RenderingServer.ViewportClearMode.Never);
            RenderingServer.ViewportSetRenderDirectToScreen(root.GetViewportRid(), true);
            // and not updated at all: its pass was still ~1.3 ms (the headset's viewport, its child, still renders)
            RenderingServer.ViewportSetUpdateMode(root.GetViewportRid(), RenderingServer.ViewportUpdateMode.Disabled);
        }
        GD.Print($"[xr] panel {_view.Size.X}x{_view.Size.Y} for a {_size.X}x{_size.Y} canvas");
        // the popups (a drop-down's list, a menu) on the panel too, under the cursor
        _popupLayer = new CanvasLayer { Name = "XrPopups", Layer = 127, CustomViewport = _view };
        AddChild(_popupLayer);
        // "--xrpopup [s]": that long in, the first drop-down on screen opens, for a headset shot of its list
        if (CmdArgs.Has("--xrpopup"))
            GetTree().CreateTimer(CmdArgs.Double("--xrpopup") ?? 6).Timeout += () =>
            {
                var drop = root.FindChildren("*", nameof(OptionButton), true, false).OfType<OptionButton>().FirstOrDefault(o => o.IsVisibleInTree());
                drop?.ShowPopup();
                var popup = drop?.GetPopup();
                GD.Print($"[xr] popup {(drop == null ? "none on screen" : $"{drop.Name}: embedded {popup!.IsEmbedded()}, at {popup.Position} {popup.Size}, visible {popup.Visible}, tracked {_popups.Any(p => p.Window == popup)} of {_popups.Count}")}");
            };

        foreach (var node in AllNodes(root)) Redirect(node);
        GetTree().NodeAdded += Redirect;
    }

    public override void _ExitTree()
    {
        GetTree().NodeAdded -= Redirect;
        _layer?.QueueFree();
    }

    /// <summary>The reticle in the panel's own pixels: a ring, filled while the trigger holds.</summary>
    private void DrawCursor()
    {
        float r = _size.X / 160f;   // in the canvas' units: the override scales it with the panel
        var colour = _clickWas ? UiTheme.Amber : UiTheme.AmberDim with { A = 0.85f };
        if (_clickWas) _cursor!.DrawCircle(Vector2.Zero, r, colour);
        else _cursor!.DrawArc(Vector2.Zero, r, 0f, Mathf.Tau, 24, colour, r * 0.35f, antialiased: true);
    }

    private static IEnumerable<Node> AllNodes(Node n)
    {
        yield return n;
        // internal children too: a drop-down's list is one of its button's
        foreach (var c in n.GetChildren(includeInternal: true))
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
        if (node is Window window) TrackPopup(window);
        // the monitor by its type: it is added under another name and renamed in its _Ready, after this ran (#1036)
        if (node is not CanvasLayer layer || layer is XrMonitor || Hidden.Contains(layer.Name) || layer.CustomViewport != null) return;
        var view = _view.GetViewportRid();
        var canvas = layer.GetCanvas();
        RenderingServer.ViewportAttachCanvas(view, canvas);
        RenderingServer.ViewportSetCanvasStacking(view, canvas, layer.Layer, layer.GetIndex());
        // a layer taken out of the tree (not just hidden) stops showing on screen: and on the panel
        layer.TreeExiting += () => RenderingServer.ViewportRemoveCanvas(view, canvas);
    }

    private CanvasLayer? _popupLayer;
    private readonly List<(Window Window, TextureRect Picture)> _popups = new();

    /// <summary>
    /// A popup window (a drop-down's list, a context menu) is embedded in the root viewport and drawn by it, not by
    /// any canvas the panel draws: in VR it answered the pointer but was never seen (#1112). Its picture, the
    /// window's own viewport texture, is drawn on the panel where it stands on the root's canvas.
    /// </summary>
    private void TrackPopup(Window window)
    {
        if (_popupLayer == null || window == GetTree().Root || window is XrMonitor) return;
        var picture = new TextureRect
        {
            Name = "Popup", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        _popupLayer.AddChild(picture);
        _popups.Add((window, picture));
        window.TreeExiting += () =>
        {
            _popups.RemoveAll(p => p.Window == window);
            picture.QueueFree();
        };
    }

    private void UpdatePopups()
    {
        foreach (var (window, picture) in _popups)
        {
            bool shown = IsInstanceValid(window) && window.Visible && window.IsEmbedded();
            if (picture.Visible != shown)
            {
                picture.Visible = shown;
                // a standalone headset's window is not updated (above), and a popup only draws when its parent does:
                // its picture stayed empty there, though the preview drew it. Drawn on its own while it is open
                if (IsInstanceValid(window))
                    RenderingServer.ViewportSetUpdateMode(window.GetViewportRid(),
                        shown ? RenderingServer.ViewportUpdateMode.Always : RenderingServer.ViewportUpdateMode.WhenParentVisible);
            }
            if (!shown) continue;
            picture.Texture ??= window.GetTexture();
            picture.Position = window.Position;
            picture.Size = window.Size;
        }
    }

    public void UpdatePanel(float dt)
    {
        UpdatePopups();

        var eye = _head.GlobalTransform;
        float headYaw = XrRig.YawOf(eye.Basis);
        if (float.IsNaN(_yaw)) _yaw = headYaw;
        // glide toward the head's heading only once it has looked well away
        float off = Mathf.AngleDifference(_yaw, headYaw);
        if (Mathf.Abs(off) > FollowAngle) _yaw += off * MathX.Damp(3f, dt);

        var forward = new Basis(Vector3.Up, _yaw) * Vector3.Forward;
        var centre = eye.Origin + forward * Distance + Vector3.Down * 0.12f;
        // the quad faces +Z: turn it so its front looks back at the eyes
        _panel.GlobalTransform = new Transform3D(new Basis(Vector3.Up, _yaw), centre);
        if (_layer != null) _layer.GlobalTransform = _panel.GlobalTransform;

        UpdatePointer();
    }

    private void UpdatePointer()
    {
        Pointing = false;
        _ray.Visible = false;
        _dot.Visible = false;
        if (_cursor != null) _cursor.Visible = false;
        if (MouseCapture.Mode == Input.MouseModeEnum.Captured || !Aim(out var window))
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
        bool click = _hand.GetFloat(XrPad.Inputs.Trigger) > 0.6f;
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
        if (_cursor != null)
        {
            _cursor.Visible = true;
            _cursor.Position = uv * _size;
            _cursor.QueueRedraw();
        }
        else
        {
            _dot.Visible = true;
            _dot.GlobalPosition = hit;
            _dotMat.AlbedoColor = _clickWas ? UiTheme.Amber : UiTheme.AmberDim with { A = 0.75f };
        }

        // a point on the panel is a point on the root canvas; the root's stretch takes it to the window
        window = GetTree().Root.GetFinalTransform() * (uv * _size);
        return true;
    }
}
