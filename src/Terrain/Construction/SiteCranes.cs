using Godot;
using UnitSport.Items;

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
///
/// <para>
/// A crane a player works or has just let go (#618) is posed by <see cref="CraneService"/> instead,
/// and a pallet on its hook hangs from four slings. Every crane in the tree is in <see cref="All"/>
/// by its key, which is how a player finds the ladder, the cab and the remote.
/// </para>
/// </summary>
public partial class SiteCranes : Node3D
{
    /// <summary>One crane in the tree: where it stands and how it is posed now.</summary>
    public sealed class Live
    {
        public required SiteCranes Owner;
        public required SiteShellBuilder.CraneRigData Data;
        public required Node3D Pivot, Trolley, Hook, Rope;
        public required MeshInstance3D Load;
        public float JibBottom;
        /// <summary>The pose drawn last frame.</summary>
        public CranePose Last;
        /// <summary>The pallet on its hook, drawn, its slings, and its load byte (−1: none).</summary>
        public Node3D? Carried, Slings;
        public int CarriedLoad = -1;
        public Camera3D? Camera;
        public RayCast3D? Ray;

        public CraneJob Job => Data.Job;
        public string Key => Data.Job.Key;
        /// <summary>A tower crane has a cab and a ladder up to it; a self-erecting one only its remote.</summary>
        public bool HasCab => Data.Spot.Kind == CraneKind.Tower;

        /// <summary>A tile-local point in the world.</summary>
        public Vector3 ToWorld(Vector3 local) => Owner.GlobalTransform * local;

        /// <summary>The mast's centre on the ground, in the world.</summary>
        public Vector3 Foot => ToWorld(new Vector3(Data.Job.Base.X, Data.Ground, Data.Job.Base.Y));

        private Vector3 Face => new(Data.Face.X, 0, Data.Face.Y);

        /// <summary>Where a climber is <paramref name="height"/> up the ladder, over the base, and its heading (facing the mast).</summary>
        public (Vector3 At, float Yaw) OnLadder(float height)
        {
            var at = new Vector3(Data.Job.Base.X, Data.Ground + CranePlans.FootHeight(Data.Spot) + height, Data.Job.Base.Y)
                     + Face * CranePlans.LadderOut(Data.Spot);
            // yaw 0 faces -Z: facing -Face
            return (ToWorld(at), Mathf.Atan2(Face.X, Face.Z));
        }

        /// <summary>The foot of the ladder, a step out from it, on the ground: where a climber lands.</summary>
        public Vector3 LadderFoot => ToWorld(new Vector3(Data.Job.Base.X, Data.Ground + 0.1f, Data.Job.Base.Y)
                                             + Face * (CranePlans.LadderOut(Data.Spot) + 0.5f));

        private Vector3 RemoteSide
        {
            get
            {
                var f = CranePlans.RemoteFace(Data.Face);
                return new Vector3(f.X, 0, f.Y);
            }
        }

        /// <summary>Where the operator of the remote stands, by its box, and its heading (facing the mast).</summary>
        public (Vector3 At, float Yaw) AtRemote =>
            (ToWorld(new Vector3(Data.Job.Base.X, Data.Ground + 0.05f, Data.Job.Base.Y) + RemoteSide * (CranePlans.MastHalf(Data.Spot) + 0.75f)),
                Mathf.Atan2(RemoteSide.X, RemoteSide.Z));

        /// <summary>The remote's box itself, in the world.</summary>
        public Vector3 RemoteBox => ToWorld(new Vector3(Data.Job.Base.X, Data.Ground + CranePlans.RemoteHeight, Data.Job.Base.Y)
                                            + RemoteSide * (CranePlans.MastHalf(Data.Spot) + 0.12f));

        /// <summary>The remote's box as a box in the world, square to the tile as <c>CranePlans</c> draws it (its outline, #1286).</summary>
        public Transform3D RemoteFrame => new(Owner.GlobalBasis, RemoteBox);

