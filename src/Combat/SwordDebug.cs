using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// <c>--sworddebug</c> (#1371): draws what a hit is decided on while the local player swings: the blade's capsule (yellow while it sweeps, red on the
/// frames it touched something) and the shapes of the targets it is tested against (green: the dummy's post, torso, head and arms, a body's
/// cylinder). A dev tool: pooled capsule nodes moved each frame, no pad or VR way.
/// </summary>
public partial class SwordDebug : Node3D
{
    public static bool On => CmdArgs.Has("--sworddebug");

    private MeshInstance3D? _blade;
    private readonly Dictionary<long, MeshInstance3D[]> _shapes = new();
    private StandardMaterial3D? _green, _yellow, _red;

    private static StandardMaterial3D Mat(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = c, NoDepthTest = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public override void _Ready()
    {
        _green = Mat(new Color(0.2f, 1f, 0.3f, 0.28f));
        _yellow = Mat(new Color(1f, 0.9f, 0.1f, 0.8f));
        _red = Mat(new Color(1f, 0.15f, 0.1f, 0.9f));
        _blade = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = SwordSweep.BladeRadius, Height = 0.95f }, Visible = false };
        AddChild(_blade);
    }

    public override void _Process(double delta)
    {
        if (GetViewport().GetCamera3D()?.GetParent() is not FootPlayer me || SwordService.Instance is not { } svc) return;
        // the blade: while a blow is being swept, and a quarter second after
        var last = me.SwordBladeLast;
        bool live = GameClock.Now - last.At < 0.25 && last.Hilt != last.Tip;
        _blade!.Visible = live;
        if (live) Place(_blade, last.Hilt, last.Tip, last.Touched ? _red! : _yellow!);
        // the shapes
        var reach = new List<SwordService.Reach>();
        svc.Targets(me, reach);
        var seen = new HashSet<long>();
        foreach (var r in reach)
        {
            long key = r.Kind == SwordTargetKind.Dummy ? -1_000_000 - r.Id : r.Id;
            seen.Add(key);
            var cap = new Capsule[4];
            var feet = new System.Numerics.Vector3(r.Position.X, r.Position.Y, r.Position.Z);
            int n = r.Kind == SwordTargetKind.Dummy ? SwordSweep.DummyShape(feet, r.Yaw, cap) : SwordSweep.BodyShape(feet, cap);
            if (!_shapes.TryGetValue(key, out var nodes))
            {
                nodes = new MeshInstance3D[n];
                for (int i = 0; i < n; i++)
                {
                    float len = (cap[i].B - cap[i].A).Length();
                    nodes[i] = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = cap[i].R, Height = len + 2f * cap[i].R }, MaterialOverride = _green };
                    AddChild(nodes[i]);
                }
                _shapes[key] = nodes;
            }
            for (int i = 0; i < n; i++)
                Place(nodes[i], new Vector3(cap[i].A.X, cap[i].A.Y, cap[i].A.Z), new Vector3(cap[i].B.X, cap[i].B.Y, cap[i].B.Z),
                    last.Touched && GameClock.Now - last.At < 0.25 ? _red! : _green!);
        }
        foreach (var key in _shapes.Keys.ToArray())
            if (!seen.Contains(key))
            {
                foreach (var n in _shapes[key]) n.QueueFree();
                _shapes.Remove(key);
            }
    }

    /// <summary>A capsule node standing on the segment a-b (its axis along it, its middle at the middle).</summary>
    private static void Place(MeshInstance3D node, Vector3 a, Vector3 b, Material material)
    {
        node.MaterialOverride = material;
        var axis = b - a;
        float len = axis.Length();
        if (len < 1e-4f) { node.GlobalPosition = a; return; }
        var y = axis / len;
        var x = Mathf.Abs(y.Dot(Vector3.Right)) < 0.9f ? y.Cross(Vector3.Right).Normalized() : y.Cross(Vector3.Forward).Normalized();
        var z = x.Cross(y);
        node.GlobalTransform = new Transform3D(new Basis(z.Cross(y).Normalized() * 1f, y, z), (a + b) * 0.5f);
    }
}
