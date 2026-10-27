using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

/// <summary>
/// The places' markers in the world (#1224, places plan 5), on the client: within 1.5 km of the player,
/// a summit cross on crossed peaks, a telescope at the other viewpoints, a stamp post at landmarks, and a
/// small flag over the ones this player has synced or stamped. One <see cref="MultiMeshInstance3D"/> per kind;
/// the meshes are built once (<see cref="PoiMarkerMeshBuilder"/>) and the instances rewritten only when the
/// set in range or its places change (checked once a second, never per frame). A plain child of the world,
/// so the origin shifter moves it.
/// <para>
/// <see cref="TryInteract"/> (E / Y, a VR hand gripping it) within <see cref="ReachM"/> asks the places
/// service to use it: stamp, sync the view, and <c>Interacted</c> for quests.
/// </para>
/// </summary>
public partial class PoiMarkers : Node3D
{
    public const float RangeM = 1500, ReachM = 3;
    private const double RefreshEvery = 1.0;

    public static PoiMarkers? Instance { get; private set; }

    private enum Kind { Cross, Telescope, Post }

    private readonly Func<FootPlayer?> _me;
    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<Vector3, float?> _ground;
    private readonly MultiMeshInstance3D[] _layers = new MultiMeshInstance3D[3];
    private MultiMeshInstance3D _flags = null!;
    private readonly List<Poi> _near = new(), _shown = new();
    private readonly List<(Poi Poi, Vector3 At)> _placed = new();
    private string _shape = "";
    private double _next;

    public PoiMarkers(Func<FootPlayer?> me, Func<WorldOrigin?> origin, Func<Vector3, float?> ground)
    {
        _me = me;
        _origin = origin;
        _ground = ground;
        Name = "PoiMarkers";
    }

    public PoiMarkers() : this(() => null, () => null, _ => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        var material = PoiMarkerMeshBuilder.Material();
        MultiMeshInstance3D Layer(string name, Mesh mesh)
        {
            var m = new MultiMeshInstance3D
            {
                Name = name,
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh },
                MaterialOverride = material,
            };
            AddChild(m);
            return m;
        }
        _layers[(int)Kind.Cross] = Layer("Crosses", PoiMarkerMeshBuilder.Cross());
        _layers[(int)Kind.Telescope] = Layer("Telescopes", PoiMarkerMeshBuilder.Telescope());
        _layers[(int)Kind.Post] = Layer("StampPosts", PoiMarkerMeshBuilder.StampPost());
        _flags = Layer("Flags", PoiMarkerMeshBuilder.Flag());
        if (AdventureService.Instance is { } svc) svc.SectionChanged += OnSection;
    }

    private void OnSection(string id)
    {
        if (id == PlacesProgress.Section.Id) _next = 0;
    }

    private static Kind KindOf(Poi poi) =>
        poi.Kind == PoiKind.SummitCross || poi.Is(PoiFlags.Cross) ? Kind.Cross
        : poi.Is(PoiFlags.Viewpoint) ? Kind.Telescope : Kind.Post;

    /// <summary>The markers in range now (checks).</summary>
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
        var places = PlacesService.Instance;
        var me = _me();
        _shown.Clear();
        if (places is { Catalogue.Count: > 0 } && me != null && IsInstanceValid(me) && _origin() is { } origin && Ui.AdventureLog.Adventuring)
        {
            var at = me.Global;
            places.Catalogue.Near(at.E, at.N, RangeM, _near);
            foreach (var poi in _near)
                if (PlacesService.HasMarker(places.Catalogue, poi)) _shown.Add(poi);
            var mine = AdventureService.Instance?.Mine.Get(PlacesProgress.Section);
            // the shape: which POIs, done or not, and whether the ground under each is known yet
            var sb = new System.Text.StringBuilder();
            foreach (var poi in _shown)
            {
                bool done = mine != null && (mine.Synced.Contains(poi.Id) || !poi.Is(PoiFlags.Viewpoint) && mine.Found.ContainsKey(poi.Id));
                bool ground = _ground(origin.ToWorld(poi.E, poi.N, 0)) != null;
                sb.Append(poi.Id).Append(done ? '+' : '-').Append(ground ? 'g' : '.');
            }
            sb.Append((int)(origin.E / 1000)).Append('/').Append((int)(origin.N / 1000));
            string shape = sb.ToString();
            if (shape == _shape) return;
            _shape = shape;
            Place(origin, mine);
            return;
        }
        if (_shape.Length == 0) return;
        _shape = "";
        _placed.Clear();
        foreach (var l in _layers) l.Multimesh.InstanceCount = 0;
        _flags.Multimesh.InstanceCount = 0;
    }

    private void Place(WorldOrigin origin, PlacesProgress? mine)
    {
        _placed.Clear();
        var counts = new int[3];
        int flags = 0;
        foreach (var poi in _shown)
        {
            var w = origin.ToWorld(poi.E, poi.N, poi.Elevation);
            if (_ground(w) is { } g) w.Y = g;
            _placed.Add((poi, w - GlobalPosition));
            counts[(int)KindOf(poi)]++;
        }
        for (int k = 0; k < 3; k++) _layers[k].Multimesh.InstanceCount = counts[k];
        var at = new int[3];
        var flagged = new List<Vector3>();
        foreach (var (poi, local) in _placed)
        {
            var kind = KindOf(poi);
            // a stable turn per place, so markers do not all face north
            float yaw = (float)(poi.Hash % 360) * Mathf.Pi / 180f;
            _layers[(int)kind].Multimesh.SetInstanceTransform(at[(int)kind]++, new Transform3D(new Basis(Vector3.Up, yaw), local));
            bool done = mine != null && (mine.Synced.Contains(poi.Id) || !poi.Is(PoiFlags.Viewpoint) && mine.Found.ContainsKey(poi.Id));
            if (done) flagged.Add(local + new Vector3(0, kind == Kind.Cross ? 3.4f : kind == Kind.Post ? 2.2f : 1.5f, 0));
        }
        _flags.Multimesh.InstanceCount = flagged.Count;
        foreach (var f in flagged) _flags.Multimesh.SetInstanceTransform(flags++, new Transform3D(Basis.Identity, f));
    }

    /// <summary>The marker within reach of a body, or null.</summary>
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

    /// <summary>E / Y / a VR hand at a marker: uses it. False when none is in reach.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        // a travel sign (#1226) first: its board
        if (Navigation.TravelSigns.TryInteract(p)) return true;
        if (Instance?.InReach(p) is not { } poi || PlacesService.Instance is not { } places) return false;
        places.AskInteract(poi.Id);
        return true;
    }

    private (Poi? Poi, InputDevice Device) _promptKey;
    private string? _prompt;

    /// <summary>"[E] Look out" at a viewpoint, "[E] Stamp" at a stamp post; null away from one. Built only when it changes.</summary>
    public static string? PromptFor(FootPlayer p)
    {
        if (Navigation.TravelSigns.PromptFor(p) is { } sign) return sign;
        if (Instance is not { } m || m.InReach(p) is not { } poi) return null;
        var key = (poi, PlayerInput.HintDevice);
        if (m._prompt != null && key == m._promptKey) return m._prompt;
        m._promptKey = key;
        string what = poi.Is(PoiFlags.Viewpoint) ? $"Look out from {poi.Name}" : $"Stamp: {poi.Name}";
        return m._prompt = InputHints.Prompt(PlayerInput.InteractMount, what);
    }
}
