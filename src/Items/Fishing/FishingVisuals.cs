using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items.Fishing;

/// <summary>
/// What every peer sees of a rod in use (#493): the float on the water and the line from the rod's tip,
/// one per angler, from two item events the owner sends (docs/notes/items/fishing.md):
/// <list type="bullet">
/// <item><see cref="ItemEventKind.FishCast"/>: the float landed at Position (water or ground).</item>
/// <item><see cref="ItemEventKind.FishEnd"/>: the line came in at Position; Extra is "" (wound in),
/// "snap", or "&lt;item id&gt;" for a fish landed and kept (it flies from the water to the angler).</item>
/// </list>
/// Both are sent with no direction, so a remote peer keeps the position instead of moving it to the hand.
/// The line goes once the angler puts the rod away, whatever arrived. The bite and the fight are the
/// owner's alone: <see cref="Dip"/> and <see cref="Drag"/> move the owner's own float.
/// </summary>
public partial class FishingVisuals : Node3D
{
    /// <summary>The rod's tip in its mesh (authored space, +Z forward): the line starts there.</summary>
    public static readonly Vector3 RodTip = new(0, 1.05f, 2.05f);

    private sealed class Line
    {
        public GlobalPos Float;
        public MeshInstance3D Bobber = null!;
        public float Dip;
        public bool Local;
    }

    private readonly Dictionary<long, Line> _lines = new();
    private readonly List<long> _gone = new();
    private ItemEvents _events = null!;
    private ImmediateMesh _mesh = null!;
    /// <summary>This machine's own angler, as its events name it (1 offline, the unique id online).</summary>
    private long _localPeer = long.MinValue;

    private static readonly NodePath HeldItemNode = "HeldItem";

