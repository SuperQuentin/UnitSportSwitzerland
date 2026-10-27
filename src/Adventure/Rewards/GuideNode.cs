using Godot;
using UnitSport.Birds;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The guide animal one follows (#1227, rewards plan 5), on its follower's client: a marmot (rigid parts as nodes, a
/// gait of turned legs) or a bearded vulture (<see cref="BirdMesh"/>'s species, wings flapping by rotation), played
/// from the server's <c>GuideLeg</c> moves on the shared clock (<see cref="GuideBook.At"/>): nothing is sent per
/// frame and no mesh is rebuilt. At each stop the marmot sits up and whistles. A plain child of the world.
/// </summary>
public partial class GuideNode : Node3D
{
    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<Vector3, float?> _ground;
    private Node3D? _body;
    private Node3D? _head, _tail;
    private readonly Node3D?[] _legs = new Node3D?[4];
    private Node3D? _wingA, _wingB;
    private string _chest = "";
    private SealLayout _layout = SealLayout.None;
    private int _leg;
    private double _since;
    private bool _stopped = true;
    private float _phase;
    private Vector3 _last;
    private AudioStreamPlayer3D _voice = null!;

    public static GuideNode? Instance { get; private set; }

    public GuideNode(Func<WorldOrigin?> origin, Func<Vector3, float?> ground)
    {
        _origin = origin;
        _ground = ground;
        Name = "Guide";
        Visible = false;
    }

    public GuideNode() : this(() => null, _ => null) { }

    /// <summary>The animal's node while one is out (the camera's fauna page).</summary>
    public Node3D? Body => _body;
    public bool IsVulture => _layout.Guide == GuideKind.Vulture;

    /// <summary>Which guide is out now (checks): its chest and leg.</summary>
    public (string Chest, int Leg) Leg => (_chest, _leg);

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (RewardService.Instance is { } r) r.GuideMoved -= OnMove;
    }

    public override void _Ready()
    {
        _voice = new AudioStreamPlayer3D { Bus = Audio.SfxBus.Name, UnitSize = 8, MaxDistance = 150 };
        AddChild(_voice);
        if (RewardService.Instance is { } r) r.GuideMoved += OnMove;
    }

    private void OnMove(GuideMove m)
    {
        if (RewardService.Instance is not { } svc) return;
        if (m.ChestId != _chest || _body == null)
        {
            if (RewardRules.TryTile(m.ChestId, p => Places.PoiCatalogueHost.Current.Resolve(p) is { } poi ? (poi.E, poi.N) : null, out var t)
                && svc.Map.TryTile(new TileId(t.E, t.N)) is { } spots && spots.FirstOrDefault(s => s.Id == m.ChestId) is { } chest)
                Show(m.ChestId, svc.Map.LayoutOf(chest));
            else return;
        }
        if (m.Done)
        {
            // arrived: it sits up by the chest, whistles, and goes back to its burrow after a while
            _leg = _layout.Props.Length - 1;
            _since = m.Since - 1000;
            Whistle();
            var t = GetTree().CreateTimer(6);
            t.Timeout += () => { if (IsInstanceValid(this) && _chest == m.ChestId) Hide(); };
            return;
        }
        _leg = m.Leg;
        _since = m.Since;
        _stopped = false;
    }

    private void Show(string chestId, SealLayout layout)
    {
        _body?.QueueFree();
        _chest = chestId;
        _layout = layout;
        if (layout.Guide == GuideKind.Vulture && BirdCatalog.ByName("Bearded Vulture") is { } species)
        {
            var parts = BirdMesh.Get(species);
            _body = new MeshInstance3D { Mesh = parts.Body, MaterialOverride = BirdMesh.Material };
            _wingA = new MeshInstance3D { Mesh = parts.WingA, MaterialOverride = BirdMesh.Material, Position = parts.Shoulder };
            _wingB = new MeshInstance3D { Mesh = parts.WingB, MaterialOverride = BirdMesh.Material, Position = parts.Shoulder };
            _body.AddChild(_wingA);
            _body.AddChild(_wingB);
        }
        else
        {
            _body = MarmotMeshBuilder.Assemble();
            _head = _body.GetNode<Node3D>("Head");
            _tail = _body.GetNode<Node3D>("Tail");
            for (int i = 0; i < 4; i++) _legs[i] = _body.GetNode<Node3D>($"Leg{i}");
        }
        AddChild(_body);
        Visible = true;
        _stopped = true;
        Whistle();
    }

    private new void Hide()
    {
        Visible = false;
        _body?.QueueFree();
        _body = null;
        _chest = "";
    }

    private void Whistle()
    {
        if (_layout.Guide == GuideKind.Vulture) return;
        _voice.Stream = RewardSounds.Whistle;
        _voice.Play();
    }

    public override void _Process(double delta)
    {
        if (_body == null || _origin() is not { } origin || _layout.Props.Length == 0) return;
        double now = Net.ClockSync.ServerNow;
        var (e, n, alt) = GuideBook.At(_layout, _leg, _since, now);
        var w = origin.ToWorld(e, n, alt);
        if (_ground(w) is { } g) w.Y = g;
        bool vulture = _layout.Guide == GuideKind.Vulture;
        bool moving = _leg > 0 && now - _since < GuideBook.LegSeconds(_layout, _leg);
        if (vulture && moving) w.Y += 12f * Mathf.Sin((float)Math.Clamp((now - _since) / Math.Max(0.1, GuideBook.LegSeconds(_layout, _leg)), 0, 1) * Mathf.Pi) + 2f;
        var local = w - GlobalPosition;
        var step = local - _last;
        step.Y = 0;
        if (step.LengthSquared() > 1e-5f) _body.Rotation = new Vector3(0, Mathf.Atan2(-step.X, -step.Z), 0);
        _body.Position = local;
        _phase += step.Length() * (vulture ? 0.6f : 9f);
        _last = local;
        if (vulture)
        {
            float flap = moving ? Mathf.Sin((float)now * 2.4f) * 0.35f : 0.9f;
            if (_wingA != null) _wingA.Rotation = new Vector3(0, 0, flap);
            if (_wingB != null) _wingB.Rotation = new Vector3(0, 0, -flap);
        }
        else
        {
            for (int i = 0; i < 4; i++)
                if (_legs[i] is { } leg) leg.Rotation = new Vector3(moving ? Mathf.Sin(_phase + (i is 0 or 3 ? 0 : Mathf.Pi)) * 0.7f : 0, 0, 0);
            // at a stop it sits up on its haunches and looks about
            float sit = moving ? 0 : -0.55f;
            _body.Rotation = new Vector3(Mathf.Lerp(_body.Rotation.X, sit, 0.15f), _body.Rotation.Y, 0);
            if (_head != null) _head.Rotation = new Vector3(0, moving ? 0 : Mathf.Sin((float)now * 1.3f) * 0.6f, 0);
            if (_tail != null) _tail.Rotation = new Vector3(moving ? 0.3f : -0.2f, 0, 0);
        }
        if (moving) _stopped = false;
        else if (!_stopped)
        {
            _stopped = true;
            Whistle();
        }
    }
}
