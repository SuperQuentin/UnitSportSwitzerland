using System.Text;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The rewards round the player (#1227), on the client: the chests (<see cref="ChestNode"/>), the edelweiss and
/// cairns (one <see cref="MultiMeshInstance3D"/> each), the seals' props, the guides' burrows and perches, the
/// trials' start posts and the shrines' offering boxes, within <see cref="RangeM"/>. Placed from this client's own
/// <see cref="RewardMap"/>, the same as the server's; what this player opened or picked (its own sections) is not
/// drawn, while another player beside it still sees it. Rebuilt only when the set in range, its state or the origin
/// tile change (checked once a second); nothing is rebuilt per frame. A plain child of the world, so the origin
/// shifter moves it. E / Y (a VR hand gripping) at a thing uses it (<see cref="TryInteract"/>); the gather hold picks
/// (<see cref="CollectibleNear"/>); rings and plates count as the body reaches them.
/// </summary>
public partial class RewardWorld : Node3D
{
    public const float RangeM = 320;
    private const double RefreshEvery = 1.0, SenseEvery = 0.1;

    public static RewardWorld? Instance { get; private set; }

    private readonly Func<FootPlayer?> _me;
    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<Vector3, float?> _ground;

    private readonly Dictionary<string, ChestNode> _chests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SealProps> _props = new(StringComparer.Ordinal);
    private readonly List<(RewardSpot Spot, Vector3 At)> _flowers = new(), _cairns = new();
    private readonly List<(TrialDef Def, Vector3 At)> _posts = new();
    private readonly List<(Poi Shrine, Vector3 At)> _boxes = new();
    private readonly List<RewardSpot> _near = new();
    private readonly List<Poi> _nearPois = new();
    private MultiMeshInstance3D _flowerMesh = null!, _cairnMesh = null!, _stoneMesh = null!, _postMesh = null!, _boxMesh = null!;
    private string _shape = "";
    private double _next, _nextSense;
    private readonly HashSet<string> _onPlate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _ringSent = new(StringComparer.Ordinal);

    /// <summary>The spots placed round the player now (the detector, checks).</summary>
    public IReadOnlyList<RewardSpot> Near => _near;
    /// <summary>The chests drawn now (checks).</summary>
    public IReadOnlyDictionary<string, ChestNode> Chests => _chests;
    /// <summary>The trials whose post is in range (checks).</summary>
    public IReadOnlyList<(TrialDef Def, Vector3 At)> Posts => _posts;

    /// <summary>A sealed chest's props drawn round it: their nodes and lit/passed looks.</summary>
    private sealed class SealProps
    {
        public required RewardSpot Chest;
        public required SealLayout Layout;
        public required Node3D Root;
        public readonly List<Node3D> Parts = new();
        public readonly List<Node3D> Lit = new();
        public readonly List<Vector3> At = new();
        /// <summary>The plates' keys in the stood-on set, made once (the 10 Hz sense allocates nothing).</summary>
        public readonly List<string> PlateKeys = new();
        public int Mask = -1;
        /// <summary>A two-handle gate's gate (#1266), and where it stands closed.</summary>
        public Node3D? Gate;
        public float GateY;
    }

    public RewardWorld(Func<FootPlayer?> me, Func<WorldOrigin?> origin, Func<Vector3, float?> ground)
    {
        _me = me;
        _origin = origin;
        _ground = ground;
        Name = "Rewards";
    }

    public RewardWorld() : this(() => null, () => null, _ => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (AdventureService.Instance is { } a) a.SectionChanged -= OnSection;
        if (RewardService.Instance is { } r)
        {
            r.SealChanged -= OnSeal;
            r.ChestOpened -= OnOpened;
            r.Picked -= OnPicked;
        }
    }

    public override void _Ready()
    {
        var material = RewardMeshes.Material();
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
        _flowerMesh = Layer("Edelweiss", RewardMeshes.Edelweiss());
        _cairnMesh = Layer("Cairns", RewardMeshes.Cairn());
        _stoneMesh = Layer("CairnStones", RewardMeshes.CairnStone());
        _postMesh = Layer("TrialPosts", RewardMeshes.StartPost());
        _boxMesh = Layer("OfferingBoxes", RewardMeshes.OfferingBox());
        if (AdventureService.Instance is { } a) a.SectionChanged += OnSection;
        if (RewardService.Instance is { } r)
        {
            r.SealChanged += OnSeal;
            r.ChestOpened += OnOpened;
            r.Picked += OnPicked;
        }
    }

    private void OnSection(string id)
    {
        if (id == ChestsProgress.Section.Id || id == CollectProgress.Section.Id) _next = 0;
    }

    private void OnPicked(string id) => _next = 0;

    private void OnSeal(string chestId, int mask)
    {
        if (_props.TryGetValue(chestId, out var p)) ShowMask(p, mask);
        if (_chests.TryGetValue(chestId, out var c)) c.SetSealed((mask & Seals.DoneBit) == 0);
        // a guide's chest shows itself when the guide has led to it
        if ((mask & Seals.DoneBit) != 0) _next = 0;
        if (_props.TryGetValue(chestId, out var props) && props.Layout.Kind == SealKind.Bonfire && mask != 0)
            PlayAt(RewardSounds.Whoosh, props.Root.Position);
    }

    private void OnOpened(string id, List<LootLine> items, ChestTier tier, int francs, string note)
    {
        string spotId = RewardRules.SpotIdOf(id);
        if (!_chests.TryGetValue(spotId, out var chest)) return;
        PlayAt(RewardSounds.Lid, chest.Position);
        chest.Open(tier, () =>
        {
            if (_chests.Remove(spotId)) chest.QueueFree();
            _next = 0;
        });
    }

    public override void _Process(double delta)
    {
        double now = GameClock.Now;
        if (now >= _next)
        {
            _next = now + RefreshEvery;
            Refresh();
        }
        if (now >= _nextSense)
        {
            _nextSense = now + SenseEvery;
            Sense();
        }
    }

    // ---- what is in range ---------------------------------------------------------------------

    private static RewardService? Svc => RewardService.Instance;

    private void Refresh()
    {
        var me = _me();
        _near.Clear();
        if (Svc is not { Map.Enabled: true } svc || me == null || !IsInstanceValid(me) || _origin() is not { } origin || !Ui.AdventureLog.Adventuring)
        {
            if (_shape.Length > 0) Clear();
            return;
        }
        var at = me.Global;
        var here = TileId.FromLv95(at.E, at.N);
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                if (svc.Map.TryTile(new TileId(here.E + de, here.N + dn)) is not { } spots) continue;
                foreach (var s in spots)
                    if (Dist2(s.E, s.N, at.E, at.N) <= RangeM * RangeM) _near.Add(s);
            }
        var cat = PoiCatalogueHost.Current;
        cat.Near(at.E, at.N, RangeM, _nearPois);
        var mine = AdventureService.Instance?.Mine;
        var opened = mine?.Get(ChestsProgress.Section).Opened;
        var taken = mine?.Get(CollectProgress.Section).Taken;
        int edition = RewardRules.Edition(DateTime.UtcNow);
        var sb = new StringBuilder();
        foreach (var s in _near)
        {
            sb.Append(s.Id);
            if (s.Kind == SpotKind.Chest)
            {
                bool gone = opened?.Contains(RewardRules.OpenId(s, edition)) == true;
                sb.Append(gone ? '-' : '+').Append(Unsealed(s) ? 'u' : 's');
            }
            else sb.Append(taken?.Contains(s.Id) == true ? '-' : '+');
            sb.Append(_ground(origin.ToWorld(s.E, s.N, s.Alt)) != null ? 'g' : '.');
        }
        foreach (var p in _nearPois) sb.Append(p.Hash % 1000);
        sb.Append((int)(origin.E / 1000)).Append('/').Append((int)(origin.N / 1000));
        string shape = sb.ToString();
        if (shape == _shape) return;
        _shape = shape;
        Place(origin, opened, taken, edition, cat);
    }

    private static double Dist2(double e0, double n0, double e1, double n1) => (e0 - e1) * (e0 - e1) + (n0 - n1) * (n0 - n1);

    /// <summary>Whether a chest opens for this player now: no seal, or the server said it is complete.</summary>
    public static bool Unsealed(RewardSpot chest) =>
        chest.Seal == SealKind.None || Svc?.SealMasks.TryGetValue(chest.Id, out int m) == true && (m & Seals.DoneBit) != 0
        // a rope team's chest (#1266): open while this player is roped to a friend beside it
        || chest.Seal == SealKind.Roped && Together.TogetherService.Instance?.RopedHere(chest.E, chest.N, Seals.RopedReachM) == true;

    private Vector3 Snap(WorldOrigin origin, double e, double n, float alt)
    {
        var w = origin.ToWorld(e, n, alt);
        if (_ground(w) is { } g) w.Y = g;
        return w - GlobalPosition;
    }

    private void Clear()
    {
        _shape = "";
        foreach (var c in _chests.Values) c.QueueFree();
        _chests.Clear();
        foreach (var p in _props.Values) p.Root.QueueFree();
        _props.Clear();
        _flowers.Clear();
        _cairns.Clear();
        _posts.Clear();
        _boxes.Clear();
        foreach (var m in new[] { _flowerMesh, _cairnMesh, _stoneMesh, _postMesh, _boxMesh }) m.Multimesh.InstanceCount = 0;
    }

    private void Place(WorldOrigin origin, HashSet<string>? opened, HashSet<string>? taken, int edition, PoiCatalogue cat)
    {
        var svc = Svc!;
        var keepChests = new HashSet<string>(StringComparer.Ordinal);
        var keepProps = new HashSet<string>(StringComparer.Ordinal);
        _flowers.Clear();
        _cairns.Clear();
        var stones = new List<Vector3>();
        foreach (var s in _near)
        {
            switch (s.Kind)
            {
                case SpotKind.Chest:
                {
                    if (opened?.Contains(RewardRules.OpenId(s, edition)) == true && !(_chests.TryGetValue(s.Id, out var o) && o.Opening)) continue;
                    var layout = svc.Map.LayoutOf(s);
                    bool done = Unsealed(s);
                    // a guide's chest is hidden until the guide has led this player to it
                    if (s.Seal != SealKind.Guide || done)
                    {
                        keepChests.Add(s.Id);
                        var at = Snap(origin, s.E, s.N, s.Alt);
                        if (!_chests.TryGetValue(s.Id, out var node))
                        {
                            node = ChestNode.Build(s.Tier, s.Seal != SealKind.None && !done, 0, s);
                            node.Rotation = new Vector3(0, (float)(s.Hash % 628) / 100f, 0);
                            AddChild(node);
                            _chests[s.Id] = node;
                        }
                        if (!node.Opening) node.Position = at;
                        node.SetSealed(s.Seal != SealKind.None && !done);
                    }
                    if (s.Seal != SealKind.None && !done && layout.Kind != SealKind.None)
                    {
                        keepProps.Add(s.Id);
                        if (!_props.ContainsKey(s.Id)) _props[s.Id] = BuildProps(origin, s, layout);
                        else PlaceProps(origin, _props[s.Id]);
                    }
                    break;
                }
                case SpotKind.Edelweiss:
                    if (taken?.Contains(s.Id) != true) _flowers.Add((s, Snap(origin, s.E, s.N, s.Alt)));
                    break;
                case SpotKind.Cairn:
                    var c = Snap(origin, s.E, s.N, s.Alt);
                    _cairns.Add((s, c));
                    if (taken?.Contains(s.Id) == true) stones.Add(c + new Vector3(0, RewardMeshes.CairnTop, 0));
                    break;
            }
        }
        foreach (var id in _chests.Keys.Where(k => !keepChests.Contains(k) && !_chests[k].Opening).ToList())
        {
            _chests[id].QueueFree();
            _chests.Remove(id);
        }
        foreach (var id in _props.Keys.Where(k => !keepProps.Contains(k)).ToList())
        {
            _props[id].Root.QueueFree();
            _props.Remove(id);
        }
        // an edelweiss is a few centimetres across: drawn 2.5 times its size, so a player sees it from the path
        Fill(_flowerMesh, _flowers.Select(f => (f.At, (float)(f.Spot.Hash % 628) / 100f)), EdelweissScale);
        Fill(_cairnMesh, _cairns.Select(f => (f.At, (float)(f.Spot.Hash % 628) / 100f)));
        Fill(_stoneMesh, stones.Select(s => (s, 0.4f)));
        // trial posts and offering boxes at the places in range
        _posts.Clear();
        _boxes.Clear();
        foreach (var poi in _nearPois)
        {
            foreach (var t in Trials.At(poi, cat)) _posts.Add((t, Snap(origin, t.StartE, t.StartN, t.StartAlt)));
            if (Offering.IsShrine(poi))
            {
                double a = (poi.Hash % 628) / 100.0;
                _boxes.Add((poi, Snap(origin, poi.E + Math.Sin(a) * 3, poi.N + Math.Cos(a) * 3, poi.Elevation)));
            }
        }
        Fill(_postMesh, _posts.Select(p => (p.At, 0f)));
        Fill(_boxMesh, _boxes.Select(b => (b.At, (float)(b.Shrine.Hash % 628) / 100f)));
    }

    public const float EdelweissScale = 2.5f;

    private static void Fill(MultiMeshInstance3D layer, IEnumerable<(Vector3 At, float Yaw)> items, float scale = 1)
    {
        var list = items.ToList();
        layer.Multimesh.InstanceCount = list.Count;
        for (int i = 0; i < list.Count; i++)
            layer.Multimesh.SetInstanceTransform(i, new Transform3D(new Basis(Vector3.Up, list[i].Yaw).Scaled(Vector3.One * scale), list[i].At));
    }

    // ---- seal props -------------------------------------------------------------------------------

    private SealProps BuildProps(WorldOrigin origin, RewardSpot chest, SealLayout layout)
    {
        var root = new Node3D { Name = "Seal_" + chest.Id.Replace(':', '_') };
        AddChild(root);
        var p = new SealProps { Chest = chest, Layout = layout, Root = root };
        var material = RewardMeshes.Material();
        for (int i = 0; i < layout.Props.Length; i++)
        {
            var part = new Node3D { Name = $"Prop{i}" };
            root.AddChild(part);
            p.Parts.Add(part);
            Node3D? lit = null;
            switch (layout.Kind)
            {
                case SealKind.Bonfire:
                    part.AddChild(new MeshInstance3D { Mesh = RewardMeshes.WoodStack(), MaterialOverride = material });
                    lit = new MeshInstance3D { Mesh = RewardMeshes.Flame(), MaterialOverride = RewardMeshes.GlowMaterial(), Visible = false };
                    lit.AddChild(new OmniLight3D { LightColor = new Color(1, 0.6f, 0.2f), LightEnergy = 1.6f, OmniRange = 9, Position = new Vector3(0, 1, 0) });
                    break;
                case SealKind.Rings:
                    part.AddChild(new MeshInstance3D { Mesh = RewardMeshes.Ring(), MaterialOverride = material, Scale = Vector3.One * layout.Props[i].Radius });
                    lit = new MeshInstance3D { Mesh = RewardMeshes.Ring(), MaterialOverride = RewardMeshes.GlowMaterial(), Visible = false, Scale = Vector3.One * (layout.Props[i].Radius * 1.02f) };
                    break;
                case SealKind.Plates:
                    part.AddChild(new MeshInstance3D { Mesh = RewardMeshes.Plate(), MaterialOverride = material });
                    break;
                case SealKind.Bells:
                    // the bell swings from the rail as its own node
                    var bell = new Node3D { Name = "Bell", Position = new Vector3(0, RewardMeshes.BellHang, 0) };
                    bell.AddChild(new MeshInstance3D { Mesh = RewardMeshes.Bell(), MaterialOverride = material });
                    part.AddChild(bell);
                    if (i == 2) part.AddChild(new MeshInstance3D { Mesh = RewardMeshes.BellRail(), MaterialOverride = material });
                    break;
                case SealKind.Guide:
                    if (i == 0)
                        part.AddChild(new MeshInstance3D { Mesh = layout.Guide == GuideKind.Vulture ? RewardMeshes.Perch() : RewardMeshes.Mound(), MaterialOverride = material });
                    break;
                case SealKind.Gate:
                    // a wheel on its post (#1266), turning while held; the gate itself between the two, lowered when open
                    var wheel = new MeshInstance3D { Name = "Wheel", Mesh = Together.GateMeshes.Wheel(), MaterialOverride = material, Position = new Vector3(0, 1.0f, 0) };
                    part.AddChild(new MeshInstance3D { Mesh = Together.GateMeshes.Post(), MaterialOverride = material });
                    part.AddChild(wheel);
                    if (i == 0)
                    {
                        var gate = new MeshInstance3D { Name = "Gate", Mesh = Together.GateMeshes.Gate(), MaterialOverride = material };
                        root.AddChild(gate);
                        p.Gate = gate;
                    }
                    break;
            }
            if (lit != null) part.AddChild(lit);
            p.Lit.Add(lit!);
            p.At.Add(Vector3.Zero);
            p.PlateKeys.Add(chest.Id + "#" + i);
        }
        PlaceProps(origin, p);
        ShowMask(p, Svc?.SealMasks.GetValueOrDefault(chest.Id) ?? 0);
        return p;
    }

    private void PlaceProps(WorldOrigin origin, SealProps p)
    {
        var l = p.Layout;
        for (int i = 0; i < l.Props.Length; i++)
        {
            var prop = l.Props[i];
            var at = Snap(origin, prop.E, prop.N, prop.Alt);
            if (l.Kind == SealKind.Rings) at.Y += 1.8f;
            p.At[i] = at;
            p.Parts[i].Position = at;
            // rings face along the run; the rail's bells face the chest
            int next = Math.Min(i + 1, l.Props.Length - 1), prev = Math.Max(0, i - 1);
            var a = l.Props[next == i ? prev : next];
            double de = a.E - prop.E, dn = a.N - prop.N;
            float yaw = (float)Math.Atan2(-de, -dn);
            if (l.Kind is SealKind.Rings or SealKind.Bells or SealKind.Gate) p.Parts[i].Rotation = new Vector3(0, yaw + (l.Kind == SealKind.Bells ? Mathf.Pi / 2 : 0), 0);
        }
        // the two-handle gate stands between its wheels, across the way to the chest
        if (p.Gate is { } gate && l.Props.Length == 2)
        {
            gate.Position = (p.At[0] + p.At[1]) * 0.5f;
            p.GateY = gate.Position.Y;
            var across = p.At[1] - p.At[0];
            gate.Rotation = new Vector3(0, Mathf.Atan2(-across.Z, across.X), 0);
        }
    }

    private static void ShowMask(SealProps p, int mask)
    {
        if (p.Mask == mask) return;
        p.Mask = mask;
        // an opened gate sinks into the ground (a tween of its node, never a new mesh)
        if (p.Gate is { } gate && GodotObject.IsInstanceValid(gate))
        {
            bool open = (mask & Seals.DoneBit) != 0;
            gate.CreateTween().TweenProperty(gate, "position:y", p.GateY - (open ? 2.6f : 0f), 1.2f);
        }
        for (int i = 0; i < p.Lit.Count; i++)
            if (p.Lit[i] != null) p.Lit[i].Visible = (mask & (1 << i)) != 0;
    }

    // ---- the body's senses: rings passed, plates stood on ----------------------------------------

    private void Sense()
    {
        var me = _me();
        if (me == null || !IsInstanceValid(me) || Svc is not { } svc || _props.Count == 0) return;
        var feet = me.GlobalPosition - GlobalPosition;
        var body = feet + new Vector3(0, 1f, 0);
        foreach (var (id, p) in _props)
        {
            if (p.Layout.Kind == SealKind.Rings)
            {
                int next = svc.SealMasks.TryGetValue(id, out int m) ? RingsDone(m) : 0;
                // the first ring always (re)starts the run; else the next one
                for (int i = 0; i < p.At.Count; i++)
                {
                    if (i != 0 && i != next) continue;
                    if ((p.At[i] - body).Length() > p.Layout.Props[i].Radius) continue;
                    if (_ringSent.TryGetValue(id, out int sent) && sent == i) continue;
                    _ringSent[id] = i;
                    svc.AskSealStep(id, i);
                }
            }
            else if (p.Layout.Kind == SealKind.Gate && PlayerInput.Held(PlayerInput.InteractMount))
            {
                // a wheel held (#1266): turned twice a second while E stays down within reach
                for (int i = 0; i < p.At.Count; i++)
                {
                    var d = p.At[i] - feet;
                    if (d.X * d.X + d.Z * d.Z > Seals.GateReachM * Seals.GateReachM || Mathf.Abs(d.Y) > 2f) continue;
                    if (Core.GameClock.Now < _wheelNext) break;
                    _wheelNext = Core.GameClock.Now + 0.5;
                    if (p.Parts[i].GetNodeOrNull<Node3D>("Wheel") is { } wheel) wheel.RotateObjectLocal(Vector3.Forward, 0.6f);
                    svc.AskSealStep(id, i);
                }
            }
            else if (p.Layout.Kind == SealKind.Plates)
            {
                for (int i = 0; i < p.At.Count; i++)
                {
                    var d = p.At[i] - feet;
                    bool on = d.X * d.X + d.Z * d.Z <= p.Layout.Props[i].Radius * p.Layout.Props[i].Radius && Mathf.Abs(d.Y) < 1.5f;
                    string key = p.PlateKeys[i];
                    if (on && _onPlate.Add(key)) svc.AskSealStep(id, i);
                    else if (!on) _onPlate.Remove(key);
                }
            }
        }
    }

    private static int RingsDone(int mask)
    {
        int n = 0;
        while ((mask & (1 << n)) != 0) n++;
        return n;
    }

    // ---- interacting ---------------------------------------------------------------------------

    private enum Thing { None, Chest, Stack, Bell, TuneBell, Burrow, Post, Box, Wheel }

    private double _wheelNext;

    private (Thing Kind, string Id, int Index, Vector3 At) InReach(FootPlayer p)
    {
        var best = (Thing.None, "", 0, Vector3.Zero);
        if (p.Indoors) return best;
        var at = p.GlobalPosition - GlobalPosition;
        float bd = RewardRules.AskReachM * RewardRules.AskReachM;
        void Try(Thing kind, string id, int index, Vector3 where, float reach = RewardRules.AskReachM)
        {
            var d = where - at;
            if (Mathf.Abs(d.Y) > 3f) return;
            float h = d.X * d.X + d.Z * d.Z;
            if (h <= reach * reach && h <= bd) { bd = h; best = (kind, id, index, where); }
        }
        foreach (var (id, c) in _chests)
            if (!c.Opening) Try(Thing.Chest, id, 0, c.Position);
        foreach (var (id, s) in _props)
            for (int i = 0; i < s.At.Count; i++)
                switch (s.Layout.Kind)
                {
                    case SealKind.Bonfire: Try(Thing.Stack, id, i, s.At[i], 2.6f); break;
                    case SealKind.Bells: Try(i == 4 ? Thing.TuneBell : Thing.Bell, id, i, s.At[i], 0.9f); break;
                    case SealKind.Guide when i == 0: Try(Thing.Burrow, id, 0, s.At[0], 2.6f); break;
                    case SealKind.Gate: Try(Thing.Wheel, id, i, s.At[i], Seals.GateReachM); break;
                }
        for (int i = 0; i < _posts.Count; i++) Try(Thing.Post, _posts[i].Def.Id, i, _posts[i].At);
        for (int i = 0; i < _boxes.Count; i++) Try(Thing.Box, _boxes[i].Shrine.Id, i, _boxes[i].At);
        return best;
    }

    /// <summary>E / Y / a VR hand at a reward thing: uses it. False when none is in reach.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } w || Svc is not { } svc) return false;
        var (kind, id, index, at) = w.InReach(p);
        switch (kind)
        {
            case Thing.Chest:
                var chest = w._chests[id].Spot!;
                if (!Unsealed(chest))
                {
                    Items.ItemController.Instance?.Toast($"Sealed. {Seals.Hint(chest.Seal)}");
                    return true;
                }
                svc.AskOpen(RewardRules.OpenId(chest, RewardRules.Edition(DateTime.UtcNow)));
                return true;
            case Thing.Stack:
                svc.AskSealStep(id, index);
                return true;
            case Thing.Bell:
                w.Ring(id, index);
                svc.AskSealStep(id, index);
                return true;
            case Thing.TuneBell:
                w.PlayTune(id);
                return true;
            case Thing.Burrow:
                svc.AskGuide(id);
                return true;
            case Thing.Wheel:
                svc.AskSealStep(id, index);
                return true;
            case Thing.Post:
                svc.AskTrial(id);
                return true;
            case Thing.Box:
                RewardHud.Instance?.Offer(w._boxes[index].Shrine);
                return true;
        }
        return false;
    }

    private (Thing, string, int, Vector3, InputDevice) _promptKey;
    private string? _prompt;

    /// <summary>The prompt at a reward thing ("[E] Open the Fine chest"), built only when it changes; null away from one.</summary>
    public static string? PromptFor(FootPlayer p)
    {
        if (Instance is not { } w) return null;
        var (kind, id, index, at) = w.InReach(p);
        if (kind == Thing.None) return null;
        var key = (kind, id, index, at, PlayerInput.HintDevice);
        if (w._prompt != null && key == w._promptKey) return w._prompt;
        w._promptKey = key;
        string what = kind switch
        {
            Thing.Chest when w._chests[id].Spot is { } c => Unsealed(c) ? $"Open the {RewardMeshes.TierName(c.Tier).ToLowerInvariant()}" : $"Sealed: {Seals.Name(c.Seal)}",
            Thing.Stack => "Light the wood stack",
            Thing.Bell => "Ring the cowbell",
            Thing.TuneBell => "Listen to the tune",
            Thing.Burrow => w._props.TryGetValue(id, out var s) && s.Layout.Guide == GuideKind.Vulture ? "Wake the bearded vulture" : "Wake the marmot",
            Thing.Post => $"Start the trial: {w._posts[index].Def.Name}",
            Thing.Wheel => "Hold: turn the wheel (a friend turns the other)",
            Thing.Box => $"Offer edelweiss at {w._boxes[index].Shrine.Name}",
            _ => "",
        };
        return w._prompt = InputHints.Prompt(PlayerInput.InteractMount, what);
    }

    /// <summary>The edelweiss or cairn the gather hold reaches from the feet or just ahead, or null.</summary>
    public RewardSpot? CollectibleNear(Vector3 feet, Vector3 ahead)
    {
        var f = feet - GlobalPosition;
        var a = ahead - GlobalPosition;
        RewardSpot? best = null;
        float bd = 1.6f * 1.6f;
        var taken = AdventureService.Instance?.Mine.Get(CollectProgress.Section).Taken;
        void From(RewardSpot s, Vector3 at, Vector3 from)
        {
            var d = at - from;
            if (Mathf.Abs(d.Y) > 2.5f) return;
            float h = d.X * d.X + d.Z * d.Z;
            if (h < bd) { bd = h; best = s; }
        }
        void Try(RewardSpot s, Vector3 at)
        {
            From(s, at, f);
            From(s, at, a);
        }
        foreach (var (s, at) in _flowers) Try(s, at);
        foreach (var (s, at) in _cairns)
            if (taken?.Contains(s.Id) != true) Try(s, at);
        return best;
    }

    // ---- the cowbells ----------------------------------------------------------------------------

    private void Ring(string chestId, int bell)
    {
        if (!_props.TryGetValue(chestId, out var p) || bell >= p.Parts.Count) return;
        PlayAt(RewardSounds.Bell(bell), p.At[bell]);
        if (p.Parts[bell].GetNodeOrNull<Node3D>("Bell") is { } b)
        {
            var t = b.CreateTween();
            t.TweenProperty(b, "rotation:x", 0.5f, 0.12f);
            t.TweenProperty(b, "rotation:x", -0.35f, 0.2f);
            t.TweenProperty(b, "rotation:x", 0f, 0.3f);
        }
    }

    /// <summary>The fifth bell plays the tune: the rail's bells in order, half a second apart.</summary>
    private async void PlayTune(string chestId)
    {
        if (!_props.TryGetValue(chestId, out var p)) return;
        Ring(chestId, 4);
        foreach (int b in p.Layout.Tune)
        {
            await ToSignal(GetTree().CreateTimer(0.55), SceneTreeTimer.SignalName.Timeout);
            if (!IsInstanceValid(this)) return;
            Ring(chestId, b);
        }
    }

    /// <summary>A sound in the world at a point (local to this node).</summary>
    public void PlayAt(AudioStream stream, Vector3 local)
    {
        var player = new AudioStreamPlayer3D { Stream = stream, Bus = SfxBus.Name, Position = local, UnitSize = 6, MaxDistance = 120 };
        AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }

    /// <summary>A chest's world position (checks), or null when it is not drawn.</summary>
    public Vector3? ChestAt(string id) => _chests.TryGetValue(id, out var c) ? c.GlobalPosition : null;

    /// <summary>A seal prop's world position (checks), or null.</summary>
    public Vector3? PropAt(string chestId, int i) => _props.TryGetValue(chestId, out var p) && i < p.Parts.Count ? p.Parts[i].GlobalPosition : null;

    /// <summary>An offering box's world position (checks).</summary>
    public Vector3? BoxAt(string shrineId)
    {
        foreach (var (shrine, at) in _boxes)
            if (shrine.Id == shrineId) return at + GlobalPosition;
        return null;
    }

    /// <summary>Offline, a pressure plate's other weight: something dropped on it (there is no other body).</summary>
    public static bool DroppedOn(double e, double n, float radius)
    {
        if (Items.DroppedItems.Instance is not { } dropped) return false;
        foreach (var item in dropped.Items)
        {
            var (ie, inn) = dropped.Origin.ToLv95(item.GlobalPosition);
            if ((ie - e) * (ie - e) + (inn - n) * (inn - n) <= radius * radius) return true;
        }
        return false;
    }

    /// <summary>Whether an edelweiss is drawn now (checks).</summary>
    public bool Shows(string id) => _flowers.Any(f => f.Spot.Id == id);
}
