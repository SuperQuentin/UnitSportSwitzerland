using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Occasions;

/// <summary>The kinds of creature an occasion can put in the air around the player.</summary>
public enum CritterKind
{
    /// <summary>Circling the nearest town's centre (its church) at roof height and above.</summary>
    Bat,
    /// <summary>A loose flock wheeling high over the ground near the player.</summary>
    Crow,
    /// <summary>A pale light drifting between the trees, fading in and out.</summary>
    Wisp,
}

/// <summary>
/// One group of creatures: what, how many, and when — as a band of <see cref="World.DayNight.Night"/>
/// (0 day .. 1 night), so bats come out at dusk and wisps only in full dark.
/// </summary>
public sealed record Flock(CritterKind Kind, int Count, float NightFrom, float NightTo);

/// <summary>
/// Flies the running occasions' flocks (part of the Atmosphere facet). Each flock is one
/// MultiMesh in world space whose few dozen transforms are set every frame — cheap at this count,
/// and every creature is placed from its own index and the clock, so there is no state to keep.
/// Purely cosmetic and local: nobody else needs to see the same bat. Client only.
/// </summary>
public partial class OccasionCreatures : Node3D
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly Func<Node3D?> _camera;
    private readonly Dictionary<(string, CritterKind), MultiMeshInstance3D> _live = new();
    private readonly Dictionary<CritterKind, Vector3[]> _anchors = new();
    private readonly Dictionary<CritterKind, double[]> _born = new();
    private ShaderMaterial _material = null!;
    private double _t;
    private static readonly bool LogFlocks = OS.GetCmdlineUserArgs().Contains("--decorlog");
    private double _logIn;
    private Vector3 _crowCentre;
    private bool _crowPlaced;

    public OccasionCreatures(ChunkManager chunks, WorldOrigin origin, Func<Node3D?> camera)
    {
        Name = "OccasionCreatures";
        _chunks = chunks;
        _origin = origin;
        _camera = camera;
    }

    public OccasionCreatures() : this(null!, null!, () => null) { }

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ps1_prop.gdshader") };
        _material.SetShaderParameter("flicker", 0.6f);
        FogUniforms.Apply(_material);
    }

    public override void _Process(double delta)
    {
        _t += delta;
        var cam = _camera()?.GlobalPosition;
        float night = World.DayNight.Instance?.Night ?? 0f;

        var wanted = new HashSet<(string, CritterKind)>();
        if (cam is { } eye && OccasionManager.Instance is { } m)
            foreach (var a in m.Active.Where(a => a.Has(OccasionFacets.Atmosphere)))
                foreach (var flock in a.Content.Flocks)
                {
                    if (night < flock.NightFrom || night > flock.NightTo) continue;
                    var key = (a.Id, flock.Kind);
                    wanted.Add(key);
                    if (!_live.TryGetValue(key, out var node)) _live[key] = node = Spawn(flock);
                    Fly(node, flock, eye, night, (float)delta);
                }

        if (LogFlocks && (_logIn -= delta) <= 0)
        {
            _logIn = 3;
            GD.Print($"[creatures] night {night:F2} cam {cam}: " + string.Join("; ", _live.Select(kv =>
                $"{kv.Key.Item2} x{kv.Value.Multimesh.InstanceCount} first at {kv.Value.Multimesh.GetInstanceTransform(0).Origin}")));
        }

        foreach (var key in _live.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _live[key].QueueFree();
            _live.Remove(key);
        }
    }

    // ---- meshes --------------------------------------------------------------------------------

    private static readonly Color Leather = PropColors.Matte(0.30f, 0.24f, 0.32f);
    private static readonly Color Feather = PropColors.Matte(0.06f, 0.06f, 0.07f);

    private static ArrayMesh Mesh(CritterKind kind)
    {
        var s = new MeshScratch();
        switch (kind)
        {
            case CritterKind.Bat:
                s.Tube(new Vector3(0, 0, -0.06f), new Vector3(0, 0, 0.07f), 0.035f, 0.02f, Leather, 5);
                s.Box(new Vector3(-0.17f, 0.01f, 0), new Vector3(0.30f, 0.01f, 0.12f), Leather, new Basis(Vector3.Forward, -0.15f));
                s.Box(new Vector3(0.17f, 0.01f, 0), new Vector3(0.30f, 0.01f, 0.12f), Leather, new Basis(Vector3.Forward, 0.15f));
                s.Box(new Vector3(-0.03f, 0.04f, 0.07f), new Vector3(0.02f, 0.04f, 0.01f), Leather);   // ears
                s.Box(new Vector3(0.03f, 0.04f, 0.07f), new Vector3(0.02f, 0.04f, 0.01f), Leather);
                break;
            case CritterKind.Crow:
                s.Tube(new Vector3(0, 0, -0.20f), new Vector3(0, 0, 0.16f), 0.05f, 0.04f, Feather, 5);
                s.Tube(new Vector3(0, 0.01f, 0.16f), new Vector3(0, 0, 0.25f), 0.035f, 0.005f, Feather, 4);   // beak
                s.Box(new Vector3(-0.26f, 0, 0), new Vector3(0.46f, 0.012f, 0.16f), Feather);
                s.Box(new Vector3(0.26f, 0, 0), new Vector3(0.46f, 0.012f, 0.16f), Feather);
                s.Box(new Vector3(0, 0, -0.26f), new Vector3(0.14f, 0.01f, 0.12f), Feather);          // tail
                break;
            case CritterKind.Wisp:
                var light = new Color(0.72f, 0.95f, 1.0f, 1f);
                s.Box(Vector3.Zero, new Vector3(0.18f, 0.18f, 0.18f), light, new Basis(new Vector3(1, 1, 0).Normalized(), 0.8f));
                s.Box(Vector3.Zero, new Vector3(0.11f, 0.30f, 0.11f), light, new Basis(Vector3.Up, 0.6f));
                break;
        }
        return s.Build();
    }

    private MultiMeshInstance3D Spawn(Flock flock)
    {
        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = Mesh(flock.Kind),
            InstanceCount = flock.Count,
        };
        var node = new MultiMeshInstance3D
        {
            Name = flock.Kind.ToString(),
            Multimesh = multi,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // instances are placed in world space and move every frame: never cull the lot
            CustomAabb = new Aabb(new Vector3(-1e5f, -1e4f, -1e5f), new Vector3(2e5f, 2e4f, 2e5f)),
            TopLevel = true,
        };
        AddChild(node);
        return node;
    }

    // ---- flight --------------------------------------------------------------------------------

    private static float H(int i, int salt) => OccasionHash.Unit(i, salt, 0, 0xB47);

    private void Fly(MultiMeshInstance3D node, Flock flock, Vector3 eye, float night, float dt)
    {
        var multi = node.Multimesh;
        switch (flock.Kind)
        {
            case CritterKind.Bat: FlyBats(multi, eye); break;
            case CritterKind.Crow: FlyCrows(multi, eye, dt); break;
            case CritterKind.Wisp: FlyWisps(multi, eye); break;
        }
    }

    /// <summary>Bats wheel round the nearest town's centre, each on its own radius, height and speed.</summary>
    private void FlyBats(MultiMesh multi, Vector3 eye)
    {
        var (e, n) = _origin.ToLv95(eye);
        var town = OccasionTowns.Near(e, n, 1500).Select(t => (OccasionTowns.Town?)t).FirstOrDefault();
        for (int i = 0; i < multi.InstanceCount; i++)
        {
            if (town is not { } tw) { multi.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.Zero), eye)); continue; }
            var centre = _origin.ToWorld(tw.E, tw.N, 0);
            if (_chunks.TryGetHeight(centre, out float g)) centre.Y = g;
            float r = 8f + H(i, 1) * 20f, speed = (0.7f + H(i, 2) * 0.9f) * (H(i, 3) < 0.5f ? 1 : -1);
            float a = (float)_t * speed + H(i, 4) * Mathf.Tau;
            float h = 16f + H(i, 5) * 22f + Mathf.Sin((float)_t * 1.7f + i) * 2.5f;
            float wobble = Mathf.Sin((float)_t * 3.1f + i * 1.3f) * 2.2f;
            var p = centre + new Vector3(Mathf.Cos(a) * (r + wobble), h, Mathf.Sin(a) * (r + wobble));
            var tangent = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a)) * Mathf.Sign(speed);
            // a flap is the wings folding in: scale across the span, fast and erratic
            float flap = 0.35f + 0.65f * Mathf.Abs(Mathf.Sin((float)_t * (11f + H(i, 6) * 4f) + i));
            var basis = TileContext.Facing(tangent, 0f, 4.0f) * Basis.FromScale(new Vector3(flap, 1, 1));
            multi.SetInstanceTransform(i, new Transform3D(basis, p));
            multi.SetInstanceCustomData(i, new Color(0, 0, 0, 0));
        }
    }

    /// <summary>A loose flock wheeling 30-45 m up over a point that drifts around the player.</summary>
    private void FlyCrows(MultiMesh multi, Vector3 eye, float dt)
    {
        var target = eye + new Vector3(Mathf.Sin((float)_t * 0.02f) * 70f, 0, Mathf.Cos((float)_t * 0.017f) * 70f);
        _crowCentre = _crowPlaced ? _crowCentre.Lerp(target, 1f - Mathf.Exp(-0.05f * dt)) : target;
        _crowPlaced = true;
        float ground = _chunks.TryGetHeight(_crowCentre, out float g) ? g : eye.Y - 60f;
        for (int i = 0; i < multi.InstanceCount; i++)
        {
            float r = 18f + H(i, 11) * 30f, speed = 0.18f + H(i, 12) * 0.12f;
            float a = (float)_t * speed + H(i, 13) * Mathf.Tau;
            var p = new Vector3(_crowCentre.X + Mathf.Cos(a) * r, ground + 30f + H(i, 14) * 15f + Mathf.Sin((float)_t * 0.5f + i) * 3f,
                _crowCentre.Z + Mathf.Sin(a) * r);
            var tangent = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a));
            // long glides broken by a few wingbeats
            float beat = Mathf.Sin((float)_t * 0.6f + i * 2.1f) > 0.3f ? 0.55f + 0.45f * Mathf.Abs(Mathf.Sin((float)_t * 7f + i)) : 1f;
            var basis = TileContext.Facing(tangent, 0f, 2.2f) * Basis.FromScale(new Vector3(beat, 1, 1))
                * new Basis(Vector3.Forward, -0.25f);   // banked into the turn
            multi.SetInstanceTransform(i, new Transform3D(basis, p));
            multi.SetInstanceCustomData(i, new Color(0, 0, 0, 0));
        }
    }

    /// <summary>
    /// Wisps each haunt a spot in the woods near the player for ~25 s — fading in, bobbing,
    /// drifting — then go out and reappear elsewhere. Only wooded cover is haunted.
    /// </summary>
    private void FlyWisps(MultiMesh multi, Vector3 eye)
    {
        int n = multi.InstanceCount;
        if (!_anchors.TryGetValue(CritterKind.Wisp, out var anchors)) _anchors[CritterKind.Wisp] = anchors = new Vector3[n];
        if (!_born.TryGetValue(CritterKind.Wisp, out var born)) _born[CritterKind.Wisp] = born = Enumerable.Repeat(-1.0, n).ToArray();
        const double Life = 25;
        for (int i = 0; i < n; i++)
        {
            double age = _t - born[i];
            // a wisp with no wood to haunt keeps looking, rather than waiting out a whole life unseen
            bool homeless = anchors[i].Y < -1000f;
            if (born[i] < 0 || age > Life || homeless || anchors[i].DistanceTo(eye) > 120f)
            {
                born[i] = _t - GD.Randf() * 3.0;
                anchors[i] = WoodedSpot(eye) ?? eye with { Y = -1e4f };
                age = _t - born[i];
            }
            float life = (float)(age / Life);
            float fade = Mathf.SmoothStep(0f, 0.15f, life) * (1f - Mathf.SmoothStep(0.8f, 1f, life));
            var p = anchors[i] + new Vector3(
                Mathf.Sin((float)_t * 0.31f + i) * 2.5f,
                1.2f + Mathf.Sin((float)_t * 1.3f + i * 2f) * 0.35f,
                Mathf.Cos((float)_t * 0.27f + i * 1.7f) * 2.5f);
            var basis = new Basis(Vector3.Up, (float)_t * 0.8f + i).Scaled(Vector3.One * Mathf.Max(2.5f * fade, 0.001f));
            multi.SetInstanceTransform(i, new Transform3D(basis, p));
            multi.SetInstanceCustomData(i, new Color(H(i, 21), 1.4f * fade, 0, 0));
        }
    }

    private Vector3? WoodedSpot(Vector3 eye)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            float a = GD.Randf() * Mathf.Tau, d = 18f + GD.Randf() * 55f;
            var p = eye + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
            if (_chunks.TryGetCover(p, out var c) && CoverFormat.IsWooded(c) && _chunks.TryGetHeight(p, out float y))
                return p with { Y = y };
        }
        return null;
    }
}
