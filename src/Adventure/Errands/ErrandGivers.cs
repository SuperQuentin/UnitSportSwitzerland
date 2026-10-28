using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// The errand givers as figures (#1267 part 2): the givers within 400 m of the camera (freed past 550 m), each a
/// <see cref="SkinnedFigure"/> posed once, standing at the door side its place gave it. A plain child of the world, so the
/// origin shifter moves it. Refreshed once a second; within 8 m a figure turns toward the player at 10 Hz (a transform, never a
/// mesh). There is no collision on any peer, so players and bots walk through. A card glyph floats over a giver with a card the
/// player has not taken (amber), a green hand over one a hand-over waits at. E / Y / a VR hand within 3 m talks
/// (<see cref="TryInteract"/>).
/// </summary>
public partial class ErrandGivers : Node3D
{
    public const float ShowM = 400, FreeM = 550, ReachM = 3, TurnM = 8;
    private const double RefreshEvery = 1.0, TurnEvery = 0.1;

    public static ErrandGivers? Instance { get; private set; }

    private sealed class Entry
    {
        public required Giver Giver;
        public required Node3D Holder;
        public required SkinnedFigure Figure;
        public required Sprite3D Icon;
        public bool Grounded;
        public int Mark = -1;
        public float Yaw;
        public List<Offer> Cards = new();
    }

    private readonly Func<FootPlayer?> _me;
    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<Vector3, float?> _ground;
    private readonly Dictionary<string, Entry> _shown = new();
    private readonly List<Giver> _near = new();
    private readonly HashSet<string> _live = new();
    private readonly List<string> _drop = new();
    private double _next, _nextTurn;
    private bool _dirty = true;
    private int _day;
    private ShaderMaterial? _material;

    public ErrandGivers(Func<FootPlayer?> me, Func<WorldOrigin?> origin, Func<Vector3, float?> ground)
    {
        _me = me;
        _origin = origin;
        _ground = ground;
        Name = "ErrandGivers";
    }

