using Godot;
using UnitSport.Core;

namespace UnitSport.Movie;

/// <summary>
/// Every camera of the movie drawn in the studio's view (#675): a small camera body and lens in the
/// camera's timeline colour, with its name over it, standing where that camera is at the playhead
/// and pointing where it looks. Hidden while looking through a camera, so none ever films another.
/// </summary>
public partial class CameraGizmos : Node3D
{
    private readonly MovieStage _stage;
    private readonly WorldOrigin _origin;
    private readonly Func<int, Vector3?> _actorAt;
    private readonly List<(Node3D Root, Label3D Name, StandardMaterial3D Paint)> _gizmos = new();

    /// <summary>Camera <paramref name="i"/>'s gizmo, for the checks; null before it is made.</summary>
    public Node3D? Gizmo(int i) => i < _gizmos.Count ? _gizmos[i].Root : null;

    /// <summary>Hides them all: the view is a camera's own.</summary>
    public bool Hidden { get; set; }

    public CameraGizmos(MovieStage stage, WorldOrigin origin, Func<int, Vector3?> actorAt)
    {
        Name = "CameraGizmos";
        _stage = stage;
        _origin = origin;
        _actorAt = actorAt;
    }

    public override void _Process(double delta)
    {
        var cameras = _stage.Project.Cameras;
        while (_gizmos.Count < cameras.Count) _gizmos.Add(Make());
        for (int i = 0; i < _gizmos.Count; i++)
        {
            var (root, name, paint) = _gizmos[i];
            bool shown = !Hidden && i < cameras.Count && cameras[i].Sample(_stage.Time, out var pose);
            if (root.Visible != shown) root.Visible = shown;
            if (!shown) continue;
            cameras[i].Sample(_stage.Time, out pose);
            root.GlobalTransform = StudioCamera.PoseTransform(_origin, pose, _actorAt);
            var color = TimelineView.CameraColor(i);
            if (paint.AlbedoColor != color) paint.AlbedoColor = color;
            if (name.Text != cameras[i].Name) name.Text = cameras[i].Name;
        }
    }

    /// <summary>A camera body (a box) with a lens (a cone) looking down −Z, and its name above.</summary>
    private (Node3D, Label3D, StandardMaterial3D) Make()
    {
        var paint = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.White };
        var root = new Node3D { Visible = false };
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.38f, 0.7f) }, MaterialOverride = paint });
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.26f, BottomRadius = 0.1f, Height = 0.4f, RadialSegments = 12 },
            MaterialOverride = paint,
            // a cylinder stands along Y: turned to open forward, along −Z
            Transform = new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2), new Vector3(0, 0, -0.52f)),
        });
        var name = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Position = new Vector3(0, 0.55f, 0), PixelSize = 0.006f,
            FontSize = 48, OutlineSize = 10, NoDepthTest = true,
        };
        root.AddChild(name);
        AddChild(root);
        return (root, name, paint);
    }

    /// <summary>A camera removed or a project loaded: the gizmos are made again for the cameras there are.</summary>
    public void Reset()
    {
        foreach (var (root, _, _) in _gizmos) root.QueueFree();
        _gizmos.Clear();
    }
}
