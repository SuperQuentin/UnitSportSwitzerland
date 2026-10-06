using Godot;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// One crane's moving parts as Godot meshes (#610): made on the tile worker from
/// <see cref="SiteShellBuilder.CraneRigData"/>, handed to <see cref="SiteCranes"/> on the main thread.
/// </summary>
public sealed record CraneRig(SiteShellBuilder.CraneRigData Data, ArrayMesh Jib, ArrayMesh Trolley, ArrayMesh Hook,
    ArrayMesh Rope, ArrayMesh Load)
{
    /// <summary>On the worker: the five meshes, with the prop material.</summary>
    public static CraneRig Make(SiteShellBuilder.CraneRigData d, Material material) => new(d,
        ChunkNode.ToPropMesh(d.Jib, material), ChunkNode.ToPropMesh(d.Trolley, material), ChunkNode.ToPropMesh(d.Hook, material),
        ChunkNode.ToPropMesh(d.Rope, material), ChunkNode.ToPropMesh(d.Load, material));

    public void Dispose()
    {
        Jib.Dispose(); Trolley.Dispose(); Hook.Dispose(); Rope.Dispose(); Load.Dispose();
    }
}

/// <summary>
/// A tile's building-site cranes, moving (#610): a child of the tile's node, in its tile-local
/// frame, so it needs nothing on an origin shift. Each frame every crane is posed by
/// <see cref="CraneMotion.Pose"/> from the clocks every peer shares — the simulation clock for how
/// fast, the environment clock for whether it is working — so all peers see the same crane doing
/// the same lift with nothing sent. The jib carries a solid box (a helicopter hits it) and three
/// red obstacle lamps, a multimesh because the prop shader lights only instances with a glow gain.
/// </summary>
public partial class SiteCranes : Node3D
{
    private sealed class Crane
    {
        public required CraneJob Job;
        public required Node3D Pivot, Trolley, Hook, Rope;
        public required MeshInstance3D Load;
        public float JibBottom;
    }

    private readonly CraneRig[] _rigs;
    private readonly Material _material;
    private readonly List<Crane> _cranes = new();

    public SiteCranes(CraneRig[] rigs, Material material)
    {
        _rigs = rigs;
        _material = material;
        Name = "Cranes";
    }

    public override void _Ready()
    {
        var lamp = ChunkNode.ToPropMesh(SiteShellBuilder.MeshOf(new[]
        {
            new ShellBox(new Vector3(-0.18f, -0.18f, -0.18f), new Vector3(0.18f, 0.18f, 0.18f), ShellPart.LampRed, false),
        }), _material);
        foreach (var rig in _rigs)
        {
            var d = rig.Data;
            var pivot = new Node3D { Name = "Crane", Position = d.Pivot };
            AddChild(pivot);
            pivot.AddChild(new MeshInstance3D { Name = "Jib", Mesh = rig.Jib });
            float k = d.Spot.Kind == CraneKind.Tower ? 1f : 0.62f;
            float bottom = CranePlans.JibBottom * k;
            var trolley = new Node3D { Name = "Trolley", Position = new Vector3(0, bottom, -CraneMotion.MinTrolley) };
            trolley.AddChild(new MeshInstance3D { Mesh = rig.Trolley });
            pivot.AddChild(trolley);
            var rope = new Node3D { Name = "Rope" };
            rope.AddChild(new MeshInstance3D { Mesh = rig.Rope });
            pivot.AddChild(rope);
            var hook = new Node3D { Name = "Hook" };
            hook.AddChild(new MeshInstance3D { Mesh = rig.Hook });
            var load = new MeshInstance3D { Name = "Load", Mesh = rig.Load, Position = new Vector3(0, -0.95f, 0), Visible = false };
            hook.AddChild(load);
            pivot.AddChild(hook);

            // what a helicopter meets: the jib and the counter-jib as one box, slewing with them
            var (center, size) = CranePlans.JibBounds(d.Spot);
            var body = new StaticBody3D { Name = "JibBody", Position = center };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            pivot.AddChild(body);

            // the red obstacle lamps: lit at night by the prop shader's glow (custom.y = gain)
            var multi = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true,
                Mesh = lamp, InstanceCount = d.Lamps.Length,
            };
            for (int i = 0; i < d.Lamps.Length; i++)
            {
                multi.SetInstanceTransform(i, new Transform3D(Basis.Identity, d.Lamps[i]));
                multi.SetInstanceCustomData(i, new Color(i * 0.37f, 1f, 0f, 0f));
            }
            pivot.AddChild(new MultiMeshInstance3D { Name = "Lamps", Multimesh = multi });

            _cranes.Add(new Crane { Job = d.Job, Pivot = pivot, Trolley = trolley, Hook = hook, Rope = rope, Load = load, JibBottom = bottom });
        }
        Pose();
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree()) Pose();
    }

    /// <summary>Every crane where the clocks say it is now.</summary>
    private void Pose()
    {
        double sim = Core.SimClock.SimAt(Net.ClockSync.ServerNow);
        double hour = World.WorldClock.CurrentHour;
        long day = (long)Math.Floor((World.WorldClock.EnvNow + World.WorldClock.HourShift) / 86400.0);
        foreach (var c in _cranes)
        {
            var pose = CraneMotion.Pose(c.Job, sim, hour, day);
            c.Pivot.Rotation = new Vector3(0, pose.Yaw, 0);
            c.Trolley.Position = new Vector3(0, c.JibBottom, -pose.Trolley);
            // the hook hangs from the trolley, its height in the pivot's frame
            float hookY = pose.HookY - c.Pivot.Position.Y;
            c.Hook.Position = new Vector3(0, hookY, -pose.Trolley);
            float ropeTop = c.JibBottom - 0.35f;
            c.Rope.Position = new Vector3(0, ropeTop, -pose.Trolley);
            c.Rope.Scale = new Vector3(1, Math.Max(0.05f, ropeTop - hookY), 1);
            c.Load.Visible = pose.Loaded;
        }
    }

    public override void _ExitTree()
    {
        foreach (var rig in _rigs) rig.Dispose();
    }
}