    public ErrandGivers() : this(() => null, () => null, _ => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _Ready()
    {
        if (AdventureService.Instance is { } svc) svc.SectionChanged += OnSection;
        GiverHost.Changed += OnGivers;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (AdventureService.Instance is { } svc) svc.SectionChanged -= OnSection;
        GiverHost.Changed -= OnGivers;
    }

    private void OnSection(string id)
    {
        if (id == ErrandsProgress.Section.Id || id == QuestsProgress.Section.Id) _dirty = true;
    }

    private void OnGivers() => ClearAll();

    /// <summary>The figures shown now (checks).</summary>
    public int ShownCount => _shown.Count;

    public IEnumerable<Giver> Shown => _shown.Values.Select(e => e.Giver);

    // ---- looks --------------------------------------------------------------------------------------

    /// <summary>A giver's look: a rider's palette by its id, and the garment of its trade (the Senn's cap, the warden's guide hat, the station's vest).</summary>
    public static FigureLook LookOf(string id, GiverKind kind)
    {
        var palette = HumanPalette.ForRider((int)(ErrandHash.Of(id) & 0x7FFFFF));
        ItemId? wear = kind switch
        {
            GiverKind.Senn => ItemId.Sennenkaeppi,
            GiverKind.HutWarden => ItemId.GuideHat,
            GiverKind.StationMaster => ItemId.HiVisVest,
            GiverKind.Dairy => ItemId.AlpineSocks,
            _ => null,
        };
        if (wear is { } w && Garments.Get(w) is { } g) palette = palette with { Outfit = palette.Outfit.With(g.Slot, g.Code) };
        return new FigureLook(palette);
    }

    private static SkinnedFigure MakeFigure(string id, GiverKind kind, ShaderMaterial material)
    {
        var figure = new SkinnedFigure { Name = "Giver", MaterialOverride = material };
        figure.Show(LookOf(id, kind));
        HumanMeshBuilder.PosePosed(figure, HumanPose.Standing);
        return figure;
    }

    /// <summary>The eight kinds side by side in the model viewer.</summary>
    [Showcase("People", "Errand givers")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseGivers() =>
        Enum.GetValues<GiverKind>().Select(k => (k.ToString(), (Func<Node3D>)(() => MakeFigure("showcase:" + k, k, HumanMeshBuilder.FigureMaterial()))));

    // ---- the glyphs ----------------------------------------------------------------------------------

    private static ImageTexture? _card, _hand;

    /// <summary>The postcard glyph (amber, a stamp in the corner) and the hand-over glyph (a green box with an arrow down), drawn once.</summary>
    public static ImageTexture Glyph(bool hand) => hand ? _hand ??= Draw(true) : _card ??= Draw(false);

    private static ImageTexture Draw(bool hand)
    {
        const int s = 16;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        img.Fill(new Color(0, 0, 0, 0));
        var edge = new Color("1c1a22");
        var body = hand ? new Color("48a840") : new Color("f0b030");
        var shade = hand ? new Color("2a6a2c") : new Color("c08818");
        for (int y = 2; y < 14; y++)
            for (int x = 1; x < 15; x++)
                img.SetPixel(x, y, y == 2 || y == 13 || x == 1 || x == 14 ? edge : y > 10 ? shade : body);
        if (hand)
        {
            for (int y = 4; y <= 9; y++) { img.SetPixel(7, y, Colors.White); img.SetPixel(8, y, Colors.White); }
            img.SetPixel(5, 8, Colors.White); img.SetPixel(6, 9, Colors.White); img.SetPixel(9, 9, Colors.White); img.SetPixel(10, 8, Colors.White);
        }
        else
        {
            for (int y = 4; y <= 6; y++) for (int x = 10; x <= 12; x++) img.SetPixel(x, y, new Color("c8202a"));
            for (int x = 3; x <= 8; x++) img.SetPixel(x, 8, shade);
            for (int x = 3; x <= 7; x++) img.SetPixel(x, 10, shade);
        }
        return ImageTexture.CreateFromImage(img);
    }

    // ---- showing the givers --------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        if (now >= _next)
        {
            _next = now + RefreshEvery;
            Refresh();
        }
        if (now >= _nextTurn && _shown.Count > 0)
        {
            _nextTurn = now + TurnEvery;
            Turn();
        }
    }

    private void ClearAll()
    {
        foreach (var e in _shown.Values) e.Holder.QueueFree();
        _shown.Clear();
        _dirty = true;
    }

    private void Refresh()
    {
        var me = _me();
        if (me == null || !IsInstanceValid(me) || _origin() is not { } origin || !Ui.AdventureLog.Adventuring || GiverHost.Current.Count == 0)
        {
            if (_shown.Count > 0) ClearAll();
            return;
        }
        int today = ErrandService.Today;
        if (today != _day) { _day = today; _dirty = true; }
        var at = me.Global;
        GiverHost.Current.Near(at.E, at.N, FreeM, _near);
        _live.Clear();
        foreach (var g in _near)
        {
            _live.Add(g.Id);
            if (_shown.ContainsKey(g.Id)) continue;
            double de = g.E - at.E, dn = g.N - at.N;
            if (de * de + dn * dn > ShowM * ShowM) { _live.Remove(g.Id); continue; }
            _material ??= HumanMeshBuilder.FigureMaterial();
            Add(g);
        }
        _drop.Clear();
        foreach (var id in _shown.Keys) if (!_live.Contains(id)) _drop.Add(id);
        foreach (var id in _drop)
        {
            _shown[id].Holder.QueueFree();
            _shown.Remove(id);
        }
        foreach (var e in _shown.Values)
        {
            // the ground under a giver arrives with its tile: set it down then
            if (!e.Grounded || e.Holder.Position == Vector3.Zero) Place(e, origin);
            if (_dirty || e.Mark < 0) Mark(e);
        }
        _dirty = false;
    }

    private void Add(Giver g)
    {
        var holder = new Node3D { Name = g.Id };
        var figure = MakeFigure(g.Id, g.Kind, _material!);
        float yaw = -g.Heading;
        figure.Rotation = new Vector3(0, yaw, 0);
        var icon = new Sprite3D
        {
            Texture = Glyph(false), PixelSize = 0.045f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Position = new Vector3(0, 2.35f, 0), Visible = false,
        };
        holder.AddChild(figure);
        holder.AddChild(icon);
        AddChild(holder);
        var entry = new Entry { Giver = g, Holder = holder, Figure = figure, Icon = icon, Yaw = yaw };
        entry.Cards = ErrandService.Instance?.CardsOf(g) ?? new();
        _shown[g.Id] = entry;
    }

    private void Place(Entry e, WorldOrigin origin)
    {
        var w = origin.ToWorld(e.Giver.E, e.Giver.N, e.Giver.Elevation);
        if (_ground(w) is { } y) { w.Y = y; e.Grounded = true; }
        e.Holder.Position = w - GlobalPosition;
    }

    /// <summary>The glyph over a giver: 2 a hand-over waits, 1 an untaken card, 0 none. Set only when it changes.</summary>
    private void Mark(Entry e)
    {
        var svc = ErrandService.Instance;
        var mine = AdventureService.Instance?.Mine;
        int mark = 0;
        if (svc != null && mine != null)
        {
            e.Cards = svc.CardsOf(e.Giver);
            if (HandOver(mine, e.Giver) != null) mark = 2;
            else foreach (var c in e.Cards) if (ErrandService.StateOf(mine, c.Id) == ErrandCardState.Open) { mark = 1; break; }
        }
        if (mark == e.Mark) return;
        e.Mark = mark;
        e.Icon.Visible = mark > 0;
        if (mark > 0) e.Icon.Texture = Glyph(mark == 2);
    }

    /// <summary>The running card whose current step is a hand-over at this giver, with its step: what the dialogue offers instead of a card.</summary>
    public static (string QuestId, int Step, DeliverStep Deliver, int Left)? HandOver(PlayerProgress mine, Giver g)
    {
        var svc = AdventureService.Instance;
        if (svc == null) return null;
        var quests = mine.Get(QuestsProgress.Section);
        foreach (var (id, state) in quests.Active)
        {
            if (!Postkarte.IsCard(id) || svc.Quests.Current(id, state) is not DeliverStep { At: AreaTarget a } d) continue;
            if (Math.Abs(a.E - g.E) < 1.5 && Math.Abs(a.N - g.N) < 1.5) return (id, state.Step, d, d.Count - state.Count);
        }
        return null;
    }

    private void Turn()
    {
        var me = _me();
        if (me == null || !IsInstanceValid(me)) return;
        var p = me.GlobalPosition;
        foreach (var e in _shown.Values)
        {
            var d = p - e.Holder.GlobalPosition;
            if (d.X * d.X + d.Z * d.Z > TurnM * TurnM) continue;
            float want = Mathf.Atan2(-d.X, -d.Z);
            e.Yaw = Mathf.LerpAngle(e.Yaw, want, 0.35f);
            e.Figure.Rotation = new Vector3(0, e.Yaw, 0);
        }
    }

    // ---- talking ---------------------------------------------------------------------------------------

    /// <summary>The giver within reach of a body, or null.</summary>
    public Giver? InReach(FootPlayer p)
    {
        if (_shown.Count == 0 || p.Indoors) return null;
        var at = p.GlobalPosition;
        Giver? best = null;
        float bd = ReachM * ReachM;
        foreach (var e in _shown.Values)
        {
            var d = e.Holder.GlobalPosition - at;
            if (Mathf.Abs(d.Y) > 4f) continue;
            float h = d.X * d.X + d.Z * d.Z;
            if (h <= bd) { bd = h; best = e.Giver; }
        }
        return best;
    }

    /// <summary>E / Y / a VR hand at a giver: opens the dialogue. False when none is in reach.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance?.InReach(p) is not { } giver || ErrandDialog.Instance is not { } dialog) return false;
        dialog.Open(giver);
        return true;
    }

    private (Giver? Giver, InputDevice Device) _promptKey;
    private string? _prompt;

    /// <summary>"[E] Talk to Frau Brügger, hut warden", the giver's figure outlined; null away from one.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } m || m.InReach(p) is not { } giver || !m._shown.TryGetValue(giver.Id, out var entry)) return null;
        var key = (giver, PlayerInput.HintDevice);
        if (m._prompt == null || key != m._promptKey)
        {
            m._promptKey = key;
            m._prompt = InputHints.Prompt(PlayerInput.InteractMount, "Talk to " + GiverNames.For(giver.Id, giver.Kind, giver.Canton));
        }
        return new Interaction(m._prompt, Outline.Of(entry.Figure));
    }
}
