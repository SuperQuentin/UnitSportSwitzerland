using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Trailer;

/// <summary>
/// The trailer director (#706): stages and films the shots of <see cref="TrailerScript"/> one after
/// the other, in the real world with the game's own machines.
///
/// <para>
/// <c>godot --path . -- --trailer all|N|A-B|N,M [--trailer-record DIR] [--trailer-stills DIR]
/// [--trailer-song FILE] [--trailer-size 1920x1080]</c>
/// </para>
///
/// <para>
/// Per shot: the style and the hour are set, the origin moves to the place and the camera waits
/// there until the ground round it has settled; the actors are put down (on their road, mounted,
/// launched) and moved for the shot's pre-roll; then the camera rolls along its keys for the shot's
/// length, the captions on top. <c>--trailer-record</c> writes each shot as <c>shotNN.mp4</c>
/// (run with <c>--fixed-fps 30</c>: one frame is one 30th of game time however slowly it renders);
/// <c>--trailer-stills</c> saves the first, middle and last frame of each as PNGs instead, to frame
/// them; without either it plays in the window, with the song's part of each shot when given one.
/// The picture is <c>--trailer-size</c> (1920x1080) whatever the window's size.
/// </para>
/// </summary>
public partial class TrailerDirector : Node
{
    public static bool Requested => CmdArgs.Has("--trailer") || CmdArgs.Has("--trailer-scout");

    /// <summary>Runs a chat line (<c>/style cartoon</c>); the client world sends it to its chat.</summary>
    public System.Action<string>? RunCommand { get; set; }

    private enum Phase { Next, Stage, Place, Preroll, Roll, Finished }

    private readonly Camera3D _camera;
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly List<Shot> _shots;
    private readonly string? _recordDir = CmdArgs.Value("--trailer-record");
    private readonly string? _stillsDir = CmdArgs.Value("--trailer-stills");
    private readonly int _fps = CmdArgs.Int("--trailer-fps") ?? 30;
    private readonly Vector2I _size;
    private Captions _captions = null!;
    private AudioStreamPlayer? _song;

    private Phase _phase = Phase.Next;
    private int _index = -1;
    private Shot? _shot;
    private ShotCamera? _shotCamera;
    private readonly List<Actor> _actors = new();
    private RouteBook _routes = new(0);
    private readonly List<Node3D> _props = new();
    private double _t, _waitWall, _settledFor, _preroll;
    private ulong _phaseStartMs;
    private string _style = "";
    private float _sea = float.NaN;
    private FrameRecorder? _recorder;
    private int _framesWanted;
    private readonly Queue<(double T, string Path)> _stills = new();
    private int _failures;
    private readonly int _defaultTraffic = GameSettings.Current.TrafficCars;
    private List<CanvasLayer>? _hud;

    /// <summary>Wall seconds a shot may take to stage or place before it is filmed as it is.</summary>
    private const double StageTimeout = 150, PlaceTimeout = 60;
    /// <summary>Tile rings round the camera that must have settled, and for how long, before the actors come.</summary>
    private const int SettleRings = 3;
    private const double SettleHold = 2.0;

    public TrailerDirector(Camera3D camera, ChunkManager chunks, WorldOrigin origin)
    {
        Name = "TrailerDirector";
        _camera = camera;
        _chunks = chunks;
        _origin = origin;
        _shots = CmdArgs.Value("--trailer-scout") is { } scout ? Scout(scout) : Select(CmdArgs.Value("--trailer") ?? "all");
        _size = CmdArgs.Value("--trailer-size")?.Split('x') is [var w, var h] && int.TryParse(w, out int sw) && int.TryParse(h, out int sh)
            ? new Vector2I(sw, sh) : new Vector2I(1920, 1080);
        // the trailer drives itself: no tutorial cards, no hints
        ProcessPriority = 100;
    }

    /// <summary>
    /// "--trailer-scout E,N,H;E,N,H": a still looking straight down from H m over each spot, north
    /// up, through a 24 mm lens (H/1080 m a pixel at 1920x1080), to read a place's layout off.
    /// </summary>
    private static List<Shot> Scout(string arg) =>
        arg.Split(';').Select((spot, i) =>
        {
            var p = spot.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            return new Shot
            {
                Number = 100 + i, Name = $"scout {p[0]:F0},{p[1]:F0}", FromBar = 1, Bars = 1, Hour = 12, Preroll = 0,
                Keys = [new Key(0, Pt.At(p[0], p[1], (float)p[2]), Pt.At(p[0], p[1], 0), 24)],
            };
        }).ToList();

