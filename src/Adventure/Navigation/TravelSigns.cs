using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The travel signs in the world (#1226, navigation plan 5.2), on the client: the nearest
/// <see cref="Count"/> travel points within <see cref="RangeM"/> get their kind's sign, lit when unlocked, dark when
/// not. One <see cref="MultiMeshInstance3D"/> per kind and state, the meshes built once (<see cref="NavMeshBuilder"/>);
/// instances rewritten only when the set, its unlocked state, the ground or the origin tile change (checked once a
/// second). <see cref="TryInteract"/> (E / Y, a VR grip on the board) within <see cref="ReachM"/> opens the departure
/// board (<see cref="DepartureScreen"/>). Shown while adventuring.
/// </summary>
public partial class TravelSigns : Node3D
{
    public const float RangeM = 400, ReachM = 4;
    public const int Count = 6;
    private const double RefreshEvery = 1.0;

    public static TravelSigns? Instance { get; private set; }

    private readonly Func<FootPlayer?> _me;
    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<Vector3, float?> _ground;
    private readonly MultiMeshInstance3D[] _layers = new MultiMeshInstance3D[10];
    private readonly List<Poi> _near = new();
    private readonly List<(Poi Poi, double D)> _nearest = new();
    private readonly List<(Poi Poi, Vector3 At)> _placed = new();
    private string _shape = "";
    private double _next;

    public TravelSigns(Func<FootPlayer?> me, Func<WorldOrigin?> origin, Func<Vector3, float?> ground)
    {
        _me = me;
        _origin = origin;
        _ground = ground;
        Name = "TravelSigns";
    }

    public TravelSigns() : this(() => null, () => null, _ => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (AdventureService.Instance is { } svc) svc.SectionChanged -= OnSection;
    }

    public override void _Ready()
    {
        var material = PoiMarkerMeshBuilder.Material();
        foreach (var kind in new[] { TravelKind.Rail, TravelKind.Bus, TravelKind.Ship, TravelKind.CableCar })
            foreach (bool lit in new[] { false, true })
            {
                var m = new MultiMeshInstance3D
                {
                    Name = $"{kind}{(lit ? "Lit" : "Dark")}",
                    Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = NavMeshBuilder.Sign(kind, lit) },
                    MaterialOverride = material,
                };
                AddChild(m);
                _layers[(int)kind * 2 + (lit ? 1 : 0)] = m;
            }
        if (AdventureService.Instance is { } svc) svc.SectionChanged += OnSection;
    }

    private void OnSection(string id)
    {
        if (id == TravelBook.Section.Id) _next = 0;
    }

    /// <summary>The signs standing now (checks).</summary>
    public IReadOnlyList<(Poi Poi, Vector3 At)> Placed => _placed;

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        if (now < _next) return;
        _next = now + RefreshEvery;
        Refresh();
    }

    private void Refresh()
    {
        var me = _me();
        _nearest.Clear();
        if (PlacesService.Instance is { Catalogue.Count: > 0 } places && me != null && IsInstanceValid(me) && _origin() is { } origin && AdventureLog.Adventuring)
        {
            var at = me.Global;
            places.Catalogue.Near(at.E, at.N, RangeM, _near);
            foreach (var poi in _near)
                if (TravelNetwork.IsPoint(poi)) _nearest.Add((poi, TravelNetwork.Distance(poi, at.E, at.N)));
            _nearest.Sort((a, b) => a.D.CompareTo(b.D));
            if (_nearest.Count > Count) _nearest.RemoveRange(Count, _nearest.Count - Count);
            var book = AdventureTravel.Instance?.MyBook;
            var sb = new System.Text.StringBuilder();
            foreach (var (poi, _) in _nearest)
                sb.Append(poi.Id).Append(book?.Has(poi.Id) == true ? '+' : '-').Append(_ground(origin.ToWorld(poi.E, poi.N, 0)) != null ? 'g' : '.');
            sb.Append((int)(origin.E / 1000)).Append('/').Append((int)(origin.N / 1000));
            string shape = sb.ToString();
            if (shape == _shape) return;
            _shape = shape;
            Place(origin, book);
            return;
        }
        if (_shape.Length == 0) return;
        _shape = "";
        _placed.Clear();
        foreach (var l in _layers) if (l != null) l.Multimesh.InstanceCount = 0;
    }

    private void Place(WorldOrigin origin, TravelBook? book)
    {
        _placed.Clear();
        Span<int> counts = stackalloc int[10];
        foreach (var (poi, _) in _nearest)
        {
            var w = origin.ToWorld(poi.E, poi.N, poi.Elevation);
            if (_ground(w) is { } g) w.Y = g;
            _placed.Add((poi, w - GlobalPosition));
            counts[Layer(poi, book)]++;
        }
        for (int k = 2; k < 10; k++) _layers[k].Multimesh.InstanceCount = counts[k];
        Span<int> at = stackalloc int[10];
        foreach (var (poi, local) in _placed)
        {
            int k = Layer(poi, book);
            // a stable turn per point, so signs do not all face north
            float yaw = (float)(poi.Hash % 360) * Mathf.Pi / 180f;
            _layers[k].Multimesh.SetInstanceTransform(at[k]++, new Transform3D(new Basis(Vector3.Up, yaw), local));
        }
    }

    private static int Layer(Poi poi, TravelBook? book) => (int)TravelNetwork.KindOf(poi.Kind) * 2 + (book?.Has(poi.Id) == true ? 1 : 0);

    /// <summary>The sign within reach of a body, or null.</summary>
    public Poi? InReach(FootPlayer p)
    {
        if (_placed.Count == 0 || p.Indoors) return null;
        var at = p.GlobalPosition - GlobalPosition;
        Poi? best = null;
        float bd = ReachM * ReachM;
        foreach (var (poi, local) in _placed)
        {
            var d = local - at;
            if (Mathf.Abs(d.Y) > 4f) continue;
            float h = d.X * d.X + d.Z * d.Z;
            if (h <= bd) { bd = h; best = poi; }
        }
        return best;
    }

    /// <summary>E / Y / a VR hand at a sign: the departure board. False when none is in reach.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance?.InReach(p) is not { } poi) return false;
        DepartureScreen.Open(poi);
        return true;
    }

    private (Poi? Poi, InputDevice Device, bool Lit) _promptKey;
    private string? _prompt;

    /// <summary>"[E] Departures: Brig" at a sign; null away from one. Built only when it changes.</summary>
    public static string? PromptFor(FootPlayer p)
    {
        if (Instance is not { } s || s.InReach(p) is not { } poi) return null;
        bool lit = AdventureTravel.Instance?.MyBook.Has(poi.Id) == true;
        var key = (poi, PlayerInput.HintDevice, lit);
        if (s._prompt != null && key == s._promptKey) return s._prompt;
        s._promptKey = key;
        return s._prompt = InputHints.Prompt(PlayerInput.InteractMount, lit ? $"Departures: {poi.Name}" : $"{poi.Name} (unlocking…)");
    }
}
