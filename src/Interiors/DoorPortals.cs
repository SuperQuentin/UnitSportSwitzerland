using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The view through an open door. Client only.
///
/// <para>
/// Each open door gets a quad in both of its doorways (<see cref="DoorLink"/>), and the one
/// nearest the camera, in view and on the camera's own side, gets the portal: a second camera,
/// moved through the door's map to where the viewer would stand on the other side, rendering at
/// half resolution into a <see cref="SubViewport"/> that the quad shows in screen space. Other
/// open doors show a dark hall. One render at most, and only while a door is open near someone.
/// </para>
///
/// <para>
/// Each quad is a short tunnel into the doorway, not a flat card: stepping through, the lens
/// passes the quad's plane and the near plane clips it, and the tunnel's walls behind still
/// show the other side. The quads live on their own visual layer, which the portal camera does
/// not draw (it would see the doorway it is looking through from behind).
/// </para>
///
/// <para>
/// Looking out from inside, the portal camera stands inside the building's closed shell. The
/// world shaders drop everything behind the doorway's plane for that camera only
/// (<c>portal_clip.gdshaderinc</c>): the shell's walls, the door leaf baked into the facade.
/// </para>
/// </summary>
public partial class DoorPortals : Node3D
{
    /// <summary>Visual layer 20: doorway quads, which the portal camera must not see.</summary>
    public const uint QuadLayer = 1u << 19;
    /// <summary>A doorway further than this from the camera gets no portal.</summary>
    private const float Range = 45f;
    /// <summary>Resolution of the portal picture relative to the screen. Low is on-style.</summary>
    private const float Scale = 0.5f;

    private readonly Func<IEnumerable<DoorLink>> _links;
    private readonly Func<Vector3, string?> _planAt;
    private SubViewport _view = null!;
    private Camera3D _camera = null!;
    private ShaderMaterial _live = null!, _dark = null!;
    private DoorLink? _active;
    private bool _clipping;

    public DoorPortals(Func<IEnumerable<DoorLink>> links, Func<Vector3, string?> planAt)
    {
        _links = links;
        _planAt = planAt;
    }

    /// <summary>The door being looked through, if any (for probes).</summary>
    public DoorLink? Active => _active;

    public override void _Ready()
    {
        // after the player has placed its camera for this frame
        ProcessPriority = 1000;
        _view = new SubViewport
        {
            Name = "PortalView",
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Size = new Vector2I(320, 240),
            HandleInputLocally = false,
        };
        AddChild(_view);
        _camera = new Camera3D { Name = "PortalCamera", CullMask = 0xFFFFF & ~QuadLayer };
        _view.AddChild(_camera);
        _camera.MakeCurrent();

        var shader = GD.Load<Shader>("res://shaders/door_portal.gdshader");
        _live = new ShaderMaterial { Shader = shader };
        _live.SetShaderParameter("view", _view.GetTexture());
        _live.SetShaderParameter("live", true);
        _dark = new ShaderMaterial { Shader = shader };
    }

    public override void _ExitTree() => SetClip(null);

    /// <summary>Gives a link its two doorway quads.</summary>
    public void Attach(DoorLink link)
    {
        link.OutsideQuad = Quad(link.Outside, link.OutsideWidth, link.OutsideHeight, DoorLink.OutsideQuadOffset, -0.8f);
        link.InsideQuad = Quad(link.Inside, link.InsideWidth, link.InsideHeight, -0.005f, 0.8f);
    }

    public void Detach(DoorLink link)
    {
        if (_active == link) _active = null;
        link.OutsideQuad?.QueueFree();
        link.InsideQuad?.QueueFree();
        link.OutsideQuad = link.InsideQuad = null;
    }

