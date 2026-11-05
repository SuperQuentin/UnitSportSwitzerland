using Godot;
using UnitSport.Core;
using UnitSport.Occasions.Festivals;
using UnitSport.Terrain;

namespace UnitSport.Combat;

/// <summary>
/// The Älplerfest's sawdust ring (#1269): a 14 m disc of sawdust inside a rope on posts, 20 m north of the festival's middle (the mortar
/// rack of the closing show is 14 m west of the middle, the flag ground 12 m east, the flag poles round it all). Built once per site and kept; the origin container keeps
/// it where it belongs when the world origin shifts. Nothing is rebuilt per frame: it only looks at the running list every second.
/// Where the ring is, and E at it, are the server's and the player's concern (<see cref="SchwingManager"/>).
/// </summary>
public partial class SchwingRing : Node3D, IOriginContainer
{
    public const string NodeName = "SchwingRing";

    /// <summary>The ring's middle (LV95), 20 m north of the site's middle (the mortar rack is 14 m west, the flag ground 12 m east).</summary>
    public static (double E, double N) Of(ResolvedSite site)
    {
        var l = PosOf(site).Lv95;   // 20 m of true north, as PosOf (a grid-north 20 m is up to 0.7 m off at Scuol, #1502)
        return (l.E, l.N);
    }

    /// <summary><see cref="Of"/> as a position (the same 20 m north of the site's middle).</summary>
    public static GlobalPos PosOf(ResolvedSite site) => site.Pos.Moved(0, 20);

    public static SchwingRing? Instance { get; private set; }

    private readonly WorldOrigin _origin;
    private readonly Func<Vector3, float?> _ground;
    private GlobalPos? _at;
    private Node3D? _ring;
    private double _look;

    private SchwingRing(WorldOrigin origin, Func<Vector3, float?> ground)
    {
        Name = NodeName;
        _origin = origin;
        _ground = ground;
    }

    public static SchwingRing Create(Node world, ChunkManager? chunks, WorldOrigin origin)
    {
        var r = new SchwingRing(origin, p => chunks != null && chunks.Ground.TryGetHeight(p, out float h) ? h : null);
        world.AddChild(r);
        return r;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Process(double delta)
    {
        _look -= delta;
        if (_look > 0) return;
        _look = 1.0;
        Refresh();
    }

    /// <summary>Whether the local player stands at the ring while it is open: the prompt and the join read this.</summary>
    public static bool At(Node3D body)
    {
        if (SchwingManager.RingNow() is not { } ring || Instance is not { } node) return false;
        var origin = node._origin.ToWorld(ring.Pos.WithAlt(body.GlobalPosition.Y));
        var d = body.GlobalPosition - origin;
        d.Y = 0;
        return d.Length() <= SchwingRules.RingDiameter * 0.5f + SchwingManager.RingReach * 0.6f;
    }

    private void Refresh()
    {
        var ring = SchwingManager.RingNow();
        if (ring is null)
        {
            if (_ring != null) { _ring.QueueFree(); _ring = null; _at = null; }
            return;
        }
        if (_ring != null && _at == ring.Value.Pos) return;
        _ring?.QueueFree();
        _at = ring.Value.Pos;
        var probe = _origin.ToWorld(ring.Value.Pos.WithAlt(0));
        float y = _ground(probe) ?? GetViewport().GetCamera3D()?.GlobalPosition.Y - 1.7f ?? 400f;
        _ring = Build();
        _ring.Position = new Vector3(probe.X, y, probe.Z);
        AddChild(_ring);
    }

    private static Node3D Build()
    {
        var root = new Node3D { Name = "Ring" };
        float r = SchwingRules.RingDiameter * 0.5f;
        var sawdust = new StandardMaterial3D { AlbedoColor = new Color(0.84f, 0.7f, 0.46f), Roughness = 1f };
        root.AddChild(new MeshInstance3D
        {
            Name = "Sawdust",
            Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = 0.12f, RadialSegments = 32, Rings = 1 },
            MaterialOverride = sawdust,
            Position = new Vector3(0, 0.02f, 0),
        });
        // the rope: a torus on twelve posts
        var rope = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.2f, 0.18f), Roughness = 0.8f };
        root.AddChild(new MeshInstance3D
        {
            Name = "Rope",
            Mesh = new TorusMesh { InnerRadius = r - 0.03f, OuterRadius = r + 0.03f, Rings = 32, RingSegments = 6 },
            MaterialOverride = rope,
            Position = new Vector3(0, 0.55f, 0),
        });
        var post = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.3f, 0.18f), Roughness = 0.9f };
        var postMesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 0.6f, RadialSegments = 6 };
        for (int i = 0; i < 12; i++)
        {
            float a = i * Mathf.Tau / 12f;
            root.AddChild(new MeshInstance3D
            {
                Name = $"Post{i}",
                Mesh = postMesh,
                MaterialOverride = post,
                Position = new Vector3(Mathf.Cos(a) * r, 0.3f, Mathf.Sin(a) * r),
            });
        }
        return root;
    }
}