    public static FishingVisuals Of(ItemEvents n)
    {
        if (n.GetNodeOrNull<FishingVisuals>("FishingLines") is { } v) return v;
        v = new FishingVisuals { Name = "FishingLines", _events = n, TopLevel = true };
        n.AddChild(v);
        return v;
    }

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        AddChild(new MeshInstance3D
        {
            Mesh = _mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.86f, 0.88f, 0.84f, 0.85f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        });
    }

    // ---- the events ------------------------------------------------------------------------------

    public static void OnCast(ItemEvents n, ItemEvent e)
    {
        if (n.Origin is not { } origin) return;
        var v = Of(n);
        if (!v._lines.TryGetValue(e.Peer, out var line))
        {
            line = new Line { Bobber = NewBobber(), Local = e.Local };
            v.AddChild(line.Bobber);
            v._lines[e.Peer] = line;
        }
        if (e.Local) v._localPeer = e.Peer;
        line.Float = origin.ToGlobal(e.Position);
        line.Dip = 0;
        line.Bobber.GlobalPosition = e.Position;
        bool wet = World.WaterField.TryLevelAt(e.Position, out _);
        if (wet) Splash(v, e.Position, 0.25f);
        n.Sound3D(e.Position, Plop, wet ? 1f : 0.6f, wet ? -6f : -14f, unitSize: 6f, maxDistance: 80f);
    }

    public static void OnEnd(ItemEvents n, ItemEvent e)
    {
        var v = Of(n);
        if (v._lines.Remove(e.Peer, out var line)) line.Bobber.QueueFree();
        if (e.Extra == "snap")
        {
            n.Sound3D(e.Position, Snap, 1f, -4f, unitSize: 6f, maxDistance: 60f);
            return;
        }
        if (!int.TryParse(e.Extra, out int id) || ItemDefs.HandMesh((ItemId)id) is not { } fish) return;
        // the fish comes out of the water, wriggling, into the angler's hand
        if (World.WaterField.TryLevelAt(e.Position, out _)) Splash(v, e.Position, 0.5f);
        n.Sound3D(e.Position, SfxSynth.SplashBank.Variants[0], 1.6f, -8f, unitSize: 6f, maxDistance: 80f);
        var body = v.Angler(e.Peer, e.Local);
        if (body == null) return;
        var flying = new MeshInstance3D { Mesh = fish, MaterialOverride = ItemDefs.Material, TopLevel = true, Scale = Vector3.One * 1.6f };
        v.AddChild(flying);
        flying.GlobalPosition = e.Position;
        var to = body.GlobalPosition + Vector3.Up * 1.3f;
        var t = flying.CreateTween();
        t.SetParallel();
        t.TweenProperty(flying, "global_position", to, 0.6).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        t.TweenProperty(flying, "rotation", new Vector3(0, 0, Mathf.Tau * 2), 0.6);
        t.Chain().TweenCallback(Callable.From(flying.QueueFree));
    }

    // ---- the owner's own float ---------------------------------------------------------------------

    /// <summary>This machine's float dips under for a bite (nobody else sees the bite).</summary>
    public void Dip(float depth)
    {
        if (_lines.TryGetValue(_localPeer, out var line)) line.Dip = depth;
    }

    /// <summary>Moves this machine's float, as a hooked fish drags it about (only the owner sees it move).</summary>
    public void Drag(Vector3 world)
    {
        if (_lines.TryGetValue(_localPeer, out var line) && _events.Origin is { } origin) line.Float = origin.ToGlobal(world);
    }

    /// <summary>Where this machine's float is now, if it has one out.</summary>
    public Vector3? LocalFloat =>
        _lines.TryGetValue(_localPeer, out var line) && _events.Origin is { } origin ? origin.ToWorld(line.Float) : null;

    // ---- every frame -------------------------------------------------------------------------------

    private FootPlayer? Angler(long peer, bool local) => local
        ? ItemController.Instance?.UsablePlayer
        : _events.GetNodeOrNull<FootPlayer>("../Players/" + peer);

    public override void _Process(double delta)
    {
        _mesh.ClearSurfaces();
        if (_lines.Count == 0 || _events.Origin is not { } origin) return;
        double t = Time.GetTicksMsec() / 1000.0;
        bool begun = false;
        foreach (var (peer, line) in _lines)
        {
            var body = Angler(peer, line.Local);
            // the rod put away (or the angler gone) takes the line with it
            if (body == null || body.HeldItemId != (int)ItemId.FishingRod)
            {
                _gone.Add(peer);
                continue;
            }
            var at = origin.ToWorld(line.Float);
            if (World.WaterField.TryLevelAt(at, out float level))
                at.Y = level + 0.02f + 0.012f * Mathf.Sin((float)t * 2.3f + peer) - line.Dip;
            line.Bobber.GlobalPosition = at;
            line.Dip = Mathf.MoveToward(line.Dip, 0f, (float)delta * 0.25f);

            var visual = body.GetNodeOrNull<HeldItemVisual>(HeldItemNode);
            var tip = visual?.ItemPoint(RodTip) ?? body.GlobalPosition + Vector3.Up * 2.2f;
            if (!begun)
            {
                _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                begun = true;
            }
            // a hanging line: a parabola sagging by a few percent of its length
            const int Segments = 10;
            float sag = 0.06f * tip.DistanceTo(at);
            var prev = tip;
            for (int i = 1; i <= Segments; i++)
            {
                float u = i / (float)Segments;
                var p = tip.Lerp(at, u) + Vector3.Down * (sag * 4f * u * (1f - u));
                _mesh.SurfaceAddVertex(prev);
                _mesh.SurfaceAddVertex(p);
                prev = p;
            }
        }
        if (begun) _mesh.SurfaceEnd();
        foreach (long peer in _gone)
            if (_lines.Remove(peer, out var line)) line.Bobber.QueueFree();
        _gone.Clear();
    }

    // ---- pieces ------------------------------------------------------------------------------------

    private static StandardMaterial3D? _red, _white;
    private static SphereMesh? _top;
    private static CylinderMesh? _stem;

    /// <summary>A red-topped float: a ball over a white stem.</summary>
    private static MeshInstance3D NewBobber()
    {
        _red ??= new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.12f, 0.08f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _white ??= new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.95f, 0.92f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _top ??= new SphereMesh { Radius = 0.045f, Height = 0.09f, RadialSegments = 8, Rings = 4 };
        _stem ??= new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.02f, Height = 0.08f, RadialSegments = 6 };
        var b = new MeshInstance3D { Mesh = _top, MaterialOverride = _red, TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        b.AddChild(new MeshInstance3D { Mesh = _stem, MaterialOverride = _white, Position = new Vector3(0, -0.06f, 0) });
        return b;
    }

    private static StandardMaterial3D? _sprayMaterial;
    private static QuadMesh? _sprayMesh;

    /// <summary>A small ring of spray where the float or a fish broke the surface.</summary>
    private static void Splash(Node parent, Vector3 at, float strength)
    {
        if (DisplayServer.GetName() == "headless") return;
        _sprayMaterial ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.86f, 0.94f, 1f, 0.85f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        };
        _sprayMesh ??= new QuadMesh { Size = new Vector2(0.05f, 0.05f) };
        var spray = new CpuParticles3D
        {
            TopLevel = true, Amount = 12 + (int)(40 * strength), Lifetime = 0.5f, OneShot = true, Explosiveness = 0.9f,
            Emitting = true, EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring, EmissionRingAxis = Vector3.Up,
            EmissionRingRadius = 0.12f + 0.2f * strength, EmissionRingInnerRadius = 0.05f, EmissionRingHeight = 0.02f,
            Direction = Vector3.Up, Spread = 25f, InitialVelocityMin = 0.8f, InitialVelocityMax = 1.5f + 2f * strength,
            Gravity = new Vector3(0, -9.8f, 0), Mesh = _sprayMesh, MaterialOverride = _sprayMaterial,
        };
        parent.AddChild(spray);
        spray.GlobalPosition = at + Vector3.Up * 0.03f;
        spray.Finished += spray.QueueFree;
    }

    // ---- sounds ------------------------------------------------------------------------------------

    private static AudioStreamWav? _plop, _snap, _click;

    /// <summary>The float landing: a short hollow plop.</summary>
    public static AudioStreamWav Plop => _plop ??= Dsp.OneShot(0.25f, 74, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * 420f * t * (1f + t * 8f)) * Mathf.Exp(-t * 28f) * 0.6f
                   + (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 60f) * 0.15f;
        }
        return s;
    });

    /// <summary>The line parting: a high twang that drops away.</summary>
    public static AudioStreamWav Snap => _snap ??= Dsp.OneShot(0.5f, 75, (_, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = Mathf.Sin(Mathf.Tau * 1400f * t * (1f - t * 0.8f)) * Mathf.Exp(-t * 9f) * 0.5f;
        }
        return s;
    });

    /// <summary>One click of the reel's ratchet (played as the line comes in, by the owner).</summary>
    public static AudioStreamWav Click => _click ??= Dsp.OneShot(0.03f, 76, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            s[i] = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 300f) * 0.7f;
        }
        return s;
    });
}
