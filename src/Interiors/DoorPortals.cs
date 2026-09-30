using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The views through open doors. Client only.
///
/// <para>
/// Each open door has a quad in both of its doorways (<see cref="DoorLink"/>). The doorways the
/// camera sees get portals: a second camera, moved through the door's map to where the viewer
/// would stand on the other side, rendering at reduced resolution into a <see cref="SubViewport"/>
/// that the quad shows in screen space. Portals nest: a portal camera sees doorways too, and one
/// of them gets a portal of its own, so a building with two doors open is seen through, from the
/// street back out onto the street. Budget: <see cref="Width"/> doorways seen directly, each with
/// one doorway seen through it. Beyond that a doorway shows a dark hall.
/// </para>
///
/// <para>
/// <b>Depth.</b> A quad has one picture, but the same doorway can be seen by cameras at different
/// depths. So every doorway has one quad per depth, each on its own visual layer, and a camera
/// draws only its own depth's quads: the view from the screen (depth 0), from a portal (1), from
/// a portal in a portal (2, always dark). A view's cameras draw after the views nested in them,
/// which is what makes their quads show this frame's picture and not the last one: Godot renders
/// a <see cref="SubViewport"/> before the viewport it is a child of.
/// </para>
///
/// <para>
/// Each quad is a short tunnel into the doorway, not a flat card: stepping through, the lens
/// passes the quad's plane and the near plane clips it, and the tunnel's walls behind still show
/// the other side. Seen from well behind, a quad draws nothing (<c>door_portal.gdshader</c>): that
/// is a portal camera looking out through that very doorway.
/// </para>
///
/// <para>
/// Looking out from inside, a portal camera stands inside the building's closed shell. The world
/// shaders drop everything behind that doorway's plane for that camera only
/// (<c>portal_clip.gdshaderinc</c>, one slot per camera).
/// </para>
/// </summary>
public partial class DoorPortals : Node3D
{
    /// <summary>Visual layers 18, 19, 20: doorway quads for depth 0, 1 and 2.</summary>
    public static readonly uint[] QuadLayers = { 1u << 17, 1u << 18, 1u << 19 };
    public const uint AllQuadLayers = (1u << 17) | (1u << 18) | (1u << 19);
    private const uint WorldLayers = 0xFFFFFu & ~AllQuadLayers;

    /// <summary>Doorways seen directly that get a portal each.</summary>
    private const int Width = 2;
    /// <summary>A doorway further than this from the camera looking at it gets no portal.</summary>
    private const float Range = 45f;
    /// <summary>Resolution of a portal picture relative to the screen, per depth. Low is on-style.</summary>
    private static readonly float[] Scale = { 0.5f, 0.35f };

    private readonly Func<IEnumerable<DoorLink>> _links;
    private readonly Func<Vector3, string?> _planAt;
    private readonly View[] _views = new View[Width];
    private ShaderMaterial _darkMaterial = null!;
    private Shader _shader = null!;
    private readonly HashSet<DoorLink> _shown = new();

    /// <summary>One portal camera and the viewport it renders into.</summary>
    private sealed class View
    {
        public required SubViewport Port { get; init; }
        public required Camera3D Camera { get; init; }
        public required int Depth { get; init; }
        public required int Slot { get; init; }
        public View? Nested { get; init; }
        public DoorLink? Link { get; set; }
    }

    public DoorPortals(Func<IEnumerable<DoorLink>> links, Func<Vector3, string?> planAt)
    {
        _links = links;
        _planAt = planAt;
    }

    /// <summary>Whether a door's doorway is shown with a live picture this frame (for probes).</summary>
    public bool IsShown(DoorLink link) => _shown.Contains(link);

    /// <summary>Whether a door is seen through another door this frame (for probes).</summary>
    public bool IsShownThrough(DoorLink link) => _views.Any(v => v.Nested?.Link == link);

    public override void _Ready()
    {
        // after the player has placed its camera for this frame
        ProcessPriority = 1000;
        _shader = GD.Load<Shader>("res://shaders/door_portal.gdshader");
        _darkMaterial = new ShaderMaterial { Shader = _shader };
        for (int i = 0; i < Width; i++)
        {
            // the nested view is a child: it renders first, and the outer view's quad shows it
            var outer = Port($"Portal{i}");
            AddChild(outer);
            var inner = Port($"Portal{i}Through");
            outer.AddChild(inner);
            var nested = new View { Port = inner, Camera = Lens(inner, 2), Depth = 2, Slot = Width + i };
            _views[i] = new View { Port = outer, Camera = Lens(outer, 1), Depth = 1, Slot = i, Nested = nested };
        }
    }

    private static SubViewport Port(string name) => new()
    {
        Name = name,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        Size = new Vector2I(320, 240),
        HandleInputLocally = false,
    };

    /// <summary>A portal camera for a depth: the world, and that depth's doorway quads.</summary>
    private static Camera3D Lens(SubViewport port, int depth)
    {
        var cam = new Camera3D { Name = "Camera", CullMask = WorldLayers | QuadLayers[depth] };
        port.AddChild(cam);
        cam.MakeCurrent();
        return cam;
    }

