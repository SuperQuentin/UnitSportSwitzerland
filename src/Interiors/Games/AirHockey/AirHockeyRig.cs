using Godot;

namespace UnitSport.Interiors.Games.AirHockey;

/// <summary>
/// An air hockey table's puck, mallets, score and sounds on a client (#1326). The table, its lit field and rails are
/// the room's mesh; the puck and the two mallets are nodes moved from the streamed poses (eased, the puck carried on by
/// its speed), the score a lit board over the table's side, set on change.
/// </summary>
public partial class AirHockeyRig : TableRig
{
    /// <summary>The field's top above the piece's floor [m] (the room mesh's lit field).</summary>
    public const float FieldY = 0.785f;

    private MeshInstance3D _puck = null!;
    private readonly MeshInstance3D[] _mallets = new MeshInstance3D[2];
    private Label3D _score = null!;
    private Vector2 _shownPuck, _lastPuck, _lastV;
    private readonly Vector2[] _shownMallet = new Vector2[2];
    private double _lastAt;
    private int _goals = -1;
    private AudioStreamPlayer3D _voice = null!;

    public (float Hx, float Hz) Field { get; }

    public AirHockeyRig(FurniturePlan piece)
    {
        Init(piece);
        Field = AirHockeyGame.Field(piece.W, piece.D);
    }

    public static AirHockeyRig Create(FurniturePlan piece) => new(piece);

    public override void _Ready()
    {
        _puck = new MeshInstance3D
        {
            Name = "Puck", Mesh = new CylinderMesh { TopRadius = AirHockeyGame.Puck, BottomRadius = AirHockeyGame.Puck, Height = 0.006f, RadialSegments = 16 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.98f, 0.82f, 0.10f) },
        };
        AddChild(_puck);
        Color[] colours = { new(0.10f, 0.30f, 0.85f), new(0.85f, 0.10f, 0.12f) };
        for (int s = 0; s < 2; s++)
        {
            var mallet = new MeshInstance3D
            {
                Name = $"Mallet{s}", Mesh = new CylinderMesh { TopRadius = AirHockeyGame.Mallet * 0.7f, BottomRadius = AirHockeyGame.Mallet, Height = 0.03f, RadialSegments = 16 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = colours[s] },
            };
            mallet.AddChild(new MeshInstance3D
            {
                Name = "Knob", Mesh = new CylinderMesh { TopRadius = 0.018f, BottomRadius = 0.016f, Height = 0.05f, RadialSegments = 10 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = (colours[s] * 0.8f) with { A = 1 } },
                Position = new Vector3(0, 0.04f, 0),
            });
            _mallets[s] = mallet;
            AddChild(mallet);
            _shownMallet[s] = new Vector2(0, (s == 0 ? -1 : 1) * (Field.Hz - 0.2f));
        }
        // the overhead scorer at the table's side
        var board = new MeshInstance3D
        {
            Name = "Scorer", Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.22f, 0.5f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.1f) },
            Position = new Vector3(Piece.W / 2 + 0.05f, 1.35f, 0),
        };
        AddChild(board);
        // on its post from the table's rail
        AddChild(new MeshInstance3D
        {
            Name = "ScorerPost", Mesh = new BoxMesh { Size = new Vector3(0.03f, 0.36f, 0.03f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.14f, 0.14f, 0.16f) },
            Position = new Vector3(Piece.W / 2 + 0.05f, 1.06f, 0),
        });
        _score = new Label3D
        {
            Name = "Score", Text = "0  :  0", FontSize = 64, PixelSize = 0.002f, Modulate = new Color(1f, 0.35f, 0.25f), Shaded = false,
            Transform = new Transform3D(new Basis(Vector3.Up, -Mathf.Pi / 2), new Vector3(Piece.W / 2 + 0.024f, 1.35f, 0)),
        };
        AddChild(_score);
        var back = (Label3D)_score.Duplicate();
        back.Name = "ScoreBack";
        back.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(Piece.W / 2 + 0.076f, 1.35f, 0));
        AddChild(back);
        _voice = new AudioStreamPlayer3D { Name = "Voice", Bus = Audio.SfxBus.Name, UnitSize = 4f, MaxDistance = 25f, Position = new Vector3(0, FieldY, 0) };
        AddChild(_voice);
    }

    public override void OnState(TableGame game, double now)
    {
        if (game is not AirHockeyGame g) return;
        string text = $"{g.Goals[0]}  :  {g.Goals[1]}";
        if (_score.Text != text)
        {
            _score.Text = text;
            GetNode<Label3D>("ScoreBack").Text = $"{g.Goals[1]}  :  {g.Goals[0]}";
        }
        int total = g.Goals[0] + g.Goals[1];
        if (_goals >= 0 && total > _goals) Play(AirHockeySounds.Goal, 0);
        _goals = total;
    }

    public override void Show(TableGame? game, double now, double delta)
    {
        if (game is not AirHockeyGame g) return;
        if (g.PuckAt != _lastPuck) { _lastPuck = g.PuckAt; _lastAt = now; }
        var target = g.PuckAt + g.PuckV * (float)Math.Min(now - _lastAt, 0.1);
        float k = 1 - Mathf.Exp(-(float)delta * 30f);
        _shownPuck = (_shownPuck - target).Length() > 0.25f ? target : _shownPuck.Lerp(target, k);
        _puck.Visible = g.InPlay;
        _puck.Position = new Vector3(_shownPuck.X, FieldY + 0.003f, _shownPuck.Y);
        for (int s = 0; s < 2; s++)
        {
            _shownMallet[s] = _shownMallet[s].Lerp(g.MalletAt[s], k);
            _mallets[s].Position = new Vector3(_shownMallet[s].X, FieldY + 0.015f, _shownMallet[s].Y);
        }
        // a clack when the puck's run turns hard: a mallet or a rail
        if (g.InPlay && (g.PuckV - _lastV).Length() > 1.2f)
            Play(AirHockeySounds.Clack, Mathf.LinearToDb(Mathf.Clamp(g.PuckV.Length() / 5, 0.25f, 1)));
        _lastV = g.PuckV;
    }

    /// <summary>Where the puck is shown (the check's view).</summary>
    public Vector2 ShownPuck => _shownPuck;

    private void Play(AudioStream s, float db)
    {
        _voice.Stream = s;
        _voice.VolumeDb = db;
        _voice.Position = new Vector3(_shownPuck.X, FieldY, _shownPuck.Y);
        _voice.Play();
    }
}
