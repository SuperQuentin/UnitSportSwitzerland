using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --churchstagecheck[,out.png]</c> (#370): a hand-made church (nave, step,
/// altar, the pastor rat, its radio, the congregation in the front pews), no map and no server. The
/// shipped chess type beat is burnt (ffmpeg), played on the church radio, and checked: five camera
/// cuts in the intro and the player's own camera back on the trumpet, the congregation up and
/// dancing different moves, the night club on; then stopped, and everything must be exactly as it
/// was in the same frame. With a shot (windowed), writes <c>_rest</c>, <c>_intro0..4</c>,
/// <c>_dance</c> and <c>_stopped</c> beside it, and compares rest with stopped pixel for pixel.
/// </summary>
public partial class ChurchStageProbe : Node3D
{
    private const string Plan = "probe_church";
    private readonly string? _shot;
    private Camera3D _own = null!;
    private InteriorNode _church = null!;
    private ChurchStage _stage = null!;
    private int _step;
    private double _t, _total;
    private bool _ok = true;
    private Image? _restImage;
    private readonly HashSet<Vector3> _cuts = new();
    private int _shotHit = -1;
    private Transform3D[] _restParts = Array.Empty<Transform3D>();
    private Items.RadioBody? _thrown;

    public ChurchStageProbe(string? shot) => _shot = shot;

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--churchstagecheck");

    private bool Windowed => _shot != null && DisplayServer.GetName() != "headless";

    private void Check(bool condition, string what)
    {
        GD.Print($"[churchstage] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.05f, 0.05f, 0.08f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.9f, 0.9f, 0.9f),
            },
        });
        CdLibrary.Create(this, server: false);
        ChurchRadios.Create(this);
        ChurchStage.ProbePlan = Plan;

        var layout = Church();
        var data = InteriorMeshBuilder.Build(layout);
        Check(data.Figures is { Length: > 2 }, $"the rat and its congregation are figures ({data.Figures?.Length ?? 0})");
        _church = InteriorNode.Create(layout, data, Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        AddChild(_church);
        _stage = _church.GetNode<ChurchStage>("ChurchStage");
        // the music comes from the radio by the rat, not from the middle of the nave (#375)
        if (ChurchRadios.RadioSpot(layout, out var spot))
        {
            var speaker = ChurchRadios.SpeakerAt(_church, spot);
            Check(speaker.SourceOffset.DistanceTo(spot) < 0.01f, $"the church radio is heard from the radio ({speaker.SourceOffset} vs {spot})");
            speaker.QueueFree();
        }
        else Check(false, "the church has a radio spot");

        // a radio thrown across a room flies like one thrown in the street (#388): it settled on
        // its first tick anywhere below y -500, and every room is 3 km down
        var origin = new WorldOrigin(0, 0);
        float floorY = InteriorManager.InteriorBaseY;
        var floor = new StaticBody3D { Name = "DeepFloor", Position = new Vector3(0, floorY, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60, 0.2f, 60) }, Position = new Vector3(0, -0.1f, 0) });
        AddChild(floor);
        _thrown = Items.RadioBody.Create(new Items.RadioState("", 0, origin.ToGlobal(new Vector3(0, floorY + 1.2f, 0)), 0, new Vector3(8f, 2f, 0)), origin);
        AddChild(_thrown);

        _own = new Camera3D { Name = "Own", Fov = 70f, Position = new Vector3(0.4f, 1.6f, 2.0f) };
        AddChild(_own);
        _own.LookAt(new Vector3(1.2f, 1.0f, 10.5f), Vector3.Up);
        _own.MakeCurrent();
    }

    /// <summary>A nave 12 x 24 m, two storeys tall, furnished as <c>FurnishChurch</c> would.</summary>
    private static InteriorLayout Church()
    {
        var l = new InteriorLayout
        {
            Key = Plan, Kind = BuildingKind.Sacral, Type = BuildingType.Church, StoreyHeight = 4f,
            Width = 12, Depth = 24, EntryWidth = 1.6f,
        };
        l.Floors.Add(new FloorPlan { Rooms = { new RoomPlan { X0 = -6, Z0 = -12, X1 = 6, Z1 = 12, Type = RoomType.Nave, Span = 2 } } });
        void F(FurnitureType t, float x, float z, float w, float d, float h, int turns = 2, float lift = 0) =>
            l.Furniture.Add(new FurniturePlan { Type = t, X = x, Z = z, W = w, D = d, H = h, Turns = turns, Lift = lift });
        F(FurnitureType.Dais, 0, 9.6f, 9f, 4f, 0.3f, 0);
        F(FurnitureType.Altar, 0, 10.4f, 2.2f, 0.9f, 1f, 2, 0.3f);
        F(FurnitureType.PastorRat, 1.85f, 10.4f, 0.9f, 0.45f, 1.35f, 2, 0.3f);
        F(FurnitureType.ChurchRadio, 2.7f, 10.4f, 0.5f, 0.4f, 1.05f, 2, 0.3f);
        F(FurnitureType.FrontPew, -2.9f, 5.6f, 4.2f, 0.55f, 0.9f, 0);
        F(FurnitureType.FrontPew, 2.9f, 5.6f, 4.2f, 0.55f, 0.9f, 0);
        for (int r = 1; r <= 4; r++)
            foreach (float x in new[] { -2.9f, 2.9f })
                F(FurnitureType.Pew, x, 5.6f - r * 1.05f, 4.2f, 0.55f, 0.9f, 0);
        return l;
    }

    public override void _Process(double delta)
    {
        _t += delta;
        _total += delta;
        if (_total > 240) { Check(false, $"finished in time (stuck at step {_step})"); Finish(); return; }
        if (_thrown != null && _total > 0.5)
        {
            Check(_thrown.Position.X > 2.5f, $"a radio thrown in interior space flies ({_thrown.Position.X:F2} m in 0.5 s, settled {_thrown.Settled})");
            _thrown.QueueFree();
            _thrown = null;
        }
        var lib = CdLibrary.Instance!;
        switch (_step)
        {
            case 0:
                // the shipped beat, burnt once into the library
                if (lib.RatBeatId < 0) return;
                var cd = lib.Find(lib.RatBeatId)!;
                Check(cd.Source == CdLibrary.RatBeatSource && cd.Duration > 100, $"the chess type beat is a CD: {cd.Describe()}");
                GD.Print($"[churchstage] beat offset {cd.BeatOffset:F3} s, hits at "
                    + string.Join(", ", Enumerable.Range(0, ChurchStage.IntroHits).Select(k => ChurchStage.HitTime(cd, k).ToString("F2"))));
                Check(ChurchStage.HitTime(cd, ChurchStage.IntroHits - 1) < ChurchStage.IntroEndOf(cd), $"five hits before the trumpet at {ChurchStage.IntroEndOf(cd):F2} s");
                Check(ChurchRadios.Instance!.PlayOf(Plan) == null && ChurchRadios.Instance.ModeOf(Plan) == Items.RadioMode.Repeat,
                    "an untouched church radio is silent, on repeat");
                Next();
                break;
            case 1:
                if (_t < 0.5) return;
                _restParts = PartTransforms();
                if (Windowed) _restImage = Shot("_rest");
                ChurchRadios.Instance!.Play(Plan, lib.RatBeatId, lib.Find(lib.RatBeatId)!.Duration);
                Next();
                break;
            case 2:
            {
                // the intro: the camera is not the player's, and cuts on every hit
                var play = ChurchRadios.Instance!.PlayOf(Plan);
                double t = ClockSync.ServerNow - (play?.StartedAt ?? 0);
                var cam = GetViewport().GetCamera3D();
                float end = ChurchStage.IntroEndOf(lib.Find(lib.RatBeatId)!);
                if (t < end - 0.05)
                {
                    if (_t > 0.1 && cam == _own) { Check(false, "the intro takes the camera"); Next(); return; }
                    if (cam != _own && cam != null) _cuts.Add(cam.GlobalPosition.Snapped(Vector3.One * 0.01f));
                    int hit = ChurchStage.HitAt(lib.Find(lib.RatBeatId)!, (float)t);
                    if (Windowed && hit > _shotHit && t > ChurchStage.HitTime(lib.Find(lib.RatBeatId)!, Math.Max(hit, 0)) + 0.15)
                    {
                        _shotHit = hit;
                        Shot($"_intro{hit}");
                    }
                    Check(UiFocus.TextEntryActive || _t < 0.05, "the controls wait during the intro");
                    return;
                }
                if (t < end + ChurchStage.HopTime)
                {
                    // the jump on the trumpet, still on the intro's last shot
                    if (cam == _own) { Check(false, "the last shot holds through the jump"); Next(); return; }
                    if (Windowed && _shotHit < ChurchStage.IntroHits && t > end + ChurchStage.HopTime * 0.45)
                    {
                        _shotHit = ChurchStage.IntroHits;
                        Shot("_jump");
                    }
                    return;
                }
                if (t < end + ChurchStage.HopTime + 0.3) return;
                Check(_cuts.Count >= ChurchStage.IntroHits, $"the camera cut on every hit ({_cuts.Count} angles)");
                Check(cam == _own, "the player's own camera is back after the jump");
                Check(!UiFocus.TextEntryActive, "the controls are back");
                Next();
                break;
            }
            case 3:
            {
                if (_t < 1.5) return;
                // dancing: the rat moved, everyone up, not all doing the same
                int standing = 0, people = 0;
                var torsos = new HashSet<Quaternion>();
                foreach (var figure in _stage.GetChildren())
                {
                    if (figure is not Node3D root || !root.Name.ToString().StartsWith("Figure")) continue;
                    var parts = root.FindChildren("Part*", "Node3D", true, false).OfType<Node3D>().ToList();
                    if (parts.Count < 6) continue;
                    // a congregant's head hangs under its torso (Part2); the rat's under its body (Part1)
                    if (root.GetNodeOrNull<Node3D>("Part2/Part5") == null) continue;
                    var legs = root.GetNode<Node3D>("Part1");
                    people++;
                    if (legs.Visible) standing++;
                    torsos.Add(root.GetNode<Node3D>("Part2").Quaternion.Normalized());
                }
                Check(people > 2 && standing == people, $"the congregation is up ({standing} of {people})");
                Check(torsos.Count > people / 2, $"they dance different moves ({torsos.Count} poses among {people})");
                Check(!PartTransforms().SequenceEqual(_restParts), "the figures move");
                var disco = _stage.GetNodeOrNull<Node3D>("Disco");
                Check(disco is { Visible: true }, "the night club is on");
                var room = _church.GetNode<MeshInstance3D>("Mesh");
                Check(room.GetInstanceShaderParameter("disco").AsSingle() > 0.5f, "the walls are in the disco");
                if (Windowed) Shot("_dance");
                // a player walks up in front of the left-hand row
                _visitor = new Node3D { Name = "Visitor", Position = new Vector3(-4.0f, 0, 7.6f) };
                AddChild(_visitor);
                ChurchStage.ProbeWatchers = () => new[] { _visitor };
                Next();
                break;
            }
            case 4:
            {
                if (_t < 1.5) return;
                var (f, want, rat) = NearestCongregant(_visitor!.Position);
                float yaw = _stage.YawOf(f);
                Check(Mathf.Abs(Mathf.AngleDifference(yaw, want)) < 0.25f,
                    $"the dancer nearest a player in front turns to face them (yaw {yaw:F2}, wants {want:F2}, the rat is at {rat:F2})");
                if (Windowed) Shot("_facing");
                // and walks back past the row
                _visitor.Position = new Vector3(-4.0f, 0, 2.0f);
                Next();
                break;
            }
            case 5:
            {
                if (_t < 1.5) return;
                var (f, want, rat) = NearestCongregant(new Vector3(-4.0f, 0, 7.6f));
                float yaw = _stage.YawOf(f);
                Check(Mathf.Abs(Mathf.AngleDifference(yaw, rat)) < 0.25f,
                    $"with the player behind them they look back to the rat (yaw {yaw:F2}, the rat at {rat:F2})");
                ChurchStage.ProbeWatchers = null;
                ChurchRadios.Instance!.Stop(Plan);
                Next();
                break;
            }
            case 6:
            {
                // one frame after the stop: exactly as before
                Check(PartTransforms().SequenceEqual(_restParts), "every figure is back in its place at once");
                Check(_stage.GetNodeOrNull<Node3D>("Disco") is not { Visible: true }, "the lights are gone at once");
                Check(_church.GetNode<MeshInstance3D>("Mesh").GetInstanceShaderParameter("disco").AsSingle() < 0.5f, "the walls are back at once");
                Check(GetViewport().GetCamera3D() == _own, "the player's camera");
                Next();
                break;
            }
            case 7:
                if (_t < 0.5) return;
                if (Windowed && _restImage != null)
                {
                    var after = Shot("_stopped");
                    float diff = MeanDiff(_restImage, after);
                    Check(diff < 0.002f, $"the church looks as it did before the music ({diff:F4} mean difference)");
                }
                Finish();
                break;
        }
    }

    private Node3D? _visitor;

    /// <summary>The congregant whose standing spot is nearest <paramref name="at"/>: its index, the yaw toward <paramref name="at"/>, and toward the rat.</summary>
    private (int F, float Want, float Rat) NearestCongregant(Vector3 at)
    {
        var figures = _stage.Figures;
        int best = -1;
        float bestD = float.MaxValue;
        Vector3 rat = Vector3.Zero;
        for (int f = 0; f < figures.Count; f++)
        {
            if (figures[f].Kind == FigureKind.Rat) { rat = figures[f].Frame.Origin; continue; }
            float d = (figures[f].Frame * _stage.StandSpot(f)).DistanceTo(at);
            if (d < bestD) { bestD = d; best = f; }
        }
        var inv = figures[best].Frame.AffineInverse();
        var spot = _stage.StandSpot(best);
        var toAt = inv * at - spot;
        var toRat = inv * rat - spot;
        return (best, Mathf.Atan2(toAt.X, toAt.Z), Mathf.Atan2(toRat.X, toRat.Z));
    }

    private void Next()
    {
        _step++;
        _t = 0;
    }

    private Transform3D[] PartTransforms() =>
        _stage.FindChildren("Part*", "Node3D", true, false).OfType<Node3D>().Select(n => n.Transform).ToArray();

    private Image Shot(string suffix)
    {
        var image = GetViewport().GetTexture().GetImage();
        image.SavePng(_shot!.Replace(".png", $"{suffix}.png"));
        return image;
    }

    private static float MeanDiff(Image a, Image b)
    {
        if (a.GetSize() != b.GetSize()) return 1f;
        double sum = 0;
        int n = 0;
        for (int y = 0; y < a.GetHeight(); y += 2)
            for (int x = 0; x < a.GetWidth(); x += 2, n++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
            }
        return (float)(sum / (3.0 * n));
    }

    private void Finish()
    {
        GD.Print($"[churchstage] RESULT: {(_ok ? "ok" : "FAILED")}");
        ChurchStage.ProbePlan = null;
        ChurchStage.ProbeWatchers = null;
        GetTree().Quit(_ok ? 0 : 1);
        SetProcess(false);
    }
}
