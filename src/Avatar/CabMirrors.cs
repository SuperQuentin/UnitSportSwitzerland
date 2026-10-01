using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The working mirrors of a cab (#69, shared with trucks and buses, #157): each a small
/// <see cref="SubViewport"/> rendering the world from the eye's reflection in the mirror's plane,
/// shown on a quad on the glass. Only the local driver in the cockpit has them, built on first use;
/// one mirror re-renders a frame, in turn.
/// </summary>
public sealed class CabMirrors
{
    /// <summary>One mirror: a small viewport rendering the world from the eye's reflection, shown on a quad on the glass.</summary>
    private sealed class Mirror
    {
        public required CarMirror Mount;
        public required SubViewport Port;
        public required Camera3D Camera;
        public required MeshInstance3D Face;
    }

    /// <summary>Pixels across a mirror's height: low, like everything else on screen (and cheap).</summary>
    private const int MirrorPixels = 48;
    /// <summary>What a mirror bothers to draw: nothing past this, m.</summary>
    private const float MirrorFar = 400f;
    /// <summary>What a mirror's camera sees: the world, without the held-item viewmodel or door portals' quads.</summary>
    private const uint MirrorCull = 0xFFFFFu & ~Items.HeldItemVisual.ViewmodelLayer & ~Interiors.DoorPortals.AllQuadLayers;

    private readonly Node3D _owner, _body;
    private readonly CarMirror[] _mounts;
    private readonly Vector3 _offset;
    private Mirror[]? _mirrors;
    private int _turn;

    /// <param name="owner">Takes the viewports as children (the rig).</param>
    /// <param name="body">Takes the mirror faces: the node the mounts are in the frame of, less <paramref name="offset"/>.</param>
    public CabMirrors(Node3D owner, Node3D body, CarMirror[] mounts, Vector3 offset)
    {
        _owner = owner;
        _body = body;
        _mounts = mounts;
        _offset = offset;
    }

    /// <summary>Once a frame: <paramref name="inCab"/> = the local driver is in the seat, <paramref name="on"/> = the setting.</summary>
    public void Update(bool inCab, bool on)
    {
        if (inCab) MeasureMirrors();
        bool wanted = (MirrorPerf ? _perfOn : on) && inCab && _mounts.Length > 0 && DisplayServer.GetName() != "headless";
        if (!wanted)
        {
            if (_mirrors != null)
                foreach (var m in _mirrors) { m.Face.Visible = false; m.Port.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled; }
            return;
        }
        _mirrors ??= BuildMirrors();
        var eye = _owner.GetViewport().GetCamera3D();
        if (eye == null) return;
        // one mirror re-rendered a frame, in turn: each at a third of the frame rate still moves
        // smoothly enough in a PS1 frame, for the cost of one small extra render
        _turn = (_turn + 1) % _mirrors.Length;
        for (int i = 0; i < _mirrors.Length; i++)
        {
            var m = _mirrors[i];
            m.Face.Visible = true;
            if (i != _turn) continue;
            Aim(m, eye.GlobalPosition);
            m.Port.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }
    }

    /// <summary>
    /// Puts a mirror's camera at the eye's reflection in its plane: what that camera sees through
    /// the glass, flipped left for right (the face's UVs), is what the mirror shows. It looks
    /// square through the mirror (along its normal) with an off-axis frustum framing exactly the
    /// glass, so its near plane IS the glass: nothing behind the mirror — its own housing, the dash,
    /// the door — is drawn. A camera aimed at the mirror's middle had its near plane square to the
    /// line of sight instead, which at a door mirror's slant let the housing into the picture as
    /// a black box.
    /// </summary>
    private static void Aim(Mirror m, Vector3 eye)
    {
        var face = m.Face.GlobalTransform;
        var centre = face.Origin;
        var normal = face.Basis.Z.Normalized();
        float depth = (eye - centre).Dot(normal);   // the eye's distance in front of the glass
        if (depth < 0.05f) return;   // behind or on the mirror: nothing to see in it
        var image = eye - 2f * depth * normal;
        // looking along the normal (−Z is the view), up the mirror's own up
        var z = -normal;
        var y = (face.Basis.Y - z * face.Basis.Y.Dot(z)).Normalized();
        var basis = new Basis(y.Cross(z), y, z);
        m.Camera.GlobalTransform = new Transform3D(basis, image);
        var local = basis.Transposed() * (centre - image);
        m.Camera.SetFrustum(m.Mount.Size.Y, new Vector2(local.X, local.Y), depth + 0.002f, MirrorFar);
    }

    /// <summary>
    /// <c>--mirrorperf</c> (with <c>--vsync off</c>): what the mirrors cost. They are switched on and
    /// off every 3 s, whatever the setting, and each span's mean frame time and draw calls logged,
    /// so the two halves see the same stretch of road.
    /// </summary>
    private static readonly bool MirrorPerf = OS.GetCmdlineUserArgs().Contains("--mirrorperf");
    private bool _perfOn = true;
    private double _perfWall, _perfDraws;
    private int _perfFrames;

    private void MeasureMirrors()
    {
        if (!MirrorPerf) return;
        _perfFrames++;
        _perfWall += _owner.GetProcessDeltaTime();
        _perfDraws += Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        if (_perfWall < 3.0) return;
        double n = _perfFrames;
        GD.Print($"[mirrorperf] mirrors {(_perfOn ? "on " : "off")}: {n / _perfWall:F0} fps, {_perfWall / n * 1000:F2} ms a frame, "
            + $"{_perfDraws / n:F0} draw calls");
        _perfWall = _perfDraws = 0;
        _perfFrames = 0;
        _perfOn = !_perfOn;
    }

    private Mirror[] BuildMirrors()
    {
        var mirrors = new Mirror[_mounts.Length];
        for (int i = 0; i < mirrors.Length; i++)
        {
            var mount = _mounts[i];
            var port = new SubViewport
            {
                Name = mount.Name + "View",
                Size = new Vector2I(Mathf.RoundToInt(MirrorPixels * mount.Size.X / mount.Size.Y), MirrorPixels),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,
            };
            _owner.AddChild(port);
            var camera = new Camera3D { Name = "Camera", CullMask = MirrorCull, Far = MirrorFar };
            port.AddChild(camera);
            camera.MakeCurrent();
            // the reflecting face faces along the mount's normal: a quad's front is +Z
            var z = mount.Normal.Normalized();
            var x = Vector3.Up.Cross(z).Normalized();
            var face = new MeshInstance3D
            {
                Name = mount.Name,
                Mesh = new QuadMesh { Size = mount.Size },
                Transform = new Transform3D(new Basis(x, z.Cross(x), z), mount.Centre + _offset),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoTexture = port.GetTexture(),
                    AlbedoColor = new Color(0.86f, 0.88f, 0.92f),
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                    // a mirror shows the world left for right
                    Uv1Scale = new Vector3(-1f, 1f, 1f),
                    Uv1Offset = new Vector3(1f, 0f, 0f),
                },
            };
            _body.AddChild(face);
            mirrors[i] = new Mirror { Mount = mount, Port = port, Camera = camera, Face = face };
        }
        return mirrors;
    }
}
