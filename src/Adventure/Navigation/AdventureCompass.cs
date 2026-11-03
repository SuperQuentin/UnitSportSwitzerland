using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The free-roam compass (#1226, navigation plan 1): the shared <see cref="CompassStrip"/> at the top centre while
/// adventuring (not in a Battle Royale round, which draws its own; not in the headset, where the watch's bezel is the
/// compass), on foot, mounted, driving or flying, when <c>Settings > Gameplay > Compass</c> is on. Markers in priority
/// order: the tracked target (amber diamond, "1.2 km ▲340 m"), party members (green, name and distance), pins (their
/// icon), discovered places within 1.5 km (faint, their name only near the caret; at most 5, the query at 2 Hz).
/// Labels are rebuilt only when a rounded distance changes (<see cref="NavFormat.Step"/>); it fades to 30 % when
/// nothing changes for 6 s.
/// </summary>
public partial class AdventureCompass : CanvasLayer, ICompassSource
{
    private const float PlacesM = 1500f, PlaceLabelDeg = 12f;
    private const int MaxPlaces = 5;
    private const double PlacesEvery = 0.5;

    public static AdventureCompass? Instance { get; private set; }

    /// <summary>Where the local body is (LV95), set by the client world.</summary>
    public static Func<GlobalPos?>? Me { get; set; }
    /// <summary>Where another player is (its replicated body), set by the client world.</summary>
    public static Func<long, GlobalPos?>? PeerAt { get; set; }

    public CompassStrip Strip { get; private set; } = null!;

    private readonly Dictionary<int, (int Step, int Climb, string Text)> _labels = new();
    private readonly List<Poi> _near = new();
    private readonly (Poi Poi, double D)[] _places = new (Poi, double)[MaxPlaces];
    private int _placeCount;
    private double _nextPlaces;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Name = "AdventureCompass";
        Layer = 5;
        Strip = new CompassStrip(this) { Name = "Strip" };
        AddChild(Strip);
    }

    public bool Shown => GameSettings.Current.AdventureCompass && AdventureLog.Adventuring && !Permissions.InMatch
        && !XR.XrSession.Active && Me?.Invoke() != null && GameShell.Instance is not { Top: not null };

    public Vector2? Heading
    {
        get
        {
            if (GetViewport()?.GetCamera3D() is not { } cam) return null;
            var f = -cam.GlobalBasis.Z;
            return new Vector2(f.X, -f.Z);
        }
    }

    public bool Declutter => true;
    public bool Fades => true;

    private string Label(int key, double m, double climb, string name = "")
    {
        int step = NavFormat.Step(m), c = double.IsNaN(climb) || Math.Abs(climb) <= 30 ? 0 : (int)Math.Round(climb / 10);
        if (_labels.TryGetValue(key, out var l) && l.Step == step && l.Climb == c) return l.Text;
        string text = NavFormat.Label(name, m, double.IsNaN(climb) ? 0 : climb, 0);
        _labels[key] = (step, c, text);
        return text;
    }

    public int Fill(CompassMarker[] into)
    {
        if (Me?.Invoke() is not { } me) return 0;
        int n = 0;
        void Add(int key, double e, double nn, Color color, MarkerShape shape, int row, byte icon, string label)
        {
            if (n >= into.Length) return;
            into[n++] = new CompassMarker
            {
                Key = key, Toward = new Vector2((float)(e - me.E), (float)(nn - me.N)), Color = color, Shape = shape, Row = (byte)row,
                Icon = icon, Label = label,
            };
        }
        static double Dist(GlobalPos me, double e, double n) => Math.Sqrt((e - me.E) * (e - me.E) + (n - me.N) * (n - me.N));

        // 1. the tracked target
        if (NavTarget.Current is { } t)
        {
            double d = Dist(me, t.E, t.N);
            double climb = NavTarget.Alt is { } alt ? alt - me.Alt : double.NaN;
            string label = t.RadiusM > 0 && d <= t.RadiusM ? "in the area" : Label(1, d, climb);
            Add(1, t.E, t.N, UiTheme.Amber, MarkerShape.Diamond, 0, 0, label);
        }
        // 2. the party
        if (Net.Parties.Client is { InParty: true } party)
        {
            long self = Multiplayer.GetUniqueId();
            foreach (var (peer, name) in party.Members)
            {
                if (peer == self || PeerAt?.Invoke(peer) is not { } at) continue;
                Add(1000 + (int)(peer % 1000), at.E, at.N, BattleRoyale.BrMapDraw.Mate, MarkerShape.Down, 1, 0,
                    Label(1000 + (int)(peer % 1000), Dist(me, at.E, at.N), double.NaN, name));
            }
        }
        // 3. pins: mine, then the party's
        if (AdventureTravel.Instance is { } travel)
        {
            foreach (var pin in travel.MyPins.Pins)
                Add(2000 + pin.Id, pin.E, pin.N, NavIcons.ColorOf(pin.Icon), MarkerShape.Icon, 2, pin.Icon, Label(2000 + pin.Id, Dist(me, pin.E, pin.N), double.NaN));
            foreach (var (owner, (_, pins)) in travel.PartyPins)
                foreach (var pin in pins)
                {
                    int key = 3000 + (int)(owner % 100) * 64 + pin.Id % 64;
                    Add(key, pin.E, pin.N, NavIcons.ColorOf(pin.Icon), MarkerShape.Icon, 2, pin.Icon, Label(key, Dist(me, pin.E, pin.N), double.NaN));
                }
        }
        // 3b. the world events running near: a cow loose, a lost hiker, an avalanche, smoke (#1339)
        if (Errands.WorldEvents.Instance is { } events)
            foreach (var (key, ee, nn2, kind, name) in events.Markers)
                Add(key, ee, nn2, Errands.WorldEvents.ColorOf(kind), MarkerShape.Diamond, 1, 0, Label(key, Dist(me, ee, nn2), double.NaN, name));
        // 4. discovered places nearby, nearest first, their name near the caret only
        RefreshPlaces(me);
        float heading = Heading is { } h ? CompassMath.Bearing(h) : 0;
        for (int i = 0; i < _placeCount; i++)
        {
            var (poi, _) = _places[i];
            float rel = CompassMath.Relative(CompassMath.Bearing(new Vector2((float)(poi.E - me.E), (float)(poi.N - me.N))), heading);
            Add(5000 + i, poi.E, poi.N, new Color(1, 1, 1, 0.6f), MarkerShape.Place, 3, (byte)poi.Kind, Math.Abs(rel) <= PlaceLabelDeg ? poi.Name : "");
        }
        return n;
    }

    private void RefreshPlaces(GlobalPos me)
    {
        double now = GameClock.Now;
        if (now < _nextPlaces) return;
        _nextPlaces = now + PlacesEvery;
        _placeCount = 0;
        if (PlacesService.Instance is not { } places || AdventureService.Instance is not { } svc || !svc.Mine.Has(PlacesProgress.Section.Id)) return;
        var mine = svc.Mine.Get(PlacesProgress.Section);
        places.Catalogue.Near(me.E, me.N, PlacesM, _near);
        foreach (var poi in _near)
        {
            if (poi.Outline != null || !mine.Found.ContainsKey(poi.Id)) continue;
            double d = Math.Sqrt((poi.E - me.E) * (poi.E - me.E) + (poi.N - me.N) * (poi.N - me.N));
            if (d < 15) continue;
            // keep the nearest few: insertion into a short sorted array
            if (_placeCount == MaxPlaces && d >= _places[MaxPlaces - 1].D) continue;
            int at = _placeCount < MaxPlaces ? _placeCount++ : MaxPlaces - 1;
            while (at > 0 && _places[at - 1].D > d) { _places[at] = _places[at - 1]; at--; }
            _places[at] = (poi, d);
        }
    }
}