        /// <summary>
        /// The ladder's lowest <paramref name="height"/> metres, the stiles' box in the world, x across them
        /// (its outline, #1286): the part a hand reaches from the ground, not all the way to the cab.
        /// </summary>
        public Transform3D LadderFrame(float height)
        {
            var centre = new Vector3(Data.Job.Base.X, Data.Ground + CranePlans.FootHeight(Data.Spot) + height / 2, Data.Job.Base.Y)
                         + Face * (CranePlans.MastHalf(Data.Spot) + 0.12f);
            return new Transform3D(Owner.GlobalBasis * new Basis(Vector3.Up, Mathf.Atan2(Face.X, Face.Z)), ToWorld(centre));
        }

        /// <summary>The operator's place in the cab for a pose, in the world, and its heading (along the jib).</summary>
        public (Vector3 At, float Yaw) InCab(CranePose pose) =>
            (ToWorld(Data.Pivot + new Basis(Vector3.Up, pose.Yaw) * CranePlans.CabSeat), pose.Yaw);

        /// <summary>The hook's origin for a pose, in the world.</summary>
        public Vector3 HookWorld(CranePose pose) => ToWorld(CraneControl.HookAt(Data.Job, pose));

        /// <summary>
        /// The height of the first solid thing under a world point, or <paramref name="from"/>'s
        /// height less 200 m: what the hook comes down onto. One node per worked crane, no allocation.
        /// </summary>
        public float FloorUnder(Vector3 from)
        {
            if (Ray == null)
            {
                Ray = new RayCast3D { Name = "HookRay", TopLevel = true, Enabled = false, TargetPosition = new Vector3(0, -200, 0) };
                Owner.AddChild(Ray);
            }
            Ray.GlobalPosition = from;
            Ray.ForceRaycastUpdate();
            return Ray.IsColliding() ? Ray.GetCollisionPoint().Y : from.Y - 200f;
        }

        /// <summary>The camera on the trolley, looking straight down at the hook, made on first use.</summary>
        public Camera3D HookCamera()
        {
            if (Camera == null)
            {
                Camera = new Camera3D
                {
                    Name = "HookCamera", Position = new Vector3(0, -0.45f, 0), Rotation = new Vector3(-Mathf.Pi / 2, 0, 0),
                    Fov = 55f, Near = 0.2f,
                };
                Trolley.AddChild(Camera);
            }
            return Camera;
        }
    }

    /// <summary>Every crane in the tree, by key (#618).</summary>
    public static readonly Dictionary<string, Live> All = new();

    private readonly CraneRig[] _rigs;
    private readonly Material _material;
    private readonly List<Live> _cranes = new();
    private ArrayMesh? _slings;

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

