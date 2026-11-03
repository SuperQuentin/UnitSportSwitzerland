using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.World;

namespace UnitSport.Occasions.Festivals.August1;

/// <summary>
/// The 1 August's Höhenfeuer on this client (#1355, <c>docs/notes/occasions/festival-august1.md</c>): from 21:00 to 05:00 game time, while the festival is on, one
/// summit in four under 2 500 m (<see cref="August1Rules.Lit"/> over the <see cref="PoiCatalogue"/>'s peaks, by their hash: the same on every peer and every year)
/// burns, drawn as flickering emissive billboards in ONE <see cref="MultiMesh"/> out to 15 km (<c>shaders/hoehenfeuer.gdshader</c>: the flicker, the facing and the
/// growth with distance are on the GPU). The set is rebuilt only when the camera has moved 500 m, the catalogue, the hour or the origin changed, never per frame.
/// One pooled light warms the nearest fire within 250 m, and the fires within 3 km lift gliders like a campfire does (<see cref="AirLift.Volumes"/>). The
/// occasion's Decorations facet hides the fires (a player's "Off"); the lift stays, it is world physics.
/// </summary>
public partial class Hoehenfeuer : Node3D, IOriginContainer
{
    public const string NodeName = "Hoehenfeuer";

    /// <summary>The flame's size on its summit, m.</summary>
    private const float Width = 7f, Height = 12f;

    private const long KeyBase = 0x4800000000L;
    private const int MaxLifts = 24;

    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<Camera3D?> _camera;
    private readonly List<Poi> _near = new();
    private readonly HashSet<long> _lifts = new();
    private readonly List<long> _stale = new();
    private MultiMesh _multi = null!;
    private MultiMeshInstance3D _node = null!;
    private OmniLight3D _light = null!;
    private float[] _buffer = [];
    private double _next, _lastE = double.NaN, _lastN;
    private bool _lastOn;
    private PoiCatalogue? _lastCatalogue;
    private int _lastEpoch = int.MinValue, _lastHourKey = -1;
    private FestivalService? _svc;

    public static Hoehenfeuer? Instance { get; private set; }

    /// <summary>A check's salt for <see cref="August1Rules.Lit"/> (0 in the game).</summary>
    public int Salt { get; set; }

    /// <summary>Fires drawn now (the MultiMesh's visible instances).</summary>
    public int Count { get; private set; }

    /// <summary>Whether the fires burn now: the festival is on, it is night, and the decorations are shown.</summary>
    public bool Burning { get; private set; }

    /// <summary>Fires within <see cref="August1Rules.LiftRange"/> that lift gliders now.</summary>
    public int Lifts => _lifts.Count;

    /// <summary>The peaks drawn, nearest first (checks).</summary>
    public IReadOnlyList<Poi> Fires => _fired;

    private readonly List<Poi> _fired = new();

    /// <summary>The MultiMesh's visible instance count (checks: a headless run cannot read the transforms back).</summary>
    public int VisibleCount => _multi.VisibleInstanceCount;

    public int Capacity => _multi.InstanceCount;

    public bool LightOn => _light.Visible;

    public Hoehenfeuer(Func<WorldOrigin?> origin, Func<Camera3D?> camera)
    {
        _origin = origin;
        _camera = camera;
        Name = NodeName;
    }

