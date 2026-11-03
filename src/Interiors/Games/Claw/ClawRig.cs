using Godot;

namespace UnitSport.Interiors.Games.Claw;

/// <summary>
/// A claw machine's moving parts on a client (#1326). The cabinet is the room's mesh; here the gantry's bar (slid along
/// z), its carriage (along x), the cable (scaled to its length), the claw's hub and three prongs (turned open and shut),
/// the plush in the box (a node each, shared meshes per kind), the chute's rim and the countdown. The motor hums while
/// the claw moves; a prize down the chute clunks.
/// </summary>
public partial class ClawRig : TableRig
{
    /// <summary>The box's floor and the gantry's height above the piece's floor [m] (the room mesh's).</summary>
    public const float FloorY = 0.9f, GantryY = 1.78f, LowestY = 1.02f;
    private const int Nodes = 18;

    private static readonly Dictionary<Items.ItemId, (Mesh Body, Mesh Head, Material Main, Material Second)> Looks = new();

    private Node3D _bar = null!, _carriage = null!, _hub = null!;
    private MeshInstance3D _cable = null!;
    private readonly Node3D[] _prongs = new Node3D[3];
    private readonly Node3D[] _prizes = new Node3D[Nodes];
    private readonly Items.ItemId[] _kinds = new Items.ItemId[Nodes];
    private Label3D _clock = null!;
    private AudioStreamPlayer3D _motor = null!, _voice = null!;
    private Vector2 _lastClaw;
    private int _won = -1;
    private float _shownDown;
    private Vector2 _shownClaw;

    public ClawGame Idle { get; }

    public ClawRig(FurniturePlan piece)
    {
        Init(piece);
        Idle = new ClawGame(piece.W, piece.D, (int)(piece.X * 97 + piece.Z * 13));
    }

    public static ClawRig Create(FurniturePlan piece) => new(piece);