            var live = new Live { Owner = this, Data = d, Pivot = pivot, Trolley = trolley, Hook = hook, Rope = rope, Load = load, JibBottom = bottom };
            _cranes.Add(live);
            All[live.Key] = live;
        }
        Pose(0f);
    }

    public override void _ExitTree()
    {
        foreach (var c in _cranes)
            if (All.TryGetValue(c.Key, out var live) && live == c) All.Remove(c.Key);
        foreach (var rig in _rigs) rig.Dispose();
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree()) Pose((float)delta);
    }

    /// <summary>Every crane where the clocks, or whoever works it, say it is now.</summary>
    private void Pose(float dt)
    {
        double sim = Core.SimClock.SimAt(Net.ClockSync.ServerNow);
        double hour = World.WorldClock.CurrentHour;
        long day = (long)Math.Floor((World.WorldClock.EnvNow + World.WorldClock.HourShift) / 86400.0);
        var service = CraneService.Instance;
        foreach (var c in _cranes)
        {
            CraneService.Hold? hold = null;
            var pose = service != null && service.PoseOf(c.Job, sim, hour, day, dt, out var held, out hold)
                ? held : CraneMotion.Pose(c.Job, sim, hour, day);
            c.Last = pose;
            c.Pivot.Rotation = new Vector3(0, pose.Yaw, 0);
            c.Trolley.Position = new Vector3(0, c.JibBottom, -pose.Trolley);
            // the hook hangs from the trolley, its height in the pivot's frame
            float hookY = pose.HookY - c.Pivot.Position.Y;
            c.Hook.Position = new Vector3(0, hookY, -pose.Trolley);
            float ropeTop = c.JibBottom - 0.35f;
            c.Rope.Position = new Vector3(0, ropeTop, -pose.Trolley);
            c.Rope.Scale = new Vector3(1, Math.Max(0.05f, ropeTop - hookY), 1);
            c.Load.Visible = pose.Loaded;
            Hang(c, pose, hold);
        }
    }

    /// <summary>The pallet on a worked crane's hook (#618): in four slings, still on the ground while they are slack.</summary>
    private void Hang(Live c, CranePose pose, CraneService.Hold? hold)
    {
        int load = hold != null && Pallets.LoadCarried(hold.Carrying) is { } l ? l : -1;
        if (load != c.CarriedLoad)
        {
            c.Carried?.QueueFree();
            c.Carried = null;
            c.CarriedLoad = load;
            if (load >= 0)
            {
                // runners along the jib, as it is set down
                c.Carried = PalletNode.Carried((byte)load);
                c.Carried.Name = "HookPallet";
                c.Pivot.AddChild(c.Carried);
            }
        }
        if (c.Carried == null || hold == null)
        {
            if (c.Slings != null) c.Slings.Visible = false;
            return;
        }
        // lifted clear of where it stood: the slings are taut from now on, wherever the hook goes
        if (!float.IsNaN(hold.Rest) && pose.HookY - CraneControl.SlingHang > hold.Rest + 0.3f) hold.Rest = float.NaN;
        float under = CraneControl.HungAt(pose.HookY, hold.Rest);
        float y0 = c.Pivot.Position.Y;
        c.Carried.Position = new Vector3(0, under - y0 + Pallets.Seat, -pose.Trolley);
        if (c.Slings == null)
        {
            c.Slings = new MeshInstance3D { Name = "Slings", Mesh = _slings ??= ChunkNode.ToPropMesh(SlingMesh(), _material) };
            c.Pivot.AddChild(c.Slings);
        }
        c.Slings.Visible = true;
        float top = pose.HookY - CraneControl.HookBottom, bottom = under + Pallets.Height;
        c.Slings.Position = new Vector3(0, top - y0, -pose.Trolley);
        c.Slings.Scale = new Vector3(1, Math.Max(0.05f, top - bottom), 1);
    }

    /// <summary>Four slings from the hook down to a pallet's corners, one metre long: the node scales them to the drop.</summary>
    internal static PierMeshBuilder.MeshData SlingMesh()
    {
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        var indices = new List<int>();
        var rope = SiteShellBuilder.Colors[(int)ShellPart.Rope];
        const float w = 0.025f;
        foreach (var (x, z) in new[] { (-0.5f, -0.4f), (0.5f, -0.4f), (0.5f, 0.4f), (-0.5f, 0.4f) })
        {
            var b = new Vector3(x, -1f, z);
            // two crossed strips, both faces of each, so a sling reads from any side
            foreach (var across in new[] { new Vector3(w, 0, 0), new Vector3(0, 0, w) })
            {
                int n = vertices.Count;
                vertices.Add(-across);
                vertices.Add(across);
                vertices.Add(b + across);
                vertices.Add(b - across);
                for (int i = 0; i < 4; i++) colors.Add(rope);
                indices.AddRange(new[] { n, n + 1, n + 2, n, n + 2, n + 3, n, n + 2, n + 1, n, n + 3, n + 2 });
            }
        }
        return new PierMeshBuilder.MeshData(vertices.ToArray(), colors.ToArray(), indices.ToArray());
    }
}
