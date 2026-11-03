using Godot;

namespace UnitSport.Interiors.Games.Pool;

/// <summary>
/// A pool table's balls, cue and sounds on a client (#1324). The table itself is the room's mesh; the sixteen balls
/// are one MultiMesh (colour and stripe per instance, a ball turned as far as it rolled), the cue a node. A shot the
/// server sends is replayed here from where the balls stood (<see cref="PoolGame.Before"/>) with the shot's numbers,
/// <see cref="StrokeTime"/> after the cue starts forward, and ends on the server's resting table.
/// </summary>
public partial class PoolRig : TableRig
{
    /// <summary>The cloth's top above the piece's floor [m] (the room mesh's cloth).</summary>
    public const float ClothY = 0.78f;
    /// <summary>The cue's stroke before the tip meets the ball [s].</summary>
    public const float StrokeTime = 0.22f;

    public static readonly Color[] Colours =
    {
        new(0.96f, 0.95f, 0.90f), new(0.98f, 0.80f, 0.10f), new(0.10f, 0.25f, 0.80f), new(0.85f, 0.12f, 0.10f),
        new(0.42f, 0.16f, 0.62f), new(0.98f, 0.45f, 0.08f), new(0.06f, 0.50f, 0.22f), new(0.50f, 0.08f, 0.10f),
        new(0.05f, 0.05f, 0.06f),
    };

    private const string BallShader = """
        shader_type spatial;
        varying float band;
        void vertex() {
            // the stripe: a band round the ball's own equator, so it turns as the ball rolls
            band = INSTANCE_CUSTOM.x > 0.5 ? step(abs(VERTEX.y), 0.016) : 1.0;
        }
        void fragment() {
            ALBEDO = mix(vec3(0.96, 0.95, 0.90), COLOR.rgb, band);
            ROUGHNESS = 0.18;
            SPECULAR = 0.7;
        }
        """;

    private PoolGame _idle = null!;
    private readonly PoolSolver _replay;
    private MultiMeshInstance3D _balls = null!;
    private readonly Basis[] _turn = new Basis[PoolSolver.Balls];
    private readonly Vector2[] _was = new Vector2[PoolSolver.Balls];
    private readonly bool[] _wasOn = new bool[PoolSolver.Balls];
    private Node3D _cue = null!;
    private MeshInstance3D _aim = null!;
    private int _shown = -1, _heard, _struck = -1;
    private readonly AudioStreamPlayer3D[] _voices = new AudioStreamPlayer3D[4];
    private int _voice;

    /// <summary>The aim the local player shows (their cue and line), or null: the cue at rest.</summary>
    public (float Angle, float Pull)? Aim { get; set; }
    /// <summary>The cue ball held in hand at this place on the cloth (the local player placing it), or null.</summary>
    public Vector2? Holding { get; set; }

    public (float Hx, float Hz) Cloth { get; }

    /// <summary>Where ball <paramref name="i"/> is shown on the cloth, or null when it is off (the check's view: headless, a MultiMesh reads its instances back at the origin).</summary>
    public Vector2? Shown(int i) => _wasOn[i] ? _was[i] : null;

    public PoolRig(FurniturePlan piece)
    {
        Init(piece);
        Cloth = PoolGame.Cloth(piece.W, piece.D);
        _replay = new PoolSolver(Cloth.Hx, Cloth.Hz);
    }

    public static PoolRig Create(FurniturePlan piece) => new(piece);

