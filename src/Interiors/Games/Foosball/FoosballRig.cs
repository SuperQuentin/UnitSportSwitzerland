using Godot;

namespace UnitSport.Interiors.Games.Foosball;

/// <summary>
/// A table football table on a client (#1324): the whole table, laid along the piece's longer side (the venues place
/// it both ways), in the game's frame (<see cref="FoosballGame"/>: x along, z across). The box, legs and pitch are
/// shared meshes; each of the eight rods is a node (slid and turned as the server's poses say) carrying its figures and
/// handle; the ball a node; the score is the beads on the two counters. Poses arrive 20 times a second and are eased
/// between, the ball carried on by its speed.
/// </summary>
public partial class FoosballRig : TableRig
{
    /// <summary>The pitch's floor and the rods' height above the piece's floor [m].</summary>
    public const float PitchY = 0.76f, RodY = 0.86f;

    private static readonly Color Red = new(0.82f, 0.12f, 0.12f), Blue = new(0.12f, 0.24f, 0.78f);
    private static BoxMesh? _figure;
    private static StandardMaterial3D? _redMat, _blueMat;

    private Node3D _table = null!;
    private readonly Node3D[] _rods = new Node3D[8];
    private MeshInstance3D _ball = null!;
    private readonly MeshInstance3D[][] _beads = new MeshInstance3D[2][];
    private Vector2 _shownBall, _lastBall;
    private double _lastAt;
    private readonly float[] _shownSlide = new float[8], _shownAngle = new float[8];
    private int _goals = -1;
    private AudioStreamPlayer3D _voice = null!;
    private Vector2 _lastV;

    public FoosballRig(FurniturePlan piece) => Init(piece);

    public static FoosballRig Create(FurniturePlan piece) => new(piece);

