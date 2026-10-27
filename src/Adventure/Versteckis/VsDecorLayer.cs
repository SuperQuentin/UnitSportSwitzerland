using Godot;
using UnitSport.Core;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// A game's area drawn on a client (#1268, plan "Performance"): one <see cref="MultiMesh"/> per decor kind (built once
/// from the hide plan, freed with the game; the herds draw their own cows), the red-and-white boundary posts (one more
/// MultiMesh), a translucent band on the line that fades in within 15 m (a fragment term, no geometry), the objectives'
/// props (the bell by the church door, the Höhenfeuer's stack and its flame, the milk can and its Milchbänkli), the
/// decoys, a sniff's puffs, and the decor's collision boxes so a player walks round the bales as a bot does. Heights come
/// from the coarse grid first and settle onto the streamed ground as it arrives (a transform per instance, never a mesh).
/// </summary>
public partial class VsDecorLayer : Node3D
{
    public WorldOrigin Origin { get; set; } = null!;

    private VsGround? _site;
    private Func<Vector3, float?> _ground = _ => null;
    private readonly Dictionary<VsProp, (MultiMeshInstance3D Node, List<VsDecor> Items)> _kinds = new();
    private MultiMeshInstance3D? _posts;
    private MeshInstance3D? _band;
    private StaticBody3D? _solid;
    private Node3D? _bell, _stack, _flame, _can, _bench, _benchCan;
    private readonly List<MeshInstance3D> _decoys = new();
    private readonly List<(MeshInstance3D Node, double Until)> _puffs = new();
    private double _settleAt;
    private int _settles;

    /// <summary>Decor instances drawn (checks).</summary>
    public int DecorCount { get; private set; }
    /// <summary>Boundary posts drawn (checks).</summary>
    public int PostCount { get; private set; }
    public bool Built => _site != null;

    /// <summary>The post spacing on the line, m.</summary>
    public const float PostEvery = 12f;

    private Vector3 World(double e, double n, float? alt = null)
    {
        var w = Origin.ToWorld(e, n, alt ?? 0);
        if (alt == null) w.Y = _ground(w) ?? _site?.GroundAt(e, n) ?? w.Y;
        return GlobalTransform.AffineInverse() * w;
    }