    public override void _Ready()
    {
        // a table nobody played since the venue opened: a fresh rack from its place
        _idle = new PoolGame(Cloth.Hx, Cloth.Hz, (int)(Piece.X * 131 + Piece.Z * 7));
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true,
            InstanceCount = PoolSolver.Balls,
            Mesh = new SphereMesh { Radius = PoolSolver.R, Height = PoolSolver.R * 2, RadialSegments = 14, Rings = 8 },
        };
        _balls = new MultiMeshInstance3D { Name = "Balls", Multimesh = mm, MaterialOverride = new ShaderMaterial { Shader = new Shader { Code = BallShader } } };
        AddChild(_balls);
        for (int i = 0; i < PoolSolver.Balls; i++)
        {
            _turn[i] = Basis.Identity;
            mm.SetInstanceColor(i, Colours[i % 8 == 0 && i != 0 ? 8 : i < 9 ? i : i - 8]);
            mm.SetInstanceCustomData(i, new Color(i > 8 ? 1 : 0, 0, 0, 0));
        }
        _cue = BuildCue();
        AddChild(_cue);
        _aim = new MeshInstance3D
        {
            Name = "AimLine", Visible = false,
            Mesh = new BoxMesh { Size = new Vector3(0.004f, 0.002f, 1f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1, 1, 1, 0.55f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha },
        };
        AddChild(_aim);
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer3D { Name = $"Voice{i}", Bus = Audio.SfxBus.Name, UnitSize = 4f, MaxDistance = 25f };
            AddChild(_voices[i]);
        }
        ShowBalls(_idle.Table, true);
    }

    /// <summary>A cue: a tapered maple shaft, the dark butt, the tip; it lies along +Z with its tip at the origin.</summary>
    private static Node3D BuildCue()
    {
        var cue = new Node3D { Name = "Cue", Visible = false };
        var shaft = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.0065f, BottomRadius = 0.0105f, Height = 0.95f, RadialSegments = 8 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.88f, 0.76f, 0.55f) },
            Transform = new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2), new Vector3(0, 0, -0.475f - 0.01f)),
        };
        var butt = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.0105f, BottomRadius = 0.0145f, Height = 0.5f, RadialSegments = 8 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.08f, 0.05f) },
            Transform = new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2), new Vector3(0, 0, -1.2f - 0.01f)),
        };
        var tip = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.0065f, BottomRadius = 0.0065f, Height = 0.012f, RadialSegments = 8 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.35f, 0.75f) },
            Transform = new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2), new Vector3(0, 0, -0.004f)),
        };
        cue.AddChild(shaft);
        cue.AddChild(butt);
        cue.AddChild(tip);
        return cue;
    }

    /// <summary>A point on the cloth in the piece's frame.</summary>
    public static Vector3 On(Vector2 p, float lift = PoolSolver.R) => new(p.X, ClothY + lift, p.Y);

    public override void OnState(TableGame game, double now)
    {
        if (game is not PoolGame g || g.ShotSeq == _shown) return;
        // a new shot: replayed from where the balls stood
        _shown = g.ShotSeq;
        _replay.CopyFrom(g.Before);
        PoolGame.Strike(_replay, g.Shot);
        _heard = 0;
        // a joiner in the middle of a shot starts where it is
        if (now - g.ShotAt > 3) _replay.Run();
    }

    public override void Show(TableGame? game, double now, double delta)
    {
        var g = game as PoolGame ?? _idle;
        float t = (float)(now - g.ShotAt) - StrokeTime;
        bool replaying = g.ShotSeq == _shown && _shown > 0 && t < g.ShotEnd - g.ShotAt && _replay.Moving;
        if (replaying && t > 0 && _struck != _shown)
        {
            _struck = _shown;
            Play(PoolSounds.Cue, On(g.Before.P[0]), 0);
        }
        if (replaying && t > 0)
        {
            _replay.RunTo(t);
            Hear(_replay);
            ShowBalls(_replay, false);
        }
        else if (!replaying || t > 0) ShowBalls(g.Table, false);
        else ShowBalls(g.Before, false);
        ShowCue(g, replaying ? t : 99f);
    }

    private void ShowCue(PoolGame g, float t)
    {
        var cueBall = Holding ?? (t < 0 ? g.Before.P[0] : g.Table.P[0]);
        if (t < 0 && t > -StrokeTime)
        {
            // the stroke: drawn back, through the ball
            float angle = g.Shot.Angle / 1000f * Mathf.Pi / 180f;
            float pull = 0.25f * (-t / StrokeTime);
            PlaceCue(g.Before.P[0], angle, pull);
            _aim.Visible = false;
            return;
        }
        if (Aim is { } aim && t > 90f)
        {
            PlaceCue(cueBall, aim.Angle, 0.03f + aim.Pull * 0.25f);
            var dir = new Vector2(Mathf.Sin(aim.Angle), Mathf.Cos(aim.Angle));
            float reach = AimReach(g, cueBall, dir);
            _aim.Visible = true;
            var mid = cueBall + dir * reach * 0.5f;
            _aim.Transform = new Transform3D(Basis.LookingAt(new Vector3(dir.X, 0, dir.Y), Vector3.Up).Scaled(new Vector3(1, 1, reach)), On(mid, 0.002f));
            return;
        }
        _cue.Visible = false;
        _aim.Visible = false;
    }

    /// <summary>How far the aim line reaches: to the first ball the cue ball would touch, or the cushion.</summary>
    private float AimReach(PoolGame g, Vector2 from, Vector2 dir)
    {
        float best = 3f;
        for (int i = 1; i < PoolSolver.Balls; i++)
        {
            if (!g.Table.On[i]) continue;
            var to = g.Table.P[i] - from;
            float along = to.Dot(dir);
            if (along <= 0) continue;
            float side2 = to.LengthSquared() - along * along;
            float r2 = 4 * PoolSolver.R * PoolSolver.R;
            if (side2 > r2) continue;
            best = Mathf.Min(best, along - Mathf.Sqrt(r2 - side2));
        }
        // the cushions
        float lx = Cloth.Hx - PoolSolver.R, lz = Cloth.Hz - PoolSolver.R;
        if (Mathf.Abs(dir.X) > 1e-4f) best = Mathf.Min(best, ((dir.X > 0 ? lx : -lx) - from.X) / dir.X);
        if (Mathf.Abs(dir.Y) > 1e-4f) best = Mathf.Min(best, ((dir.Y > 0 ? lz : -lz) - from.Y) / dir.Y);
        return Mathf.Max(0.01f, best);
    }

    private void PlaceCue(Vector2 ball, float angle, float pull)
    {
        var dir = new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle));
        // raised at the butt a little, as over a rail
        var tip = On(ball) - dir * (PoolSolver.R + pull);
        var back = (-dir + Vector3.Up * 0.12f).Normalized();
        _cue.Visible = true;
        _cue.Transform = new Transform3D(Basis.LookingAt(-back, Vector3.Up), tip);
    }

    private void ShowBalls(PoolSolver s, bool snap)
    {
        var mm = _balls.Multimesh;
        for (int i = 0; i < PoolSolver.Balls; i++)
        {
            bool on = s.On[i];
            var p = i == 0 && Holding is { } held ? held : s.P[i];
            if (on && _wasOn[i] && !snap)
            {
                var d = p - _was[i];
                float dist = d.Length();
                if (dist > 1e-5f && dist < 0.5f)
                {
                    var axis = Vector3.Up.Cross(new Vector3(d.X, 0, d.Y)).Normalized();
                    _turn[i] = new Basis(axis, dist / PoolSolver.R) * _turn[i];
                    _turn[i] = _turn[i].Orthonormalized();
                }
            }
            _was[i] = p;
            _wasOn[i] = on;
            // a potted ball is gone: under the table, out of sight
            mm.SetInstanceTransform(i, on ? new Transform3D(_turn[i], On(p)) : new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.001f), new Vector3(0, 0.1f, 0)));
        }
    }

    /// <summary>The shot's knocks as the replay passes them.</summary>
    private void Hear(PoolSolver s)
    {
        for (; _heard < s.Events.Count; _heard++)
        {
            var e = s.Events[_heard];
            var stream = e.Kind switch
            {
                PoolSolver.EventKind.Contact => PoolSounds.Clack,
                PoolSolver.EventKind.Cushion => PoolSounds.Cushion,
                _ => PoolSounds.Pocket,
            };
            float speed = s.V[e.Ball].Length();
            Play(stream, On(s.P[e.Ball]), e.Kind == PoolSolver.EventKind.Pocket ? 0 : Mathf.LinearToDb(Mathf.Clamp(speed / 3f, 0.08f, 1f)));
        }
    }

    private void Play(AudioStream stream, Vector3 at, float db)
    {
        var v = _voices[_voice++ % _voices.Length];
        v.Stream = stream;
        v.Position = at;
        v.VolumeDb = db;
        v.Play();
    }
}
