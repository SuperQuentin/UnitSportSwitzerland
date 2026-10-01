using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The views through open doors. Client only.
///
/// <para>
/// Each open door has a quad in both of its doorways (<see cref="DoorLink"/>). The doorways the
/// camera sees get portals: a second camera, moved through the door's map to where the viewer
/// would stand on the other side, rendering at the screen's resolution into a <see cref="SubViewport"/>
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
/// The screen's camera never stands in a doorway itself: there its near plane cuts the quad, and
/// the view goes black or shows the wall behind. For the frame being drawn, its near plane is
/// shortened and it is moved to the nearer side (<see cref="KeepOutOfDoorways"/>), then both are
/// put back once the frame is drawn, since whatever placed it keeps placing it relative to itself.
/// </para>
///
/// <para>
/// Looking out from inside, a portal camera stands inside the building's closed shell. The world
/// shaders drop everything behind that doorway's plane for that camera only
/// (<c>portal_clip.gdshaderinc</c>, one slot per camera).
/// </para>
///
/// <para>
/// <b>In a headset</b> (#244) one picture cannot line up with two eyes. The same four cameras are
/// then door × eye instead of door × depth: each doorway seen gets a picture per eye, from where
/// that eye is mapped through the door, with that eye's own asymmetric frustum, and the quad picks
/// its eye's picture by <c>VIEW_INDEX</c>. No doorway is seen through another one in VR. The
/// headset's camera cannot be moved out of a doorway (the tracking places it): it only gets the
/// short near plane there, and the short far distance indoors.
/// </para>
/// </summary>
public partial class DoorPortals : Node3D, Core.IOriginContainer
{
    /// <summary>Visual layers 18, 19, 20: doorway quads for depth 0, 1 and 2.</summary>
    public static readonly uint[] QuadLayers = { 1u << 17, 1u << 18, 1u << 19 };
    public const uint AllQuadLayers = (1u << 17) | (1u << 18) | (1u << 19);
    /// <summary>What portal cameras draw: not the first-person viewmodel, already drawn by the screen's camera.</summary>
    private const uint WorldLayers = 0xFFFFFu & ~AllQuadLayers & ~Items.HeldItemVisual.ViewmodelLayer;

    /// <summary>Doorways seen directly that get a portal each.</summary>
    private const int Width = 2;
    /// <summary>A doorway further than this from the camera looking at it gets no portal.</summary>
    private const float Range = 45f;
    /// <summary>Resolution of a portal picture relative to the screen, per depth.</summary>
    private static readonly float[] Scale = { 1f, 1f };

    private readonly Func<IEnumerable<DoorLink>> _links;
    private readonly Func<Vector3, string?> _planAt;
    private readonly View[] _views = new View[Width];
    private ShaderMaterial _darkMaterial = null!;
    private Shader _shader = null!;
    private readonly HashSet<DoorLink> _shown = new();
    /// <summary>The screen's camera changed near a doorway for this frame, and how it was.</summary>
    private (Camera3D Camera, Transform3D Local, float Near)? _restore;
    /// <summary>Within this of an open doorway, the screen's camera gets <see cref="DoorwayNear"/>.</summary>
    private const float NearZone = 1f;
    /// <summary>
    /// The near plane by a doorway: small enough that the jump across it is ~5 cm, not ~45 cm as
    /// with the usual 8 cm. The reversed depth buffer keeps the far distance sharp.
    /// </summary>
    private const float DoorwayNear = 0.005f;

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