    /// <summary>"all", "5", "5-9" or "5,7,12" (shot numbers).</summary>
    private static List<Shot> Select(string arg)
    {
        var all = TrailerScript.Shots;
        if (arg == "all" || arg.Length == 0) return all.ToList();
        var want = new HashSet<int>();
        foreach (var part in arg.Split(','))
        {
            var range = part.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out int a) && int.TryParse(range[1], out int b))
                for (int k = a; k <= b; k++) want.Add(k);
            else if (int.TryParse(part, out int n)) want.Add(n);
        }
        return all.Where(s => want.Contains(s.Number)).ToList();
    }

    public override void _Ready()
    {
        // the film's frame size, whatever the window: the root renders at it and is scaled to fit
        var root = GetTree().Root;
        root.ContentScaleMode = Window.ContentScaleModeEnum.Viewport;
        root.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;
        root.ContentScaleSize = _size;
        AddChild(_captions = new Captions());
        _captions.Clear(black: true);
        if (_recordDir != null && !GameClock.Fixed)
            GD.PrintErr("[trailer] --trailer-record without --fixed-fps: frames follow the wall clock, the film will stutter");
        if (_recordDir == null && _stillsDir == null && CmdArgs.Value("--trailer-song") is { } songPath) LoadSong(songPath);
        RenderingServer.FramePostDraw += OnFrameDrawn;
        // the cut as a whole, whichever shots this run films: no gap, no overlap, the song's length
        var problems = Song.CutProblems(TrailerScript.Shots.Select(s => (s.Number, s.FromBar, s.Bars)).ToList())
            .Concat(TrailerScript.Shots.Where(s => s.Keys.Count == 0).Select(s => $"shot {s.Number} has no camera key"));
        foreach (var problem in problems)
        {
            GD.PrintErr($"[trailer] cut: {problem}");
            _failures++;
        }
        GD.Print($"[trailer] {_shots.Count} shot(s): {string.Join(" ", _shots.Select(s => s.Number))}; "
            + (_recordDir != null ? $"recording to {_recordDir} at {_fps} fps, {_size.X}x{_size.Y}" : _stillsDir != null ? $"stills to {_stillsDir}" : "preview"));
    }

    public override void _ExitTree()
    {
        RenderingServer.FramePostDraw -= OnFrameDrawn;
        _recorder?.Dispose();
        _recorder = null;
        if (_hud != null) GetTree().NodeAdded -= OnNodeAdded;
        GameSettings.Current.TrafficCars = _defaultTraffic;
    }

    private void LoadSong(string path)
    {
        if (!File.Exists(path)) { GD.PrintErr($"[trailer] no song at {path}"); return; }
        AudioStream stream = path.EndsWith(".ogg", System.StringComparison.OrdinalIgnoreCase)
            ? AudioStreamOggVorbis.LoadFromFile(path)
            : new AudioStreamMP3 { Data = File.ReadAllBytes(path) };
        _song = new AudioStreamPlayer { Stream = stream, Name = "TrailerSong" };
        AddChild(_song);
    }

    public override void _PhysicsProcess(double delta)
    {
        foreach (var a in _actors) a.Step(delta, _shot?.Number ?? 0);
        if (_phase == Phase.Place) Place();
    }

    public override void _Process(double delta)
    {
        HideHud();
        _camera.Current = true;
        switch (_phase)
        {
            case Phase.Next: Next(); break;
            case Phase.Stage: Stage(delta); break;
            case Phase.Place:
                if (Wall() > PlaceTimeout) StartPreroll("placing timed out");
                break;
            case Phase.Preroll: Preroll(delta); break;
            case Phase.Roll: Roll(delta); break;
        }
    }

    private double Wall() => (Time.GetTicksMsec() - _phaseStartMs) / 1000.0;

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseStartMs = Time.GetTicksMsec();
    }

    // --- one shot ----------------------------------------------------------------------------

    private void Next()
    {
        foreach (var a in _actors) a.Free();
        _actors.Clear();
        foreach (var p in _props) p.QueueFree();
        _props.Clear();
        _index++;
        if (_index >= _shots.Count)
        {
            Enter(Phase.Finished);
            GD.Print(_failures == 0 ? "[trailer] RESULT: ok" : $"[trailer] RESULT: FAILED ({_failures} problem(s), above)");
            GetTree().Quit(_failures == 0 ? 0 : 1);
            return;
        }
        var shot = _shot = _shots[_index];
        _routes = new RouteBook(shot.Number);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[trailer] shot {shot.Number} \"{shot.Name}\" (bars {shot.FromBar}-{shot.FromBar + shot.Bars - 1}, {shot.Start:F2}-{shot.End:F2} s, {shot.Length:F2} s): staging"));
        _captions.Clear(black: _recordDir == null && _stillsDir == null);

        if (shot.Style != _style)
        {
            RunCommand?.Invoke($"/style {shot.Style}");
            _style = shot.Style;
        }
        // every shot its own sea: a storm must not carry over into the next lake
        float sea = shot.Sea ?? Shot.CalmSea;
        if (sea != _sea) RunCommand?.Invoke(string.Create(CultureInfo.InvariantCulture, $"/seastate {sea:F2}"));
        _sea = sea;
        GameSettings.Current.TrafficCars = shot.Traffic ?? _defaultTraffic;
        World.Traffic.Current?.Forget();
        HoldClock(shot.Hour);

        // the origin to the place first, so it is near the camera rather than 100 km off
        var focus = Focus(shot);
        OriginShifter.Instance?.ShiftTo(focus.E, focus.N);
        _camera.GlobalPosition = _origin.ToWorld(focus.E, focus.N, 0) with { Y = 3000f };
        _shotCamera = new ShotCamera(shot)
        {
            Place = PlaceSpot,
            Actor = i => i >= 0 && i < _actors.Count ? _actors[i].Frame : null,
            Surface = p => _chunks.TryGetSurface(p, out float g) ? g : null,
            Road = (key, at) => _routes.Point(key, at, _chunks),
        };
        _settledFor = 0;
        _waitWall = 0;
        Enter(Phase.Stage);
    }

    /// <summary>Where a shot happens: its first world key's spot, else its first actor's.</summary>
    private static Spot Focus(Shot shot)
    {
        foreach (var k in shot.Keys) if (k.Eye.World is { } s) return s;
        if (shot.Cast.Count > 0) return shot.Cast[0].At;
        foreach (var k in shot.Keys) if (k.Look.World is { } s) return s;
        return new Spot(2600000, 1200000, 500);
    }

    private Vector3? PlaceSpot(Spot s)
    {
        var at = _origin.ToWorld(s.E, s.N, s.Agl ? 0 : s.H);
        if (!s.Agl) return at;
        if (!_chunks.TryGetSurface(at, out float g)) return null;
        return at with { Y = g + s.H };
    }

    /// <summary>A prop: a solid box on the ground, its face to <see cref="Prop.Bearing"/>.</summary>
    private void Build(Prop prop)
    {
        var at = _origin.ToWorld(prop.At.E, prop.At.N, 0);
        float bearing = prop.Bearing;
        if (prop.Actor >= 0)
        {
            if (prop.Actor >= _actors.Count || _actors[prop.Actor].Body is not { } who) return;
            var ahead = (-who.GlobalBasis.Z with { Y = 0 }).Normalized();
            at = who.GlobalPosition + ahead * prop.Ahead;
            bearing = Mathf.RadToDeg(Mathf.Atan2(ahead.X, -ahead.Z));
        }
        else if (prop.Route != null)
        {
            if (_routes.Point(prop.Route, new Vector3(0f, 0f, prop.Arc), _chunks) is not { } on
                || _routes.Point(prop.Route, new Vector3(0f, 0f, prop.Arc + 3f), _chunks) is not { } next) return;
            at = on;
            var along = (next - on) with { Y = 0 };
            bearing = Mathf.RadToDeg(Mathf.Atan2(along.X, -along.Z));
        }
        float g = _chunks.TryGetHeight(at, out float h) ? h : 0f;
        var body = new StaticBody3D { Name = $"Prop{_props.Count}" };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = prop.Size } });
        body.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = prop.Size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = prop.Colour, Roughness = 0.9f },
        });
        AddChild(body);
        float b = Mathf.DegToRad(bearing);
        var face = new Vector3(Mathf.Sin(b), 0f, -Mathf.Cos(b));
        body.GlobalTransform = new Transform3D(Basis.LookingAt(face, Vector3.Up), at with { Y = g + prop.Size.Y * 0.5f - 0.3f });
        _props.Add(body);
    }

    /// <summary>The clock at <paramref name="hour"/>, standing still (a day of a thousand hours).</summary>
    private static void HoldClock(double hour)
    {
        if (World.DayNight.Instance is not { } clock) return;
        clock.Hour = hour;
        clock.DayLengthOverride = 60000f;
    }

    private void Stage(double delta)
    {
        var shot = _shot!;
        // the camera at its first key while the ground streams in (an actor's key: high over the place)
        var eye = shot.Keys[0].Eye.World is { } s ? PlaceSpot(s) : null;
        if (eye is { } e) _camera.GlobalPosition = e;
        else
        {
            var f = Focus(shot);
            var over = _origin.ToWorld(f.E, f.N, 0);
            _camera.GlobalPosition = over with { Y = (_chunks.TryGetSurface(over, out float g) ? g : 500f) + 40f };
        }
        if (shot.Keys[0].Look.World is { } look && PlaceSpot(look) is { } lp && lp != _camera.GlobalPosition)
            _camera.LookAt(lp, Vector3.Up);

        bool settled = eye != null || shot.Keys[0].Eye.World == null;
        settled &= _chunks.SettledNear(_camera.GlobalPosition, SettleRings);
        _settledFor = settled ? _settledFor + delta : 0;
        if (_settledFor < SettleHold && Wall() < StageTimeout) return;
        if (_settledFor < SettleHold)
        {
            GD.PrintErr($"[trailer] shot {shot.Number}: the ground never settled in {StageTimeout:F0} s; filming it as it is");
            _failures++;
        }
        GD.Print($"[trailer] shot {shot.Number}: ground in after {Wall():F1} s, placing {shot.Cast.Count} actor(s)");
        for (int i = 0; i < shot.Cast.Count; i++)
            _actors.Add(new Actor(shot.Cast[i], i, this, _chunks, _origin, _routes));
        foreach (var prop in shot.Props.Where(p => p.Actor < 0 && p.Route == null)) Build(prop);
        Enter(Phase.Place);
    }

    private void Place()
    {
        foreach (var a in _actors) a.Prepare();
        if (_actors.Any(a => !a.Ready && a.Failed == null)) return;
        StartPreroll(null);
    }

    private void StartPreroll(string? why)
    {
        var shot = _shot!;
        foreach (var a in _actors.Where(a => !a.Ready))
        {
            GD.PrintErr($"[trailer] shot {shot.Number}: actor {a.Index} ({a.Spec.Ride}) left out: {a.Failed ?? why}");
            _failures++;
        }
        foreach (var prop in shot.Props.Where(p => p.Actor >= 0 || p.Route != null)) Build(prop);
        foreach (var a in _actors) a.Go(Others);
        _preroll = 0;
        _shotCamera!.Reset();
        GD.Print($"[trailer] shot {shot.Number}: actors moving, {shot.Preroll:F1} s of pre-roll");
        Enter(Phase.Preroll);
    }

    /// <summary>Every other actor with an autopilot, for one's racecraft.</summary>
    private IEnumerable<AutoPilot.Other> Others(Actor me)
    {
        foreach (var a in _actors)
            if (a != me && a.Body is { } b && GodotObject.IsInstanceValid(b))
                yield return new AutoPilot.Other(b.GlobalPosition, b.WorldVelocity, false);
    }

    private void Preroll(double delta)
    {
        var shot = _shot!;
        _preroll += delta;
        _shotCamera!.Apply(_camera, 0, delta);
        if (_preroll < shot.Preroll) return;
        // the camera has to be placeable (its actors there) before the film starts
        if (!_shotCamera.Apply(_camera, 0, delta) && _preroll < shot.Preroll + 10) return;
        StartRoll();
    }

    private void StartRoll()
    {
        var shot = _shot!;
        _t = 0;
        _shotCamera!.Reset();
        if (World.DayNight.Instance is { } clock)
        {
            clock.Hour = shot.Hour;
            clock.DayLengthOverride = shot.MinutesPerDay ?? 60000f;
        }
        // from the song's clock, not the shot's length: every cut stays within a frame of its bar line
        _framesWanted = (int)System.Math.Round(shot.End * _fps) - (int)System.Math.Round(shot.Start * _fps);
        if (_recordDir != null)
        {
            string path = Path.Combine(_recordDir, $"shot{shot.Number:00}.mp4");
            _recorder = FrameRecorder.Start(path, _size.X, _size.Y, _fps);
            if (_recorder == null) { _failures++; GD.PrintErr($"[trailer] shot {shot.Number}: no recorder, not filmed"); }
        }
        _stills.Clear();
        if (_stillsDir != null)
        {
            Directory.CreateDirectory(_stillsDir);
            foreach (var (f, tag) in new[] { (0.0, "a"), (0.5, "b"), (1.0, "c") })
                _stills.Enqueue((System.Math.Min(f * shot.Length, shot.Length - 1.0 / _fps), Path.Combine(_stillsDir, $"shot{shot.Number:00}{tag}.png")));
        }
        if (_song != null)
        {
            _song.Play((float)shot.Start);
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[trailer] shot {shot.Number}: rolling, {shot.Length:F2} s ({_framesWanted} frames)"));
        Enter(Phase.Roll);
        Frame(0);
    }

    private void Roll(double delta)
    {
        var shot = _shot!;
        // recording: time is frames written (exact); otherwise it is the clock
        _t = _recorder != null ? _recorder.Frames / (double)_fps : _t + delta;
        if (_recorder == null && _t >= shot.Length)
        {
            EndShot();
            return;
        }
        Frame(delta);
    }

    /// <summary>The camera and the captions for this frame of the roll.</summary>
    private void Frame(double delta)
    {
        var shot = _shot!;
        _shotCamera!.Apply(_camera, _t, delta);
        _captions.Show(shot.Captions, _t, shot.Length, shot.FadeIn, shot.FadeOut);
    }

    /// <summary>After the renderer has drawn: the frame into the film, or a still when one is due.</summary>
    private void OnFrameDrawn()
    {
        if (_phase != Phase.Roll) return;
        if (_recorder != null)
        {
            _recorder.Write(GetViewport());
            if (_recorder.Frames >= _framesWanted) EndShot();
            return;
        }
        if (_stills.Count > 0 && _t >= _stills.Peek().T)
        {
            var (_, path) = _stills.Dequeue();
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng(path);
            GD.Print($"[trailer] still {path}");
        }
    }

    private void EndShot()
    {
        var shot = _shot!;
        if (_recorder != null)
        {
            int frames = _recorder.Frames;
            string path = _recorder.Path;
            bool broken = _recorder.Error != null;
            _recorder.Dispose();
            _recorder = null;
            GD.Print($"[trailer] shot {shot.Number}: wrote {path}, {frames} frames");
            if (broken) _failures++;
        }
        _song?.Stop();
        GD.Print($"[trailer] shot {shot.Number}: done");
        Enter(Phase.Next);
    }

    // --- the game's own layers off the picture ------------------------------------------------

    /// <summary>Every CanvasLayer but the captions hidden: walked once, then followed through NodeAdded.</summary>
    private void HideHud()
    {
        if (_hud == null)
        {
            _hud = new List<CanvasLayer>();
            foreach (var node in GetTree().Root.FindChildren("*", "CanvasLayer", true, false))
                if (node is CanvasLayer layer && layer != _captions) _hud.Add(layer);
            GetTree().NodeAdded += OnNodeAdded;
        }
        _hud.RemoveAll(l => !IsInstanceValid(l));
        foreach (var layer in _hud) layer.Visible = false;
    }

    private void OnNodeAdded(Node node)
    {
        if (node is CanvasLayer layer && layer != _captions) _hud!.Add(layer);
    }
}