    public Hoehenfeuer() : this(() => null, () => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        ClearLifts();
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        var quad = new QuadMesh { Size = Vector2.One, CenterOffset = new Vector3(0, 0.5f, 0) };
        quad.Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/hoehenfeuer.gdshader") };
        _multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = quad,
            InstanceCount = August1Rules.MaxFires,
            VisibleInstanceCount = 0,
        };
        _buffer = new float[August1Rules.MaxFires * MultiMeshPack.Stride(_multi)];
        _node = new MultiMeshInstance3D
        {
            Name = "Flames", Multimesh = _multi, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // the flames are placed in world space round the origin: one box over the whole draw range
            CustomAabb = new Aabb(new Vector3(-17000, -4000, -17000), new Vector3(34000, 8000, 34000)),
        };
        AddChild(_node);
        _light = new OmniLight3D { Name = "Glow", OmniRange = 70f, LightEnergy = 2.4f, LightColor = new Color(1f, 0.55f, 0.2f), ShadowEnabled = false, Visible = false };
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        if (now < _next) return;
        _next = now + 0.5;
        _svc ??= FestivalService.Instance;
        var om = OccasionManager.Instance;
        bool festival = _svc?.RunningOf(August1Rules.FestivalId) != null;
        double hour = _svc?.Hour() ?? 12;
        bool night = August1Rules.Burning(hour);
        bool shown = om == null || om.IsActive(August1Rules.FestivalId, OccasionFacets.Decorations);
        Burning = festival && night;
        if (_origin() is not { } origin || _camera() is not { } cam)
        {
            return;
        }
        var (ce, cn) = origin.ToLv95(cam.GlobalPosition);
        if (!Burning)
        {
            if (_lastOn) { Clear(); ClearLifts(); }
            _lastOn = false;
            return;
        }
        var catalogue = PoiCatalogueHost.Current;
        bool moved = double.IsNaN(_lastE) || August1Rules.Dist(ce, cn, (_lastE, _lastN)) > 500;
        int hourKey = (int)(hour * 2);
        if (_lastOn && !moved && catalogue == _lastCatalogue && origin.Epoch == _lastEpoch && hourKey == _lastHourKey && shown == _lastShown)
        {
            UpdateLight(origin, ce, cn);
            return;
        }
        _lastOn = true;
        _lastShown = shown;
        _lastE = ce;
        _lastN = cn;
        _lastCatalogue = catalogue;
        _lastEpoch = origin.Epoch;
        _lastHourKey = hourKey;
        Rebuild(origin, catalogue, ce, cn, shown);
        UpdateLight(origin, ce, cn);
    }

    private bool _lastShown = true;

    /// <summary>The summits burning round (<paramref name="e"/>, <paramref name="n"/>): peaks under 2 500 m that the hash lights, nearest first, at most <see cref="August1Rules.MaxFires"/>.</summary>
    public List<Poi> FiresNear(PoiCatalogue catalogue, double e, double n)
    {
        _near.Clear();
        catalogue.Near(e, n, (float)August1Rules.DrawRange, _near, PoiKind.Peak);
        var lit = new List<Poi>();
        foreach (var p in _near)
            if (August1Rules.Lit(p.Hash, p.Elevation, Salt)) lit.Add(p);
        lit.Sort((a, b) => August1Rules.Dist(a.E, a.N, (e, n)).CompareTo(August1Rules.Dist(b.E, b.N, (e, n))));
        if (lit.Count > August1Rules.MaxFires) lit.RemoveRange(August1Rules.MaxFires, lit.Count - August1Rules.MaxFires);
        return lit;
    }

    private void Rebuild(WorldOrigin origin, PoiCatalogue catalogue, double e, double n, bool shown)
    {
        var fires = FiresNear(catalogue, e, n);
        _fired.Clear();
        _fired.AddRange(fires);
        int stride = MultiMeshPack.Stride(_multi);
        Array.Clear(_buffer);
        int count = shown ? fires.Count : 0;
        for (int i = 0; i < count; i++)
        {
            var p = fires[i];
            var at = origin.ToWorld(p.E, p.N, p.Elevation + 1f);
            MultiMeshPack.Transform(_buffer, i * stride, new Transform3D(Basis.FromScale(new Vector3(Width, Height, 1f)), at));
            // the flicker's phase: a fraction from the POI's hash
            MultiMeshPack.Color(_buffer, i * stride + 12, new Color((p.Hash & 0xFFFF) / 65535f, 0, 0, 0));
        }
        _multi.Buffer = _buffer;
        _multi.VisibleInstanceCount = count;
        Count = count;
        SetLifts(fires, e, n);
    }

    private void Clear()
    {
        _multi.VisibleInstanceCount = 0;
        Count = 0;
        _fired.Clear();
        _light.Visible = false;
    }

    /// <summary>The nearest fire within 250 m warms its surroundings with one light.</summary>
    private void UpdateLight(WorldOrigin origin, double e, double n)
    {
        if (Count > 0 && _fired.Count > 0 && August1Rules.Dist(_fired[0].E, _fired[0].N, (e, n)) < 250)
        {
            _light.GlobalPosition = origin.ToWorld(_fired[0].E, _fired[0].N, _fired[0].Elevation + 5f);
            _light.Visible = true;
        }
        else _light.Visible = false;
    }

    // ---- the fires lift gliders ------------------------------------------------------------------------------------------

    private void SetLifts(List<Poi> fires, double e, double n)
    {
        _stale.Clear();
        _stale.AddRange(_lifts);
        int set = 0;
        foreach (var p in fires)
        {
            if (set >= MaxLifts || August1Rules.Dist(p.E, p.N, (e, n)) > August1Rules.LiftRange) break;
            long key = KeyBase | (long)(p.Hash & 0xFFFFFFFFUL);
            AirLift.Volumes.Set(key, p.E, p.N, p.Elevation, August1Rules.LiftRadius, August1Rules.LiftTop, August1Rules.LiftSpeed);
            _lifts.Add(key);
            _stale.Remove(key);
            set++;
        }
        foreach (long key in _stale)
        {
            AirLift.Volumes.Remove(key);
            _lifts.Remove(key);
        }
    }

    private void ClearLifts()
    {
        foreach (long key in _lifts) AirLift.Volumes.Remove(key);
        _lifts.Clear();
    }
}
