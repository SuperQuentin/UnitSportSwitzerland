using Godot;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>
/// The telescope on its tripod (#1440): three splayed aluminium legs, a mount head, and a white
/// refractor tube on it. Set up, the tube is its own node (<see cref="TubeName"/>) turned about the
/// head, so looking through it never rebuilds a mesh (<c>general/no-mesh-rebuild-per-frame</c>);
/// the eye goes at <see cref="EyeOffset"/> on the tube. Meshes built once, vertex-coloured.
/// </summary>
public static class TelescopeVisual
{
    public const string TubeName = "Tube";

    /// <summary>The mount head: the tube turns about this point, metres above the ground.</summary>
    public static readonly Vector3 Head = new(0, 1.28f, 0);

    /// <summary>Where the view is taken from, in the tube's frame (on its axis, at the objective's end).</summary>
    public static readonly Vector3 EyeOffset = new(0, 0.07f, -0.5f);

    private static readonly Color Leg = new(0.72f, 0.74f, 0.77f), Dark = new(0.09f, 0.09f, 0.1f),
        Tube = new(0.93f, 0.93f, 0.9f), Trim = new(0.78f, 0.62f, 0.25f);

    private static ArrayMesh? _mount, _tube, _whole;

    /// <summary>The tripod and its head, without the tube.</summary>
    [Showcase("Items", "Telescope mount")]
    public static ArrayMesh MountMesh() => _mount ??= Build(MountParts);

    /// <summary>The tube, in its own frame: along -Z from the head, the eyepiece behind.</summary>
    [Showcase("Items", "Telescope tube")]
    public static ArrayMesh TubeMesh() => _tube ??= Build(TubeParts);

    /// <summary>The whole telescope set up, tube level: the ghost and the model viewer.</summary>
    [Showcase("Items", "Telescope, set up")]
    public static ArrayMesh SetUpMesh() => _whole ??= Build(st =>
    {
        MountParts(st);
        TubeParts(st, Head);
    });

    /// <summary>A set-up telescope (<c>PlacedKind.Telescope</c>): a thin collider, the mount, and the tube to turn.</summary>
    public static Node3D Visual(PlacedObject _)
    {
        var body = new StaticBody3D();
        body.AddToGroup(Player.FootPlayer.NoClimbGroup);   // placed things are never free-climbed (#1225)
        body.AddChild(new MeshInstance3D { Mesh = MountMesh(), MaterialOverride = ItemDefs.Material });
        var tube = new Node3D { Name = TubeName, Position = Head };
        tube.AddChild(new MeshInstance3D { Mesh = TubeMesh(), MaterialOverride = ItemDefs.Material });
        body.AddChild(tube);
        // as thin as the legs stand: something to walk round and to aim at, not a wall
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f, 1.4f, 0.5f) }, Position = new Vector3(0, 0.7f, 0) });
        return body;
    }

    private static void MountParts(SurfaceTool st)
    {
        // three legs from the head down and out to a 0.42 m circle
        for (int i = 0; i < 3; i++)
        {
            float a = i * Mathf.Tau / 3f + Mathf.Pi / 6f;
            var foot = new Vector3(Mathf.Cos(a) * 0.42f, 0f, Mathf.Sin(a) * 0.42f);
            var top = new Vector3(Mathf.Cos(a) * 0.06f, Head.Y - 0.1f, Mathf.Sin(a) * 0.06f);
            Strut(st, foot, top, 0.035f, Leg);
            Strut(st, foot + (top - foot) * 0.02f, foot + (top - foot) * 0.08f, 0.05f, Dark);   // a rubber foot
        }
        // the spreader tray half way up, meeting the legs, and the mount head
        Box(st, new Vector3(0, 0.55f, 0), Basis.Identity, new Vector3(0.36f, 0.03f, 0.36f), Leg);
        Box(st, new Vector3(0, Head.Y - 0.1f, 0), Basis.Identity, new Vector3(0.16f, 0.1f, 0.16f), Dark);
        Box(st, new Vector3(0, Head.Y - 0.03f, 0), Basis.Identity, new Vector3(0.08f, 0.06f, 0.08f), Dark);
    }

    private static void TubeParts(SurfaceTool st) => TubeParts(st, Vector3.Zero);

    private static void TubeParts(SurfaceTool st, Vector3 at)
    {
        var b = Basis.Identity;
        Box(st, at + new Vector3(0, 0.07f, -0.08f), b, new Vector3(0.12f, 0.12f, 0.86f), Tube);       // the tube
        Box(st, at + new Vector3(0, 0.07f, -0.58f), b, new Vector3(0.135f, 0.135f, 0.16f), Dark);      // the dew shield
        Box(st, at + new Vector3(0, 0.07f, -0.5f), b, new Vector3(0.127f, 0.127f, 0.02f), Trim);       // a brass ring
        Box(st, at + new Vector3(0, 0.07f, 0.38f), b, new Vector3(0.07f, 0.07f, 0.1f), Dark);          // the focuser
        Box(st, at + new Vector3(0, 0.14f, 0.43f), b, new Vector3(0.04f, 0.1f, 0.04f), Dark);          // the eyepiece, up
        Box(st, at + new Vector3(0, 0.155f, -0.1f), b, new Vector3(0.03f, 0.03f, 0.3f), Dark);         // the finder
        Box(st, at + new Vector3(0, 0f, 0f), b, new Vector3(0.06f, 0.05f, 0.18f), Dark);              // the saddle
    }

    private static ArrayMesh Build(System.Action<SurfaceTool> parts)
    {
        ShowcaseTrace.Mark();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        parts(st);
        return st.Commit();
    }

    /// <summary>A square-section strut from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static void Strut(SurfaceTool st, Vector3 a, Vector3 b, float width, Color color)
    {
        var along = (b - a).Normalized();
        var side = along.Cross(Vector3.Up).LengthSquared() > 1e-4f ? along.Cross(Vector3.Up).Normalized() : Vector3.Right;
        var basis = new Basis(side, along, side.Cross(along));
        Box(st, (a + b) / 2, basis, new Vector3(width, a.DistanceTo(b), width), color);
    }

    /// <summary>A box of <paramref name="size"/> (in its own axes) at <paramref name="centre"/>, turned by <paramref name="basis"/>.</summary>
    private static void Box(SurfaceTool st, Vector3 centre, Basis basis, Vector3 size, Color color)
    {
        var h = size / 2;
        for (int axis = 0; axis < 3; axis++)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                var n = Vector3.Zero;
                n[axis] = sign;
                var u = Vector3.Zero;
                u[(axis + 1) % 3] = 1;
                var v = n.Cross(u);
                var o = n * h[axis];
                float du = h[(axis + 1) % 3], dv = Mathf.Abs(v.X) * h.X + Mathf.Abs(v.Y) * h.Y + Mathf.Abs(v.Z) * h.Z;
                Vector3 Corner(float su, float sv) => centre + basis * (o + u * (su * du) + v * (sv * dv));
                var wn = (basis * n).Normalized();
                // the winding PoiMarkerMeshBuilder.Box uses (n = u x v)
                foreach (var (su, sv) in new[] { (-1f, -1f), (1f, 1f), (1f, -1f), (-1f, -1f), (-1f, 1f), (1f, 1f) })
                {
                    st.SetColor(color);
                    st.SetNormal(wn);
                    st.AddVertex(Corner(su, sv));
                }
            }
    }
}