    /// <summary>Builds the area once: the decor, the line, the objectives' props, the collision.</summary>
    public void Build(VsGround site, Func<Vector3, float?> ground)
    {
        Clear();
        _site = site;
        _ground = ground;
        var material = Rewards.RewardMeshes.Material();
        foreach (var group in site.Decor.Where(d => d.Kind != VsProp.Cow).GroupBy(d => d.Kind))
        {
            var items = group.ToList();
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = VsPropMeshes.Of(group.Key), InstanceCount = items.Count };
            var node = new MultiMeshInstance3D { Name = group.Key.ToString(), Multimesh = mm, MaterialOverride = material };
            AddChild(node);
            _kinds[group.Key] = (node, items);
            DecorCount += items.Count;
        }
        // the posts on the line, every 12 m
        int posts = Math.Max(12, (int)(Mathf.Tau * site.Radius / PostEvery));
        var pm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = VsPropMeshes.Post(), InstanceCount = posts };
        _posts = new MultiMeshInstance3D { Name = "Posts", Multimesh = pm, MaterialOverride = material };
        AddChild(_posts);
        PostCount = posts;
        // the band: an open cylinder on the line, its alpha a term of the fragment's distance to the camera
        _band = new MeshInstance3D
        {
            Name = "Band",
            Mesh = new CylinderMesh { TopRadius = site.Radius, BottomRadius = site.Radius, Height = 4f, CapTop = false, CapBottom = false, RadialSegments = 96, Rings = 1 },
            MaterialOverride = BandMaterial(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_band);
        // the objectives' props
        foreach (var o in site.Sites)
            switch (o.Site)
            {
                case VsSite.Bell:
                    _bell = new Node3D { Name = "Bell" };
                    _bell.AddChild(new MeshInstance3D { Mesh = Rewards.RewardMeshes.BellRail(), MaterialOverride = material, Scale = new Vector3(0.35f, 1f, 1f) });
                    _bell.AddChild(new MeshInstance3D { Mesh = Rewards.RewardMeshes.Bell(), MaterialOverride = material, Position = Vector3.Up * Rewards.RewardMeshes.BellHang });
                    AddChild(_bell);
                    break;
                case VsSite.Fire:
                    _stack = new MeshInstance3D { Name = "Hoehenfeuer", Mesh = Rewards.RewardMeshes.WoodStack(), MaterialOverride = material };
                    AddChild(_stack);
                    _flame = new MeshInstance3D { Name = "Flame", Mesh = Rewards.RewardMeshes.Flame(), MaterialOverride = Rewards.RewardMeshes.GlowMaterial(), Visible = false };
                    AddChild(_flame);
                    break;
                case VsSite.Can:
                    _can = new MeshInstance3D { Name = "MilkCan", Mesh = VsPropMeshes.MilkCan(), MaterialOverride = material };
                    AddChild(_can);
                    _bench = new MeshInstance3D { Name = "Milchbank", Mesh = VsPropMeshes.Milchbank(), MaterialOverride = material };
                    AddChild(_bench);
                    _benchCan = new MeshInstance3D { Name = "DeliveredCan", Mesh = VsPropMeshes.MilkCan(), MaterialOverride = material, Visible = false };
                    AddChild(_benchCan);
                    break;
            }
        _solid = Solids(site, (e, n) => World(e, n));
        AddChild(_solid);
        Place();
        _settleAt = GameClock.Now + 1;
        _settles = 0;
    }

    /// <summary>Every instance and prop on its ground: at build, and again as the streamed ground arrives.</summary>
    private void Place()
    {
        if (_site == null) return;
        foreach (var (kind, (node, items)) in _kinds)
            for (int i = 0; i < items.Count; i++)
            {
                var d = items[i];
                node.Multimesh.SetInstanceTransform(i, new Transform3D(new Basis(Vector3.Up, d.Yaw), World(d.E, d.N)));
            }
        if (_posts != null)
        {
            int count = _posts.Multimesh.InstanceCount;
            for (int i = 0; i < count; i++)
            {
                double a = Math.Tau * i / count;
                _posts.Multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, World(_site.E + Math.Cos(a) * _site.Radius, _site.N + Math.Sin(a) * _site.Radius)));
            }
        }
        if (_band != null) _band.Position = World(_site.E, _site.N) + Vector3.Up * 1.2f;
        foreach (var o in _site.Sites)
            switch (o.Site)
            {
                case VsSite.Bell when _bell != null: _bell.Position = World(o.E, o.N); break;
                case VsSite.Fire when _stack != null:
                    _stack.Position = World(o.E, o.N);
                    _flame!.Position = _stack.Position + Vector3.Up * 0.3f;
                    break;
                case VsSite.Can when _can != null:
                    _can.Position = World(o.FromE, o.FromN);
                    _bench!.Position = World(o.E, o.N);
                    _benchCan!.Position = _bench.Position + Vector3.Up * 0.85f;
                    break;
            }
    }

    /// <summary>The decor's collision: a box per prop (the herds' cows are the herds'), and the objectives' props.</summary>
    public static StaticBody3D Solids(VsGround site, Func<double, double, Vector3> at)
    {
        var body = new StaticBody3D { Name = "Solid" };
        void Box(double e, double n, float yaw, float w, float d, float h)
        {
            var p = at(e, n);
            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(w, h, d) },
                Transform = new Transform3D(new Basis(Vector3.Up, yaw), p + Vector3.Up * (h / 2)),
            });
        }
        foreach (var dc in site.Decor)
        {
            if (dc.Kind == VsProp.Cow) continue;
            var info = VsProps.Of(dc.Kind);
            Box(dc.E, dc.N, dc.Yaw, info.Width, info.Depth, info.Height);
        }
        foreach (var o in site.Sites)
            if (o.Site == VsSite.Can) Box(o.E, o.N, 0, 1.7f, 0.7f, 0.85f);
            else if (o.Site == VsSite.Fire) Box(o.E, o.N, 0, 1.2f, 1.2f, 1.3f);
        return body;
    }

    /// <summary>The state's part of the area: objectives done or carried, decoys.</summary>
    public void Refresh(VsState? s)
    {
        if (_site == null) return;
        bool running = s is { Phase: VsPhase.Gather or VsPhase.Hide or VsPhase.Seek or VsPhase.RoundEnd };
        Visible = s != null && s.Phase != VsPhase.Closed;
        if (s == null) return;
        foreach (var o in s.Objectives)
            switch (o.Site)
            {
                case VsSite.Fire when _flame != null: _flame.Visible = o.Done && running; break;
                case VsSite.Can when _can != null:
                    _can.Visible = !o.Done && o.Carrier == 0;
                    _benchCan!.Visible = o.Done;
                    break;
            }
        // the decoys: a pool of instances showing the shared meshes
        while (_decoys.Count < s.Decoys.Count)
        {
            var m = new MeshInstance3D { Name = $"Decoy{_decoys.Count}", MaterialOverride = Rewards.RewardMeshes.Material() };
            AddChild(m);
            _decoys.Add(m);
        }
        for (int i = 0; i < _decoys.Count; i++)
        {
            bool on = i < s.Decoys.Count;
            _decoys[i].Visible = on;
            if (!on) continue;
            var d = s.Decoys[i];
            var mesh = VsPropMeshes.Of(d.Prop);
            if (_decoys[i].Mesh != mesh) _decoys[i].Mesh = mesh;
            _decoys[i].Transform = new Transform3D(new Basis(Vector3.Up, d.Yaw), World(d.E, d.N, d.Alt));
        }
    }

    /// <summary>A sniff's puffs: a pollen cloud at each point for a few seconds.</summary>
    public void Puffs(double[] es, double[] ns, double seconds)
    {
        for (int i = 0; i < Math.Min(es.Length, ns.Length); i++)
        {
            var m = new MeshInstance3D
            {
                Name = "Puff", Mesh = PuffMesh, MaterialOverride = PuffMaterial,
                Position = World(es[i], ns[i]) + Vector3.Up * 1.6f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(m);
            _puffs.Add((m, GameClock.Now + seconds));
        }
    }

    private static readonly SphereMesh PuffMesh = new() { Radius = 0.9f, Height = 1.8f, RadialSegments = 10, Rings = 6 };
    private static StandardMaterial3D PuffMaterial => _puffMaterial ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(1f, 0.92f, 0.3f, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
    private static StandardMaterial3D? _puffMaterial;

    private static ShaderMaterial? _bandMaterial;

    /// <summary>The band: red and white stripes, unshaded, faded in within 15 m of the camera (the line near the player).</summary>
    private static ShaderMaterial BandMaterial()
    {
        if (_bandMaterial != null) return _bandMaterial;
        var shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
                void fragment() {
                    float near = 1.0 - smoothstep(4.0, 15.0, length(VERTEX));
                    float stripe = step(0.5, fract(UV.x * 120.0));
                    ALBEDO = mix(vec3(0.85, 0.08, 0.1), vec3(0.95), stripe);
                    ALPHA = near * 0.35 * (1.0 - abs(UV.y - 0.5) * 1.6);
                }
                """,
        };
        return _bandMaterial = new ShaderMaterial { Shader = shader };
    }

    public void Clear()
    {
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        _kinds.Clear();
        _decoys.Clear();
        _puffs.Clear();
        _posts = null;
        _band = null;
        _solid = null;
        _bell = _stack = _flame = _can = _bench = _benchCan = null;
        _site = null;
        DecorCount = PostCount = 0;
    }

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        for (int i = _puffs.Count - 1; i >= 0; i--)
            if (now >= _puffs[i].Until)
            {
                _puffs[i].Node.QueueFree();
                _puffs.RemoveAt(i);
            }
        // settle on the streamed ground: once a second for the first 20 s, then every 10 s
        if (_site != null && now >= _settleAt)
        {
            _settleAt = now + (++_settles < 20 ? 1 : 10);
            Place();
        }
    }
}