    /// <summary>
    /// A doorway-sized tunnel in a doorway frame: its mouth at <paramref name="front"/> on the
    /// viewer's side, running to <paramref name="back"/> on the far side.
    /// </summary>
    private MeshInstance3D Quad(Transform3D frame, float width, float height, float front, float back)
    {
        float hw = width / 2;
        var v = new List<Vector3>();
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { v.Add(a); v.Add(b); v.Add(c); v.Add(a); v.Add(c); v.Add(d); }
        Vector3 P(float x, float y, float z) => new(x, y, z);
        Face(P(-hw, 0, front), P(hw, 0, front), P(hw, height, front), P(-hw, height, front)); // mouth
        Face(P(-hw, 0, back), P(hw, 0, back), P(hw, height, back), P(-hw, height, back));     // end
        Face(P(-hw, 0, front), P(-hw, 0, back), P(-hw, height, back), P(-hw, height, front));  // sides
        Face(P(hw, 0, front), P(hw, 0, back), P(hw, height, back), P(hw, height, front));
        Face(P(-hw, height, front), P(hw, height, front), P(hw, height, back), P(-hw, height, back));
        Face(P(-hw, 0, front), P(hw, 0, front), P(hw, 0, back), P(-hw, 0, back));

        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var quad = new MeshInstance3D
        {
            Name = "Doorway",
            Mesh = mesh,
            Layers = QuadLayer,
            MaterialOverride = _dark,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
            Visible = false,
        };
        AddChild(quad);
        quad.GlobalTransform = frame;
        return quad;
    }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        DoorLink? best = null;
        bool fromInside = false;
        float bestD = Range;
        if (cam != null)
        {
            var eye = cam.GlobalPosition;
            fromInside = eye.Y < InteriorManager.InteriorBaseY + 1000f;
            ClipFar(cam, fromInside);
            string? plan = fromInside ? _planAt(eye) : null;
            foreach (var l in _links())
            {
                bool shown = l.Swing > 0.001f;
                if (l.OutsideQuad != null) l.OutsideQuad.Visible = shown;
                if (l.InsideQuad != null) l.InsideQuad.Visible = shown;
                if (!shown || (fromInside && l.Plan != plan)) continue;
                var frame = fromInside ? l.Inside : l.Outside;
                var local = frame.AffineInverse() * eye;
                // on the doorway's own side (a little slack for a lens right in the doorway)
                if (fromInside ? local.Z > 0.3f : local.Z < -0.3f) continue;
                float d = local.Length();
                if (d >= bestD || !InView(cam, frame, fromInside ? l.InsideWidth : l.OutsideWidth,
                        fromInside ? l.InsideHeight : l.OutsideHeight, d)) continue;
                bestD = d;
                best = l;
            }
        }

        if (best != _active)
        {
            foreach (var l in _links())
            {
                if (l.OutsideQuad != null) l.OutsideQuad.MaterialOverride = l == best ? _live : _dark;
                if (l.InsideQuad != null) l.InsideQuad.MaterialOverride = l == best ? _live : _dark;
            }
            _active = best;
            _view.RenderTargetUpdateMode = best != null ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        }
        if (best == null || cam == null)
        {
            SetClip(null);
            return;
        }

        var map = fromInside ? best.ToOutside : best.ToInside;
        _camera.GlobalTransform = map * cam.GlobalTransform;
        _camera.Projection = cam.Projection;
        _camera.Fov = cam.Fov;
        _camera.Near = cam.Near;
        _camera.Far = _shortened == cam ? _far : cam.Far;
        _camera.KeepAspect = cam.KeepAspect;
        _camera.HOffset = cam.HOffset;
        _camera.VOffset = cam.VOffset;
        var size = GetViewport().GetVisibleRect().Size * Scale;
        var want = new Vector2I(Math.Max(16, (int)size.X), Math.Max(16, (int)size.Y));
        if (_view.Size != want) _view.Size = want;

        // looking out, the camera stands inside the facade's closed shell: cut it away
        SetClip(fromInside ? best : null);
    }

    /// <summary>
    /// A lens inside an interior draws out to <see cref="InsideFar"/> only. The outside world stays
    /// visible while a door is open, for the portal camera, and node visibility is the same for
    /// every viewport: without this the main camera drew the whole landscape 3 km overhead, behind
    /// the ceiling. The portal camera keeps the real far distance.
    /// </summary>
    private void ClipFar(Camera3D cam, bool inside)
    {
        if (_shortened != null && (_shortened != cam || !inside))
        {
            if (IsInstanceValid(_shortened) && Mathf.IsEqualApprox(_shortened.Far, InsideFar)) _shortened.Far = _far;
            _shortened = null;
        }
        // a settings change sets the far distance again while inside: take the new one
        if (inside && !Mathf.IsEqualApprox(cam.Far, InsideFar))
        {
            _far = cam.Far;
            _shortened = cam;
            cam.Far = InsideFar;
        }
    }

    private const float InsideFar = 400f;
    private Camera3D? _shortened;
    private float _far;

    private static bool InView(Camera3D cam, Transform3D frame, float width, float height, float distance)
    {
        if (distance < 2f) return true;
        float hw = width / 2;
        foreach (var p in new[] { new Vector3(0, height / 2, 0), new Vector3(-hw, 0, 0), new Vector3(hw, 0, 0),
                     new Vector3(-hw, height, 0), new Vector3(hw, height, 0) })
            if (cam.IsPositionInFrustum(frame * p)) return true;
        return false;
    }

    private void SetClip(DoorLink? link)
    {
        if (link == null)
        {
            if (!_clipping) return;
            _clipping = false;
            RenderingServer.GlobalShaderParameterSet("portal_clip_eye", new Vector3(0, 1e9f, 0));
            return;
        }
        _clipping = true;
        // behind the portal quad: the facade, and the leaf baked on it
        var n = link.Outside.Basis.Z;
        float w = n.Dot(link.Outside.Origin) + DoorLink.OutsideQuadOffset;
        RenderingServer.GlobalShaderParameterSet("portal_clip_eye", _camera.GlobalPosition);
        RenderingServer.GlobalShaderParameterSet("portal_clip_plane", new Vector4(n.X, n.Y, n.Z, w));
    }
}
