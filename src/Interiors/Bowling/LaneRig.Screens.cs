using Godot;

namespace UnitSport.Interiors;

// A lane pair's two score screens (#1323): the lane's game as text, set only when it changes, and after
// each roll a slow-motion replay of the ball meeting the pins, from the frames the deck sent. The replay
// is a small scene of its own (deck, pins, ball, a camera low in front of the pins) in its own world,
// rendered into the screen only while it plays.
public partial class LaneRig
{
    /// <summary>A replay plays the deck's frames this much slower than they happened.</summary>
    public const float ReplaySpeed = 0.35f;
    /// <summary>At most this much of the deck's motion is replayed [s of the roll].</summary>
    private const float ReplayLength = 2.4f;

    private readonly Label3D?[] _scores = new Label3D?[2];
    private readonly string[] _scoreText = { "", "" };

    /// <summary>Lane <paramref name="lane"/>'s screen shows <paramref name="text"/> (a no-op when it already does).</summary>
    public void SetScore(int lane, string text)
    {
        if (_scoreText[lane] == text) return;
        _scoreText[lane] = text;
        var label = _scores[lane];
        if (label == null)
        {
            var g = Geometry;
            label = _scores[lane] = new Label3D
            {
                Name = $"Score{lane}", PixelSize = 0.0011f, FontSize = 28, OutlineSize = 0,
                Modulate = new Color(0.95f, 0.95f, 0.85f), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Shaded = false, DoubleSided = false,
                // on the screen's face, its top left corner; the screen faces −Z
                Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), new Vector3(g.LaneX(lane) + 0.4f, g.ScreenY + 0.47f, g.ScreenZ - 0.026f)),
            };
            AddChild(label);
        }
        label.Text = text;
        // three bowlers' two lines each fill the screen; more get a smaller hand
        int lines = text.Count(c => c == '\n');
        label.PixelSize = lines <= 6 ? 0.0021f : 0.0012f;
    }

    // ---- the replay ----------------------------------------------------------------------------

    private SubViewport? _replayView;
    private MeshInstance3D?[] _replayQuads = new MeshInstance3D?[2];
    private MultiMeshInstance3D? _replayPins;
    private MeshInstance3D? _replayBall;
    private List<(float Time, Transform3D[] Bodies)>? _replayFrames;
    private int _replayLane = -1;
    private float _replayT;
    /// <summary>The roll's word (STRIKE!...) swept over the replay near its end, Wii Sports style (#1336); null for none.</summary>
    private Label? _replayWord;
    private string? _replayWordText;
    /// <summary>The word comes in this long before the replay ends, and takes this long to sweep in [s].</summary>
    private const float WordLead = 2.3f, WordSweep = 0.35f;

    /// <summary>The lane whose screen plays a replay now, -1 for none (a camera watching it, #1336).</summary>
    public int ReplayLane => _replayLane;

    /// <summary>
    /// Plays a slow-motion replay of a roll on lane <paramref name="lane"/>'s screen: the deck's
    /// <paramref name="frames"/> (the ball first, then the pins, in the deck's frame) from the moment
    /// the ball reached it.
    /// </summary>
    public void PlayReplay(int lane, List<(float Time, Transform3D[] Bodies)> frames, int color, string? word = null)
    {
        if (frames.Count < 2) return;
        EnsureReplayScene();
        _replayWordText = word;
        _replayWord!.Visible = false;
        if (word != null) _replayWord.Text = word;
        if (_replayLane >= 0 && _replayLane != lane && _replayQuads[_replayLane] is { } was) was.Visible = false;
        _replayLane = lane;
        _replayFrames = frames;
        _replayT = 0;
        _replayBall!.Mesh = BallMesh(color);
        _replayView!.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        var quad = _replayQuads[lane] ??= ReplayQuad(lane);
        quad.Visible = true;
        if (_scores[lane] is { } label) label.Visible = false;
        SetProcess(true);
    }

    /// <summary>Steps the replay, if one plays; true while it does.</summary>
    private bool StepReplay(double delta)
    {
        if (_replayFrames is not { } frames || _replayLane < 0) return false;
        _replayT += (float)delta * ReplaySpeed;
        float end = Mathf.Min(frames[^1].Time, ReplayLength);
        if (_replayT > end + 0.4f)
        {
            if (_replayQuads[_replayLane] is { } quad) quad.Visible = false;
            if (_scores[_replayLane] is { } label) label.Visible = true;
            _replayView!.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            _replayFrames = null;
            _replayLane = -1;
            return false;
        }
        // the word: sweeping in from the right over the last seconds, a little too big and settling
        if (_replayWordText != null && _replayWord != null)
        {
            float left = (end + 0.4f - _replayT) / ReplaySpeed;
            float u = Mathf.Clamp((WordLead - left) / WordSweep, 0, 1);
            _replayWord.Visible = u > 0;
            if (u > 0)
            {
                float ease = 1 - (1 - u) * (1 - u);
                _replayWord.Position = new Vector2(Mathf.Lerp(_replayView!.Size.X, 0, ease), 0);
                _replayWord.Scale = Vector2.One * (1.25f - 0.25f * ease);
            }
        }
        int k = frames.FindLastIndex(f => f.Time <= _replayT);
        if (k < 0) k = 0;
        var a = frames[k];
        var b = k + 1 < frames.Count ? frames[k + 1] : a;
        float w = b.Time > a.Time ? Mathf.Clamp((_replayT - a.Time) / (b.Time - a.Time), 0, 1) : 0;
        var ball = a.Bodies[0].InterpolateWith(b.Bodies[0], w);
        _replayBall!.Visible = ball.Origin.Y > -0.5f;
        _replayBall.Transform = ball;
        var mm = _replayPins!.Multimesh;
        for (int i = 0; i < 10; i++)
        {
            var t = a.Bodies[i + 1].InterpolateWith(b.Bodies[i + 1], w);
            mm.SetInstanceTransform(i, t.Origin.Y < -0.35f ? Hidden : t);
        }
        return true;
    }

    /// <summary>The replay's own little world: the end of a lane, its pins and ball, lit, a camera low in front.</summary>
    private void EnsureReplayScene()
    {
        if (_replayView != null) return;
        var run = Geometry.Run;
        _replayView = new SubViewport
        {
            Name = "Replay", Size = new Vector2I(384, 216), OwnWorld3D = true, TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_replayView);
        var root = new Node3D { Name = "Deck" };
        _replayView.AddChild(root);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.03f, 0.03f, 0.06f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.55f, 0.6f),
        };
        root.AddChild(new WorldEnvironment { Environment = env });
        root.AddChild(new DirectionalLight3D { Rotation = new Vector3(-1.0f, 0.4f, 0), LightEnergy = 1.1f });
        var plain = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        // the deck and its gutters, the pit's black behind
        var deck = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(BowlingSizes.HalfLane * 2, 0.05f, 4f) },
            Position = new Vector3(0, -0.025f, run.HeadPin - 1.2f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.70f, 0.48f) },
        };
        root.AddChild(deck);
        foreach (float side in new[] { -1f, 1f })
            root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(BowlingSizes.Gutter, 0.02f, 4f) },
                Position = new Vector3(side * (BowlingSizes.HalfLane + BowlingSizes.Gutter / 2), -0.05f, run.HeadPin - 1.2f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.1f) },
            });
        _replayPins = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _pinMesh, InstanceCount = 10 },
            MaterialOverride = plain,
        };
        root.AddChild(_replayPins);
        _replayBall = new MeshInstance3D { MaterialOverride = plain };
        root.AddChild(_replayBall);
        // the roll's word over the picture: big, bright, outlined, centred
        _replayWord = new Label
        {
            Name = "Word", Visible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Size = new Vector2(_replayView.Size.X, _replayView.Size.Y), PivotOffset = new Vector2(_replayView.Size.X, _replayView.Size.Y) / 2,
            LabelSettings = new LabelSettings
            {
                FontSize = 54, FontColor = new Color(1f, 0.86f, 0.2f), OutlineSize = 10, OutlineColor = new Color(0.25f, 0.05f, 0.35f),
                ShadowSize = 4, ShadowColor = new Color(0, 0, 0, 0.6f), ShadowOffset = new Vector2(3, 3),
            },
        };
        _replayView.AddChild(_replayWord);
        // low, a little off to the side, as a lane's pin camera looks
        var cam = new Camera3D { Fov = 40f, Current = true };
        root.AddChild(cam);
        cam.Position = new Vector3(0.35f, 0.32f, run.HeadPin - 2.6f);
        cam.LookAt(new Vector3(0, 0.15f, run.HeadPin + 0.35f), Vector3.Up);
    }

    /// <summary>A quad on lane <paramref name="lane"/>'s screen showing the replay's picture.</summary>
    private MeshInstance3D ReplayQuad(int lane)
    {
        var g = Geometry;
        var quad = new MeshInstance3D
        {
            Name = $"ReplayScreen{lane}",
            Mesh = new QuadMesh { Size = new Vector2(0.84f, 0.46f) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoTexture = _replayView!.GetTexture(),
            },
            // the quad faces +Z; the screen faces −Z, toward the settees
            Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), new Vector3(g.LaneX(lane), g.ScreenY + 0.26f, g.ScreenZ - 0.025f)),
        };
        AddChild(quad);
        return quad;
    }
}