    /// <summary>
    /// Takes the doors whose portal shows (see <c>open_door_box</c> in <c>ps1_building.gdshader</c>),
    /// nearest the screen's camera first, at most <see cref="MaxOpenDoors"/>: their baked leaf and
    /// handle are not drawn. Called when the list changes.
    /// </summary>
    public Action<Vector4[], Vector4[], int>? OpenDoors { get; set; }
    public const int MaxOpenDoors = 16;
    private string _openKey = "";

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
        // after the player has placed its camera for this frame, and the XR rig its headset (1000)
        ProcessPriority = 1001;
        RenderingServer.FramePostDraw += PutCameraBack;
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
        OpenDoors?.Invoke(new Vector4[MaxOpenDoors], new Vector4[MaxOpenDoors], 0);
        RenderingServer.FramePostDraw -= PutCameraBack;
        PutCameraBack();
        for (int slot = 0; slot < 2 * Width; slot++) SetClip(slot, null, null, false);
    }

    /// <summary>Gives a link its doorway quads, one per depth on each side.</summary>
    public void Attach(DoorLink link)
    {
        var outsideMesh = Tunnel(link.OutsideWidth, link.OutsideHeight, DoorLink.OutsideQuadOffset, -0.8f);
        var insideMesh = Tunnel(link.InsideWidth, link.InsideHeight, DoorLink.InsideQuadOffset, 0.8f);
        for (int d = 0; d < QuadLayers.Length; d++)
        {
            link.OutsideQuads[d] = Quad(outsideMesh, link.Outside, d, facing: link.Outside.Basis.Z, DoorLink.OutsideQuadOffset);
            link.InsideQuads[d] = Quad(insideMesh, link.Inside, d, facing: -link.Inside.Basis.Z, DoorLink.InsideQuadOffset);
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

    private MeshInstance3D Quad(ArrayMesh mesh, Transform3D frame, int depth, Vector3 facing, float mouth)
    {
        // its own material: which picture it shows is decided per quad, every frame
        var material = new ShaderMaterial { Shader = _shader };
        // seen from behind its mouth, not the doorway's plane: between the two, a camera is in the tunnel
        var plane = facing.Dot(frame * new Vector3(0, 0, mouth));
        material.SetShaderParameter("facing", new Vector4(facing.X, facing.Y, facing.Z, plane));
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
        SendOpenDoors(links, cam);
        if (cam != null)
        {
            // first: which side it ends up on decides everything below
            KeepOutOfDoorways(cam, links);
            // the screen's camera draws depth-0 quads only
            if ((cam.CullMask & AllQuadLayers) != QuadLayers[0])
                cam.CullMask = (cam.CullMask & ~AllQuadLayers) | QuadLayers[0];
            ClipFar(cam, cam.GlobalPosition.Y < InteriorManager.InteriorBaseY + 1000f);
        }
        var head = XR.XrSession.Rig?.Head;
        if (head != null && IsInstanceValid(head))
        {
            FitHead(head, links);
            if (StereoInterface() is { } xr)
            {
                Stereo(links, head, xr);
                return;
            }
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
    /// Near an open doorway (<see cref="NearZone"/>), the screen's camera gets a near plane of
    /// <see cref="DoorwayNear"/>, so the slab it must stay out of is barely wider than the gap
    /// between the two quads. Standing in that slab (<see cref="DoorLink.LensSlabMin"/> ..
    /// <see cref="DoorLink.LensSlabMax"/>, and the opening widened as much), it is moved to whichever
    /// side is nearer: back out on its own side, or carried through by the door's map. Through only
    /// within the opening, never through the wall beside or above it.
    /// </summary>
    private void KeepOutOfDoorways(Camera3D cam, List<DoorLink> links)
    {
        var eye = cam.GlobalTransform;
        bool inside = eye.Origin.Y < InteriorManager.InteriorBaseY + 1000f;
        foreach (var l in links)
        {
            if (l.Swing <= 0.001f) continue;
            var frame = inside ? l.Inside : l.Outside;
            var local = frame.AffineInverse() * eye.Origin;
            float hw = (inside ? l.InsideWidth : l.OutsideWidth) / 2;
            float top = inside ? l.InsideHeight : l.OutsideHeight;
            if (!Around(local, hw, top, DoorLink.LensSlabMin(NearZone), DoorLink.LensSlabMax(NearZone), NearZone)) continue;

            _restore ??= (cam, cam.Transform, cam.Near);
            cam.Near = Mathf.Min(cam.Near, DoorwayNear);
            float margin = NearReach(cam) * 1.2f;
            float min = DoorLink.LensSlabMin(margin), max = DoorLink.LensSlabMax(margin);
            if (!Around(local, hw, top, min, max, margin)) return;

            // Z is shared by both frames (the map keeps it): the room below 0, the street above
            bool inOpening = Mathf.Abs(local.X) < l.HalfPass && local.Y > 0 && local.Y < l.PassHeight;
            bool towardStreet = local.Z > (min + max) / 2;
            if (!inOpening) towardStreet = !inside;
            var moved = eye with { Origin = frame * new Vector3(local.X, local.Y, towardStreet ? max : min) };
            if (towardStreet == inside) moved = (inside ? l.ToOutside : l.ToInside) * moved;
            cam.GlobalTransform = moved;
            return;
        }
    }

    /// <summary>
    /// The doors whose quads show, for the building shader to drop their baked leaf: the same
    /// test as the quads' own (a swing started), so the leaf and the quad change over together.
    /// </summary>
    private void SendOpenDoors(List<DoorLink> links, Camera3D? cam)
    {
        if (OpenDoors == null) return;
        var eye = cam?.GlobalPosition ?? Vector3.Zero;
        var open = links.Where(l => l.Swing > 0.001f)
            .OrderBy(l => l.Outside.Origin.DistanceSquaredTo(eye))
            .Take(MaxOpenDoors).ToList();
        string key = string.Join(";", open.Select(l => l.Door).Order());
        if (key == _openKey) return;
        _openKey = key;
        var boxes = new Vector4[MaxOpenDoors];
        var axes = new Vector4[MaxOpenDoors];
        for (int i = 0; i < open.Count; i++)
        {
            var o = open[i].Outside;
            boxes[i] = new Vector4(o.Origin.X, o.Origin.Y, o.Origin.Z, open[i].OutsideWidth / 2);
            axes[i] = new Vector4(o.Basis.Z.X, o.Basis.Z.Z, open[i].OutsideHeight, 0);
        }
        OpenDoors(boxes, axes, open.Count);
    }

    /// <summary>Whether a point in a doorway frame is within the slab, and the opening widened by <paramref name="margin"/>.</summary>
    private static bool Around(Vector3 local, float halfWidth, float top, float min, float max, float margin) =>
        local.Z > min && local.Z < max && Mathf.Abs(local.X) < halfWidth + margin
        && local.Y > -margin && local.Y < top + margin;

    /// <summary>How far the near plane's corners reach from the lens.</summary>
    private static float NearReach(Camera3D cam)
    {
        var size = cam.GetViewport().GetVisibleRect().Size;
        float aspect = size.Y > 0 ? size.X / size.Y : 16f / 9f;
        float t = Mathf.Tan(Mathf.DegToRad(cam.Fov) / 2);
        // Fov is along the kept axis: the other one is wider or narrower by the aspect
        float across = cam.KeepAspect == Camera3D.KeepAspectEnum.Height ? aspect : 1 / aspect;
        return cam.Near * Mathf.Sqrt(1 + t * t * (1 + across * across));
    }

    /// <summary>Once the frame is drawn, the screen's camera goes back where whatever placed it put it.</summary>
    private void PutCameraBack()
    {
        if (_restore is not { } r) return;
        _restore = null;
        if (!IsInstanceValid(r.Camera)) return;
        r.Camera.Transform = r.Local;
        r.Camera.Near = r.Near;
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

    /// <summary>A portal picture per eye, relative to the headset's own render size per eye.</summary>
    private const float StereoScale = 0.5f;

    /// <summary>The headset's interface when it draws two eyes (not <c>--xrsim</c>, which draws none).</summary>
    private static XRInterface? StereoInterface()
    {
        if (XR.XrSession.Simulated) return null;
        var xr = XRServer.PrimaryInterface;
        return xr != null && xr.IsInitialized() && xr.GetViewCount() == 2 ? xr : null;
    }

    /// <summary>
    /// The headset's camera by the doorways: depth-0 quads only, as the screen's camera, the short
    /// near plane within <see cref="NearZone"/> of an open doorway, the short far distance indoors.
    /// </summary>
    private static void FitHead(Camera3D head, List<DoorLink> links)
    {
        if ((head.CullMask & AllQuadLayers) != QuadLayers[0])
            head.CullMask = (head.CullMask & ~AllQuadLayers) | QuadLayers[0];
        var eye = head.GlobalPosition;
        bool inside = eye.Y < InteriorManager.InteriorBaseY + 1000f;
        bool byDoorway = links.Any(l =>
        {
            if (l.Swing <= 0.001f) return false;
            var local = (inside ? l.Inside : l.Outside).AffineInverse() * eye;
            return Around(local, (inside ? l.InsideWidth : l.OutsideWidth) / 2, inside ? l.InsideHeight : l.OutsideHeight,
                DoorLink.LensSlabMin(NearZone), DoorLink.LensSlabMax(NearZone), NearZone);
        });
        head.Near = byDoorway ? DoorwayNear : XR.XrRig.HeadNear;
        float far = inside ? InsideFar : Core.GameSettings.Current.CameraFar;
        if (!Mathf.IsEqualApprox(head.Far, far)) head.Far = far;
    }

    /// <summary>
    /// The doorways the headset sees, each with a picture per eye: <c>_views[i]</c> for the left
    /// eye, its <c>Nested</c> for the right one.
    /// </summary>
    private void Stereo(List<DoorLink> links, XRCamera3D head, XRInterface xr)
    {
        if (head.GetParent() is not Node3D origin) return;
        var direct = Seen(head, head.GlobalTransform, links, null, Width);
        var target = xr.GetRenderTargetSize() * StereoScale;
        float aspect = target.Y > 0 ? target.X / target.Y : 1f;
        float far = Core.GameSettings.Current.CameraFar;
        for (int i = 0; i < Width; i++)
        {
            var left = _views[i];
            var right = left.Nested!;
            if (i >= direct.Count)
            {
                Idle(left);
                Idle(right);
                continue;
            }
            var (link, fromInside) = direct[i];
            var map = fromInside ? link.ToOutside : link.ToInside;
            foreach (var (view, e) in new[] { (left, 0u), (right, 1u) })
                AimEye(view, link, fromInside, map * xr.GetTransformForView(e, origin.GlobalTransform),
                    xr.GetProjectionForView(e, aspect, head.Near, far), head.Near, far, target.Y);
            var quad = (fromInside ? link.InsideQuads : link.OutsideQuads)[0];
            if (quad != null) Show(quad, left.Port.GetTexture(), right.Port.GetTexture());
            _shown.Add(link);
        }
    }

    /// <summary>
    /// Points one eye's portal camera through a doorway, with that eye's frustum (the projection
    /// as a frustum at the near plane, as <see cref="XR.XrMonitor"/> draws the eyes), so the quad's
    /// screen-space lookup lines up in that eye.
    /// </summary>
    private void AimEye(View view, DoorLink link, bool fromInside, Transform3D lens, Projection p, float near, float far, float height)
    {
        view.Link = link;
        var c = view.Camera;
        // nudged back as in Aim: the clip tells the cameras apart by position
        c.GlobalTransform = lens.Translated(lens.Basis.Z.Normalized() * 0.05f * (view.Slot + 1));
        c.Environment = World.DayNight.EnvironmentAt(c.GlobalPosition);
        float h = 2f * near / p.Y.Y, w = 2f * near / p.X.X;
        c.KeepAspect = Camera3D.KeepAspectEnum.Height;
        c.HOffset = c.VOffset = 0f;
        c.SetFrustum(h, new Vector2(p.Z.X * near / p.X.X, p.Z.Y * near / p.Y.Y), near, far);
        // the picture has the frustum's own aspect, or the frustum is stretched to fit it
        int ph = Math.Max(16, (int)height);
        var want = new Vector2I(Math.Max(16, (int)Mathf.Round(ph * w / h)), ph);
        if (view.Port.Size != want) view.Port.Size = want;
        view.Port.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        SetClip(view.Slot, link, c, outward: fromInside);
    }

    /// <summary>Points a view through a doorway, and shows its picture in that doorway's quad of the depth above.</summary>
    private void Aim(View view, DoorLink link, bool fromInside, Transform3D lens, Camera3D screenCam, float far, Vector2 screen)
    {
        view.Link = link;
        var c = view.Camera;
        // nudged back along its view: through two doors in line, the map can put a portal camera
        // exactly on the screen's, and the clip tells cameras apart by position (portal_clip).
        // Back, not forward: a lens by a doorway would put its portal camera through the far one,
        // in that doorway's tunnel, and its picture would be the tunnel's dark hall.
        c.GlobalTransform = lens.Translated(lens.Basis.Z.Normalized() * 0.05f * (view.Slot + 1));
        // lit like the space it stands in: a view into a room shows its people lit by the room
        c.Environment = World.DayNight.EnvironmentAt(c.GlobalPosition);
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

    /// <summary>A quad's picture, or none (a dark hall); with <paramref name="right"/>, one per eye.</summary>
    private static void Show(MeshInstance3D quad, Texture2D? picture, Texture2D? right = null)
    {
        if (quad.MaterialOverride is not ShaderMaterial m) return;
        m.SetShaderParameter("live", picture != null);
        if (picture != null) m.SetShaderParameter("view", picture);
        m.SetShaderParameter("stereo", right != null);
        if (right != null) m.SetShaderParameter("view_right", right);
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
