using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A mirror on the wall above a washbasin (#439): a small viewport rendering the room from the
/// viewer's reflection in the glass, shown on a quad, the way the cab mirrors work (#69,
/// <see cref="Avatar.CabMirrors"/>). In VR it is where you see your own figure and real hands.
///
/// <para>
/// Cost: only the nearest mirror within <see cref="Range"/> that the viewer stands in front of
/// renders, every other frame, at <see cref="Pixels"/> pixels high in the PS1 grain; the others show
/// their last picture. Its viewport is made the first time it is the one.
/// </para>
/// </summary>
public partial class WallMirror : Node3D
{
    public static readonly Vector2 Size = new(0.5f, 0.6f);
    private const int Pixels = 96;
    private const float Range = 5f, Far = 40f;
    /// <summary>What it sees: the world and figures (your own VR body included), not the held-item viewmodel nor door portals' quads.</summary>
    private const uint Cull = 0xFFFFFu & ~Items.HeldItemVisual.ViewmodelLayer & ~DoorPortals.AllQuadLayers
                              | XR.XrSession.SpectatorOnlyLayer;

    private static readonly List<WallMirror> All = new();
    private static WallMirror? _active;
    private static ulong _pickedFrame;

    private MeshInstance3D _face = null!;
    private StandardMaterial3D _mat = null!;
    private SubViewport? _port;
    private Camera3D? _camera;

    public override void _Ready()
    {
        _mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.55f, 0.62f, 0.68f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            // a mirror shows the world left for right
            Uv1Scale = new Vector3(-1f, 1f, 1f),
            Uv1Offset = new Vector3(1f, 0f, 0f),
        };
        _face = new MeshInstance3D
        {
            Name = "Glass",
            Mesh = new QuadMesh { Size = Size },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_face);
    }

    /// <summary>Every wall mirror in the tree is in this group.</summary>
    public const string Group = "wall_mirrors";

    public override void _EnterTree()
    {
        All.Add(this);
        AddToGroup(Group);
    }

    public override void _ExitTree()
    {
        All.Remove(this);
        if (_active == this) _active = null;
    }

    public override void _Process(double delta)
    {
        if (DisplayServer.GetName() == "headless" || !IsVisibleInTree()) return;
        var eye = GetViewport().GetCamera3D();
        if (eye == null) return;
        ulong frame = Engine.GetProcessFrames();
        if (_pickedFrame != frame)
        {
            _pickedFrame = frame;
            _active = Pick(eye.GlobalPosition);
        }
        if (_active != this || frame % 2 != 0) return;
        Render(eye.GlobalPosition);
    }

    /// <summary>The nearest mirror in range that the eye is in front of.</summary>
    private static WallMirror? Pick(Vector3 eye)
    {
        WallMirror? best = null;
        float bestD = Range;
        foreach (var m in All)
        {
            if (!m.IsVisibleInTree()) continue;
            var t = m.GlobalTransform;
            float depth = (eye - t.Origin).Dot(t.Basis.Z.Normalized());
            float d = eye.DistanceTo(t.Origin);
            if (depth > 0.05f && d < bestD)
            {
                bestD = d;
                best = m;
            }
        }
        return best;
    }

    private void Render(Vector3 eye)
    {
        if (_port == null)
        {
            _port = new SubViewport
            {
                Name = "View",
                Size = new Vector2I(Mathf.RoundToInt(Pixels * Size.X / Size.Y), Pixels),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,
            };
            AddChild(_port);
            _camera = new Camera3D { Name = "Camera", CullMask = Cull, Far = Far };
            _port.AddChild(_camera);
            _camera.MakeCurrent();
            _mat.AlbedoTexture = _port.GetTexture();
            _mat.AlbedoColor = new Color(0.86f, 0.88f, 0.92f);
        }
        // the eye's reflection, looking square through the glass with a frustum framing exactly it,
        // so its near plane is the glass (CabMirrors.Aim)
        var face = _face.GlobalTransform;
        var centre = face.Origin;
        var normal = face.Basis.Z.Normalized();
        float depth = (eye - centre).Dot(normal);
        if (depth < 0.05f) return;
        var image = eye - 2f * depth * normal;
        var z = -normal;
        var y = (face.Basis.Y - z * face.Basis.Y.Dot(z)).Normalized();
        var basis = new Basis(y.Cross(z), y, z);
        _camera!.GlobalTransform = new Transform3D(basis, image);
        var local = basis.Transposed() * (centre - image);
        _camera.SetFrustum(Size.Y, new Vector2(local.X, local.Y), depth + 0.002f, Far);
        _port.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    /// <summary>A mirror above each washbasin of <paramref name="layout"/>, in the interior's own frame.</summary>
    public static void AddTo(Node3D interior, InteriorLayout layout)
    {
        foreach (var p in layout.Furniture)
        {
            if (p.Type != FurnitureType.Sink) continue;
            // the basin is authored with its back to −Z: the glass on that wall, facing the room
            var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
            var at = new Vector3(p.X, layout.FloorY(p.Floor) + 1.45f, p.Z) + basis * new Vector3(0f, 0f, -p.D / 2 + 0.012f);
            interior.AddChild(new WallMirror { Name = $"Mirror{interior.GetChildCount()}", Transform = new Transform3D(basis, at) });
        }
    }
}