    /// <summary>The game's frame in the piece's: turned a quarter when the piece is deeper than it is wide.</summary>
    public Transform3D TableFrame => Piece.D > Piece.W ? new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2), Vector3.Zero) : Transform3D.Identity;

    public override void _Ready()
    {
        _table = new Node3D { Name = "Table", Transform = TableFrame };
        AddChild(_table);
        Body();
        for (int rod = 0; rod < 8; rod++) _table.AddChild(_rods[rod] = Rod(rod));
        _ball = new MeshInstance3D
        {
            Name = "Ball", Mesh = new SphereMesh { Radius = FoosballGame.Ball, Height = 2 * FoosballGame.Ball, RadialSegments = 10, Rings = 6 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.96f, 0.95f, 0.9f) },
            Position = new Vector3(0, PitchY + FoosballGame.Ball, 0),
        };
        _table.AddChild(_ball);
        for (int team = 0; team < 2; team++)
        {
            _beads[team] = new MeshInstance3D[FoosballGame.Win];
            var mat = new StandardMaterial3D { AlbedoColor = team == 0 ? Red : Blue };
            var mesh = new SphereMesh { Radius = 0.009f, Height = 0.018f, RadialSegments = 8, Rings = 4 };
            for (int i = 0; i < FoosballGame.Win; i++)
            {
                _beads[team][i] = new MeshInstance3D { Name = $"Bead{team}_{i}", Mesh = mesh, MaterialOverride = mat };
                _table.AddChild(_beads[team][i]);
            }
        }
        ShowBeads(new[] { 0, 0 });
        _voice = new AudioStreamPlayer3D { Name = "Voice", Bus = Audio.SfxBus.Name, UnitSize = 4f, MaxDistance = 25f, Position = new Vector3(0, RodY, 0) };
        AddChild(_voice);
    }

    /// <summary>The box on its legs, the green pitch with its lines, the goals' mouths, the bead wires.</summary>
    private void Body()
    {
        var wood = new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.24f, 0.14f) };
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.09f) };
        var pitch = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.56f, 0.24f) };
        var line = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.94f, 0.92f) };
        float hx = FoosballGame.HalfX, hz = FoosballGame.HalfZ;
        void Box(string name, Vector3 min, Vector3 max, Material m) =>
            _table.AddChild(new MeshInstance3D { Name = name, Mesh = new BoxMesh { Size = max - min }, MaterialOverride = m, Position = (min + max) / 2 });
        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
                Box("Leg", new Vector3(sx * (hx - 0.02f) - 0.03f, 0, sz * (hz - 0.02f) - 0.03f), new Vector3(sx * (hx - 0.02f) + 0.03f, PitchY - 0.04f, sz * (hz - 0.02f) + 0.03f), dark);
        Box("Under", new Vector3(-hx - 0.045f, PitchY - 0.06f, -hz - 0.03f), new Vector3(hx + 0.045f, PitchY - 0.005f, hz + 0.03f), wood);
        Box("Pitch", new Vector3(-hx, PitchY - 0.005f, -hz), new Vector3(hx, PitchY, hz), pitch);
        Box("Halfway", new Vector3(-0.003f, PitchY, -hz), new Vector3(0.003f, PitchY + 0.0008f, hz), line);
        // the long sides, under the rods; the ends with the goals' mouths
        foreach (float sz in new[] { -1f, 1f })
            Box("Side", new Vector3(-hx - 0.045f, PitchY - 0.06f, sz > 0 ? hz : -hz - 0.03f), new Vector3(hx + 0.045f, RodY + 0.05f, sz > 0 ? hz + 0.03f : -hz), wood);
        foreach (float sx in new[] { -1f, 1f })
        {
            float x0 = sx > 0 ? hx : -hx - 0.045f, x1 = sx > 0 ? hx + 0.045f : -hx;
            Box("End", new Vector3(x0, PitchY - 0.06f, -hz), new Vector3(x1, RodY + 0.05f, -FoosballGame.GoalHalf), wood);
            Box("End", new Vector3(x0, PitchY - 0.06f, FoosballGame.GoalHalf), new Vector3(x1, RodY + 0.05f, hz), wood);
            Box("Crossbar", new Vector3(x0, PitchY + 0.07f, -FoosballGame.GoalHalf), new Vector3(x1, RodY + 0.05f, FoosballGame.GoalHalf), wood);
            Box("Goal", new Vector3(sx > 0 ? hx + 0.045f : -hx - 0.06f, PitchY - 0.06f, -FoosballGame.GoalHalf), new Vector3(sx > 0 ? hx + 0.06f : -hx - 0.045f, PitchY + 0.07f, FoosballGame.GoalHalf), dark);
        }
    }

    /// <summary>A rod: the bar across the table, the handle on its team's side, its figures hanging from it (turned with it).</summary>
    private static Node3D Rod(int rod)
    {
        var node = new Node3D { Name = $"Rod{rod}", Position = new Vector3(FoosballGame.RodX[rod], RodY, 0) };
        var chrome = new StandardMaterial3D { AlbedoColor = new Color(0.75f, 0.76f, 0.78f), Metallic = 0.8f, Roughness = 0.3f };
        float handleSide = FoosballGame.Team[rod] == 0 ? -1 : 1;
        node.AddChild(new MeshInstance3D
        {
            Name = "Bar", Mesh = new CylinderMesh { TopRadius = 0.008f, BottomRadius = 0.008f, Height = 1.0f, RadialSegments = 8 },
            MaterialOverride = chrome, Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(0, 0, handleSide * 0.12f)),
        });
        node.AddChild(new MeshInstance3D
        {
            Name = "Handle", Mesh = new CylinderMesh { TopRadius = 0.017f, BottomRadius = 0.017f, Height = 0.11f, RadialSegments = 8 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.1f) },
            Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(0, 0, handleSide * 0.58f)),
        });
        _figure ??= new BoxMesh { Size = new Vector3(2 * FoosballGame.FootHalfX, 0.1f, 2 * FoosballGame.FootHalfZ) };
        _redMat ??= new StandardMaterial3D { AlbedoColor = Red };
        _blueMat ??= new StandardMaterial3D { AlbedoColor = Blue };
        for (int k = 0; k < FoosballGame.Figures[rod]; k++)
            node.AddChild(new MeshInstance3D
            {
                Name = $"Figure{k}", Mesh = _figure, MaterialOverride = FoosballGame.Team[rod] == 0 ? _redMat : _blueMat,
                // the figure from just over the bar to its foot, FootDepth under it
                Position = new Vector3(0, -FoosballGame.FootDepth + 0.05f, FoosballGame.FigureZ(rod, k)),
            });
        return node;
    }

    private void ShowBeads(int[] goals)
    {
        for (int team = 0; team < 2; team++)
            for (int i = 0; i < FoosballGame.Win; i++)
            {
                // over the end the team attacks from: its goals slid to one side of the wire, the rest to the other
                float end = team == 0 ? -FoosballGame.HalfX - 0.02f : FoosballGame.HalfX + 0.02f;
                bool scored = i < goals[team];
                float z = scored ? 0.1f + 0.018f * i : -0.1f - 0.018f * (i - goals[team]);
                _beads[team][i].Position = new Vector3(end, RodY + 0.06f, team == 0 ? z : -z);
            }
    }

    public override void OnState(TableGame game, double now)
    {
        if (game is not FoosballGame g) return;
        int total = g.Goals[0] + g.Goals[1];
        if (_goals >= 0 && total > _goals) Play(FoosballSounds.Goal);
        _goals = total;
        ShowBeads(g.Goals);
    }

    public override void Show(TableGame? game, double now, double delta)
    {
        if (game is not FoosballGame g) return;
        if (g.BallAt != _lastBall) { _lastBall = g.BallAt; _lastAt = now; }
        // carried on by its speed since the last pose, then eased there
        var target = g.BallAt + g.BallV * (float)Math.Min(now - _lastAt, 0.1);
        float k = 1 - Mathf.Exp(-(float)delta * 30f);
        _shownBall = (_shownBall - target).Length() > 0.15f ? target : _shownBall.Lerp(target, k);
        _ball.Visible = g.InPlay;
        _ball.Position = new Vector3(_shownBall.X, PitchY + FoosballGame.Ball, _shownBall.Y);
        for (int rod = 0; rod < 8; rod++)
        {
            _shownSlide[rod] = Mathf.Lerp(_shownSlide[rod], g.Slide[rod], k);
            _shownAngle[rod] = Mathf.Lerp(_shownAngle[rod], g.Angle[rod], k);
            // the angle turns the figures about the bar: positive swings the foot towards +x (red's attack)
            _rods[rod].Transform = new Transform3D(new Basis(Vector3.Back, _shownAngle[rod]), new Vector3(FoosballGame.RodX[rod], RodY, _shownSlide[rod]));
        }
        // a knock when the ball's run along the table turns hard (a figure hit it)
        if (g.InPlay && Mathf.Sign(g.BallV.X) != Mathf.Sign(_lastV.X) && Mathf.Abs(g.BallV.X - _lastV.X) > 0.6f)
            Play(Mathf.Abs(g.BallV.X) > 1.5f ? FoosballSounds.Kick : FoosballSounds.Knock, Mathf.LinearToDb(Mathf.Clamp(Mathf.Abs(g.BallV.X) / 3, 0.2f, 1)));
        _lastV = g.BallV;
    }

    /// <summary>Where ball and rods are shown (the check's view).</summary>
    public Vector2 ShownBall => _shownBall;
    public Node3D RodNode(int rod) => _rods[rod];

    private void Play(AudioStream s, float db = 0)
    {
        _voice.Stream = s;
        _voice.VolumeDb = db;
        _voice.Position = _table.Transform * new Vector3(_shownBall.X, RodY, _shownBall.Y);
        _voice.Play();
    }
}