    public override void _ExitTree()
    {
        for (int slot = 0; slot < 2 * Width; slot++) SetClip(slot, null, null, false);
    }

    /// <summary>Gives a link its doorway quads, one per depth on each side.</summary>
    public void Attach(DoorLink link)
    {
        var outsideMesh = Tunnel(link.OutsideWidth, link.OutsideHeight, DoorLink.OutsideQuadOffset, -0.8f);
        var insideMesh = Tunnel(link.InsideWidth, link.InsideHeight, -0.005f, 0.8f);
        for (int d = 0; d < QuadLayers.Length; d++)
        {
            link.OutsideQuads[d] = Quad(outsideMesh, link.Outside, d, facing: link.Outside.Basis.Z);
            link.InsideQuads[d] = Quad(insideMesh, link.Inside, d, facing: -link.Inside.Basis.Z);
        }
    }

    public void Detach(DoorLink link)
    {
        foreach (var v in _views)
        {
            if (v.Link == link) v.Link = null;
            if (v.Nested?.Link == link) v.Nested.Link = null;
        }
        for (int d = 0; d < QuadLayers.Length; d++)
        {
            link.OutsideQuads[d]?.QueueFree();
            link.InsideQuads[d]?.QueueFree();
            link.OutsideQuads[d] = link.InsideQuads[d] = null;
        }
    }