    public override void _Ready()
    {
        var chrome = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.79f, 0.82f), Metallic = 0.8f, Roughness = 0.25f };
        float hz = Piece.D / 2 - 0.06f;
        _bar = new Node3D { Name = "Gantry" };
        _bar.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Piece.W - 0.12f, 0.03f, 0.03f) }, MaterialOverride = chrome });
        AddChild(_bar);
        _carriage = new Node3D { Name = "Carriage" };
        _carriage.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.08f, 0.05f, 0.08f) }, MaterialOverride = chrome });
        _bar.AddChild(_carriage);
        _cable = new MeshInstance3D { Name = "Cable", Mesh = new BoxMesh { Size = new Vector3(0.008f, 1f, 0.008f) }, MaterialOverride = chrome };
        _carriage.AddChild(_cable);
        _hub = new Node3D { Name = "Claw" };
        _hub.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.05f, Height = 0.07f, RadialSegments = 12 }, MaterialOverride = chrome });
        _carriage.AddChild(_hub);
        var prong = new BoxMesh { Size = new Vector3(0.014f, 0.11f, 0.02f) };
        for (int i = 0; i < 3; i++)
        {
            var arm = new Node3D { Name = $"Prong{i}", Rotation = new Vector3(0, i * Mathf.Tau / 3, 0) };
            var pivot = new Node3D { Name = "Pivot", Position = new Vector3(0, -0.03f, 0.04f) };
            pivot.AddChild(new MeshInstance3D { Mesh = prong, MaterialOverride = chrome, Position = new Vector3(0, -0.055f, 0) });
            arm.AddChild(pivot);
            _hub.AddChild(arm);
            _prongs[i] = pivot;
        }
        // the chute's rim in the front-left corner
        var rim = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.85f, 0.88f) };
        var c = Idle.Chute;
        foreach (var (dx, dz, sx, sz) in new[] { (0.13f, 0f, 0.015f, 0.26f), (-0.13f, 0f, 0.015f, 0.26f), (0f, 0.13f, 0.26f, 0.015f), (0f, -0.13f, 0.26f, 0.015f) })
            AddChild(new MeshInstance3D { Name = "ChuteRim", Mesh = new BoxMesh { Size = new Vector3(sx, 0.12f, sz) }, MaterialOverride = rim, Position = new Vector3(c.X + dx, FloorY + 0.06f, c.Y + dz) });
        AddChild(new MeshInstance3D
        {
            Name = "ChuteHole", Mesh = new BoxMesh { Size = new Vector3(0.24f, 0.004f, 0.24f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.02f, 0.02f, 0.03f) }, Position = new Vector3(c.X, FloorY + 0.003f, c.Y),
        });
        for (int i = 0; i < Nodes; i++)
        {
            _prizes[i] = new Node3D { Name = $"Prize{i}", Visible = false };
            AddChild(_prizes[i]);
        }
        _clock = new Label3D
        {
            Name = "Countdown", Text = "", FontSize = 72, PixelSize = 0.0018f, Modulate = new Color(1f, 0.85f, 0.2f), Shaded = false,
            Position = new Vector3(0.15f, 0.8f, Piece.D / 2 + 0.17f),
        };
        AddChild(_clock);
        _motor = new AudioStreamPlayer3D { Name = "Motor", Bus = Audio.SfxBus.Name, Stream = ClawSounds.Motor, UnitSize = 3f, MaxDistance = 15f, VolumeDb = -80, Position = new Vector3(0, GantryY, 0) };
        AddChild(_motor);
        _voice = new AudioStreamPlayer3D { Name = "Voice", Bus = Audio.SfxBus.Name, UnitSize = 4f, MaxDistance = 20f, Position = new Vector3(c.X, FloorY, c.Y) };
        AddChild(_voice);
        _shownClaw = Idle.ClawAt;
        Show(null, 0, 0);
    }

    /// <summary>A prize's look: a body and a head, its colours (shared by every machine).</summary>
    private static (Mesh Body, Mesh Head, Material Main, Material Second) Look(Items.ItemId kind)
    {
        if (Looks.TryGetValue(kind, out var look)) return look;
        var (main, second) = kind switch
        {
            Items.ItemId.PlushCow => (new Color(0.96f, 0.96f, 0.93f), new Color(0.08f, 0.08f, 0.08f)),
            Items.ItemId.PlushMarmot => (new Color(0.62f, 0.45f, 0.28f), new Color(0.86f, 0.70f, 0.48f)),
            _ => (new Color(0.12f, 0.11f, 0.11f), new Color(0.80f, 0.45f, 0.20f)),
        };
        look = (new SphereMesh { Radius = 0.055f, Height = 0.09f, RadialSegments = 10, Rings = 6 }, new SphereMesh { Radius = 0.035f, Height = 0.07f, RadialSegments = 10, Rings = 6 },
            new StandardMaterial3D { AlbedoColor = main, Roughness = 1f }, new StandardMaterial3D { AlbedoColor = second, Roughness = 1f });
        Looks[kind] = look;
        return look;
    }

    /// <summary>Node <paramref name="i"/> dressed as a <paramref name="kind"/>: only when it changes.</summary>
    private void Dress(int i, Items.ItemId kind)
    {
        if (_kinds[i] == kind && _prizes[i].GetChildCount() > 0) return;
        _kinds[i] = kind;
        foreach (var c in _prizes[i].GetChildren()) c.QueueFree();
        var (body, head, main, second) = Look(kind);
        _prizes[i].AddChild(new MeshInstance3D { Mesh = body, MaterialOverride = main, Position = new Vector3(0, 0.045f, 0) });
        _prizes[i].AddChild(new MeshInstance3D { Mesh = head, MaterialOverride = kind == Items.ItemId.PlushCow ? main : second, Position = new Vector3(0, 0.1f, 0.03f) });
        // a cow's spots, a Bernese's white chest: a second small ball
        _prizes[i].AddChild(new MeshInstance3D { Mesh = head, MaterialOverride = second, Position = new Vector3(0.03f, 0.06f, -0.02f), Scale = Vector3.One * 0.6f });
    }

    public override void OnState(TableGame game, double now)
    {
        if (game is not ClawGame g) return;
        if (_won >= 0 && g.Won > _won) { _voice.Stream = ClawSounds.Win; _voice.Play(); }
        _won = g.Won;
    }

    public override void Show(TableGame? game, double now, double delta)
    {
        var g = game as ClawGame ?? Idle;
        g.Seen(now);
        float k = delta <= 0 ? 1 : 1 - Mathf.Exp(-(float)delta * 20f);
        _shownClaw = _shownClaw.Lerp(g.ClawAt, k);
        _shownDown = Mathf.Lerp(_shownDown, g.Down, k);
        _bar.Position = new Vector3(0, GantryY, _shownClaw.Y);
        _carriage.Position = new Vector3(_shownClaw.X, 0, 0);
        float hubY = Mathf.Lerp(GantryY - 0.12f, LowestY, _shownDown);
        _hub.Position = new Vector3(0, hubY - GantryY, 0);
        float cable = GantryY - hubY - 0.03f;
        _cable.Scale = new Vector3(1, Mathf.Max(0.01f, cable), 1);
        _cable.Position = new Vector3(0, -cable / 2, 0);
        float openAngle = Mathf.Lerp(0.05f, 0.75f, g.Open);
        foreach (var p in _prongs) p.Rotation = new Vector3(openAngle, 0, 0);
        for (int i = 0; i < Nodes; i++)
        {
            bool on = i < g.Pile.Count;
            _prizes[i].Visible = on;
            if (!on) continue;
            Dress(i, g.Pile[i].Item);
            var at = g.Pile[i].At;
            float y = FloorY;
            if (i == g.Held) { at = _shownClaw; y = hubY - 0.17f; }
            _prizes[i].Position = new Vector3(at.X, y, at.Y);
            _prizes[i].Rotation = new Vector3(0, (i * 1.7f) % Mathf.Tau, 0);
        }
        string clock = g.State == ClawGame.Phase.Steer ? $"{Math.Max(0, (int)Math.Ceiling(g.SteerUntil - now)):00}" : g.State == ClawGame.Phase.Idle ? "INSERT" : "";
        if (_clock.Text != clock) _clock.Text = clock;
        // the motor while the claw moves
        bool moving = (g.ClawAt - _lastClaw).Length() > 0.0005f || g.State is ClawGame.Phase.Drop or ClawGame.Phase.Lift;
        _lastClaw = g.ClawAt;
        float want = moving ? -10 : -80;
        _motor.VolumeDb = Mathf.MoveToward(_motor.VolumeDb, want, (float)delta * 200);
        if (moving && !_motor.Playing) _motor.Play();
        else if (!moving && _motor.Playing && _motor.VolumeDb <= -79) _motor.Stop();
    }

    /// <summary>Where the claw hangs and how far down it is shown (the check's view).</summary>
    public (Vector2 At, float Down) Shown => (_shownClaw, _shownDown);
}