    /// <summary>
    /// A doorway-sized tunnel in a doorway frame: its mouth at <paramref name="front"/> on the
    /// viewer's side, running to <paramref name="back"/> on the far side.
    /// </summary>
    private static ArrayMesh Tunnel(float width, float height, float front, float back)
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
        return mesh;
    }

    private MeshInstance3D Quad(ArrayMesh mesh, Transform3D frame, int depth, Vector3 facing)
    {
        // its own material: which picture it shows is decided per quad, every frame
        var material = new ShaderMaterial { Shader = _shader };
        material.SetShaderParameter("facing", new Vector4(facing.X, facing.Y, facing.Z, facing.Dot(frame.Origin)));
        var quad = new MeshInstance3D
        {
            Name = "Doorway",
            Mesh = mesh,
            Layers = QuadLayers[depth],
            MaterialOverride = material,
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
        var links = _links().ToList();
        foreach (var l in links)
            for (int d = 0; d < QuadLayers.Length; d++)
            {
                bool shown = l.Swing > 0.001f;
                if (l.OutsideQuads[d] is { } o) { o.Visible = shown; Show(o, null); }
                if (l.InsideQuads[d] is { } i) { i.Visible = shown; Show(i, null); }
            }
        _shown.Clear();

        var cam = GetViewport().GetCamera3D();
        if (cam != null)
        {
            // the screen's camera draws depth-0 quads only
            if ((cam.CullMask & AllQuadLayers) != QuadLayers[0])
                cam.CullMask = (cam.CullMask & ~AllQuadLayers) | QuadLayers[0];
            ClipFar(cam, cam.GlobalPosition.Y < InteriorManager.InteriorBaseY + 1000f);
        }
        float far = cam == null ? 4000f : _shortened == cam ? _far : cam.Far;

        var direct = cam != null ? Seen(cam, cam.GlobalTransform, links, null, Width) : new List<(DoorLink, bool)>();
        var screen = GetViewport().GetVisibleRect().Size;
        for (int i = 0; i < Width; i++)
        {
            var view = _views[i];
            if (i < direct.Count && cam != null)
            {
                var (link, fromInside) = direct[i];
                var lens = (fromInside ? link.ToOutside : link.ToInside) * cam.GlobalTransform;
                Aim(view, link, fromInside, lens, cam, far, screen);
                // a camera sent in through a door is in that door's building, whatever is nearest
                var through = Seen(view.Camera, lens, links, link, 1, fromInside ? null : link.Plan);
                if (through.Count > 0)
                {
                    var (next, nextInside) = through[0];
                    Aim(view.Nested!, next, nextInside, (nextInside ? next.ToOutside : next.ToInside) * lens, cam, far, screen);
                }
                else Idle(view.Nested!);
            }
            else
            {
                Idle(view);
                Idle(view.Nested!);
            }
        }
    }

    /// <summary>
    /// The open doorways a camera at <paramref name="lens"/> sees on its own side, nearest first:
    /// the door, and whether the camera is inside its building.
    /// </summary>
    private List<(DoorLink Link, bool Inside)> Seen(Camera3D cam, Transform3D lens, List<DoorLink> links, DoorLink? through, int max,
        string? inPlan = null)
    {
        var eye = lens.Origin;
        bool inside = eye.Y < InteriorManager.InteriorBaseY + 1000f;
        string? plan = inside ? inPlan ?? _planAt(eye) : null;
        var found = new List<(float D, DoorLink Link)>();
        foreach (var l in links)
        {
            if (l == through || l.Swing <= 0.001f || (inside && l.Plan != plan)) continue;
            var frame = inside ? l.Inside : l.Outside;
            var local = frame.AffineInverse() * eye;
            // on the doorway's own side (a little slack for a lens right in the doorway)
            if (inside ? local.Z > 0.3f : local.Z < -0.3f) continue;
            float d = local.Length();
            if (d >= Range || !InView(cam, lens, frame, inside ? l.InsideWidth : l.OutsideWidth,
                    inside ? l.InsideHeight : l.OutsideHeight, d)) continue;
            found.Add((d, l));
        }
        return found.OrderBy(f => f.D).Take(max).Select(f => (f.Link, inside)).ToList();
    }

    /// <summary>Points a view through a doorway, and shows its picture in that doorway's quad of the depth above.</summary>
    private void Aim(View view, DoorLink link, bool fromInside, Transform3D lens, Camera3D screenCam, float far, Vector2 screen)
    {
        view.Link = link;
        var c = view.Camera;
        // nudged along its view: through two doors in line, the map can put a portal camera
        // exactly on the screen's, and the clip tells cameras apart by position (portal_clip)
        c.GlobalTransform = lens.Translated(-lens.Basis.Z.Normalized() * 0.05f * (view.Slot + 1));
        c.Projection = screenCam.Projection;
        c.Fov = screenCam.Fov;
        c.Near = screenCam.Near;
        c.Far = far;
        c.KeepAspect = screenCam.KeepAspect;
        c.HOffset = screenCam.HOffset;
        c.VOffset = screenCam.VOffset;
        var size = screen * Scale[view.Depth - 1];
        var want = new Vector2I(Math.Max(16, (int)size.X), Math.Max(16, (int)size.Y));
        if (view.Port.Size != want) view.Port.Size = want;
        view.Port.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

        var quad = (fromInside ? link.InsideQuads : link.OutsideQuads)[view.Depth - 1];
        if (quad != null) Show(quad, view.Port.GetTexture());
        if (view.Depth == 1) _shown.Add(link);
        // whatever is on the camera's side of the doorway it looks through is not in its picture:
        // the facade's closed shell looking out, other interiors in the shared space looking in
        SetClip(view.Slot, link, c, outward: fromInside);
    }

    private void Idle(View view)
    {
        view.Link = null;
        view.Port.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        SetClip(view.Slot, null, null, false);
    }

    private static void Show(MeshInstance3D quad, Texture2D? picture)
    {
        if (quad.MaterialOverride is not ShaderMaterial m) return;
        m.SetShaderParameter("live", picture != null);
        if (picture != null) m.SetShaderParameter("view", picture);
    }

    /// <summary>
    /// A lens inside an interior draws out to <see cref="InsideFar"/> only. The outside world stays
    /// visible while a door is open, for the portal cameras, and node visibility is the same for
    /// every viewport: without this the main camera drew the whole landscape 3 km overhead, behind
    /// the ceiling. The portal cameras keep the real far distance.
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

    /// <summary>
    /// Whether a doorway is in a camera's view. <paramref name="lens"/> is where the camera is
    /// about to be, which for a nested view is not where it was last frame.
    /// </summary>
    private static bool InView(Camera3D cam, Transform3D lens, Transform3D frame, float width, float height, float distance)
    {
        if (distance < 2f) return true;
        var saved = cam.GlobalTransform;
        cam.GlobalTransform = lens;
        float hw = width / 2;
        bool seen = false;
        foreach (var p in new[] { new Vector3(0, height / 2, 0), new Vector3(-hw, 0, 0), new Vector3(hw, 0, 0),
                     new Vector3(-hw, height, 0), new Vector3(hw, height, 0) })
            if (cam.IsPositionInFrustum(frame * p)) { seen = true; break; }
        cam.GlobalTransform = saved;
        return seen;
    }

    private readonly bool[] _clipping = new bool[4];

    /// <summary>
    /// A portal camera's clip: the doorway it looks through, keeping what is beyond it. Looking
    /// out, the plane is the facade, out past the leaf baked on it; looking in, the interior's
    /// doorway, keeping the room (and its reveal).
    /// </summary>
    private void SetClip(int slot, DoorLink? link, Camera3D? cam, bool outward)
    {
        if (link == null || cam == null)
        {
            if (!_clipping[slot]) return;
            _clipping[slot] = false;
            RenderingServer.GlobalShaderParameterSet($"portal_clip_eye_{slot}", new Vector3(0, 1e9f, 0));
            return;
        }
        _clipping[slot] = true;
        Vector3 n;
        float w;
        if (outward)
        {
            n = link.Outside.Basis.Z;
            w = n.Dot(link.Outside.Origin) + DoorLink.OutsideQuadOffset;
        }
        else
        {
            n = -link.Inside.Basis.Z;
            w = n.Dot(link.Inside.Origin) - 0.02f;
        }
        RenderingServer.GlobalShaderParameterSet($"portal_clip_eye_{slot}", cam.GlobalPosition);
        RenderingServer.GlobalShaderParameterSet($"portal_clip_plane_{slot}", new Vector4(n.X, n.Y, n.Z, w));
    }
}
