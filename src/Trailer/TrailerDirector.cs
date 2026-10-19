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
    /// <summary>The shot's film set (<see cref="Shot.Set"/>), freed with the props.</summary>
    private Node3D? _set;
    /// <summary>
    /// How high a set is put, above the sea, m: under every Swiss ground (the lowest, Lake Maggiore,
    /// is at 193 m) and far above the interiors' floor (<c>InteriorBaseY</c>, −3000).
    /// </summary>
    private const float SetAltitude = -400f;
    private double _t, _waitWall, _settledFor, _drawnFor, _preroll;
    private double _phaseStart;
    private string _style = "";
    private Texture2D? _photo;
    private bool _photoAsked;
    private float _sea = float.NaN;
    /// <summary>The occasions the shots asked for (<see cref="Shot.Occasion"/>), null while the calendar's run.</summary>
    private string? _occasion;
    /// <summary>The shot's item cues already played (<see cref="Shot.ItemCues"/>), by index.</summary>
    private readonly HashSet<int> _cued = new();
    /// <summary>The shot's UFO props (<see cref="Shot.Craft"/>), by index.</summary>
    private readonly List<CraftProp> _craft = new();
    /// <summary>Seconds since the actors were let go (the pre-roll included): the clock of their scripts and the craft's switches.</summary>
    private double _moving = -1;
    /// <summary>The game screen shown on cue (<see cref="Shot.Screens"/>), its layer, and which cue it is.</summary>
    private CanvasLayer? _screenLayer;
    private int _screenShown = -1;
    private FrameRecorder? _recorder;
    private int _framesWanted;
    private readonly Queue<(double T, string Path)> _stills = new();
    private int _failures;
    private readonly int _defaultTraffic = GameSettings.Current.TrafficCars;
    private List<CanvasLayer>? _hud;

    /// <summary>Wall seconds a shot may take to stage or place before it is filmed as it is.</summary>
    private const double StageTimeout = 150, PlaceTimeout = 60;
    /// <summary>
    /// Tile rings round the camera that must be complete (<see cref="ChunkManager.CompleteNear"/>: each
    /// tile at its wanted detail, with its roads, buildings and collision), and for how long, before
    /// the actors come. A world merely drawn (some mesh on every tile) for <see cref="CompleteGrace"/>
    /// s without completing is filmed as it is, with a warning.
    /// </summary>
    private const int SettleRings = 3;
    private const double SettleHold = 3.0, CompleteGrace = 45;

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

    /// <summary>
    /// Which film: <c>--trailer-film story</c> (the default, "Meet You at the Top",
    /// <see cref="StoryScript"/>), <c>showcase</c> (the first cut, <see cref="TrailerScript"/>) or
    /// <c>drognens</c> (the music clip at the barracks, <see cref="DrognensScript"/>, #717).
    /// </summary>
    public static IReadOnlyList<Shot> Film => CmdArgs.Value("--trailer-film") switch
    {
        "showcase" => TrailerScript.Shots,
        "drognens" => DrognensScript.Shots,
        "bubbles" => BubbleDemoScript.Shots,
        "ufo" => UfoDemoScript.Shots,
        "doors" => UfoDemoScript.Doors,
        _ => StoryScript.Shots,
    };

    /// <summary>"all", "5", "5-9" or "5,7,12" (shot numbers).</summary>
    private static List<Shot> Select(string arg)
    {
        var all = Film;
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
        Player.TractorBeams.Scripted += AddCraftBeams;
        // the cut as a whole, whichever shots this run films: no gap, no overlap, the song's length
        var problems = (Film.Count > 0 ? Film[0].Song : Song.VoxelRevolution).CutProblems(Film.Select(s => (s.Number, s.FromBar, s.Bars)).ToList())
            .Concat(Film.Where(s => s.Keys.Count == 0).Select(s => $"shot {s.Number} has no camera key"));
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
        Player.TractorBeams.Scripted -= AddCraftBeams;
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
        if (_moving >= 0) _moving += delta;
        foreach (var a in _actors) a.Step(delta, _shot?.Number ?? 0);
        if (_phase == Phase.Place) Place();
    }

    public override void _Process(double delta)
    {
        HideHud();
        _camera.Current = true;
        if (_moving >= 0)
            foreach (var c in _craft) c.Show(_moving, (float)delta);
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

    /// <summary>Real seconds in this phase: tiles stream on threads, which no fixed frame rate speeds up.</summary>
    private double Wall() => RealClock.Now - _phaseStart;

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseStart = RealClock.Now;
    }

    // --- one shot ----------------------------------------------------------------------------

    private void Next()
    {
        if (!_terrainChecked && !CheckTerrain()) return;
        foreach (var a in _actors) a.Free();
        _actors.Clear();
        foreach (var p in _props) p.QueueFree();
        _props.Clear();
        _craft.Clear();
        _moving = -1;
        ShowScreen(-1);
        Unstage();
        _index++;
        if (_index >= _shots.Count)
        {
            // the calendar's occasions back, as the game runs
            if (_occasion != null) RunCommand?.Invoke("/occasion auto");
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
        SetOccasions(shot.Occasion);
        GameSettings.Current.TrafficCars = shot.Traffic ?? _defaultTraffic;
        World.Traffic.Current?.Forget();
        HoldClock(shot.Hour);

        // the origin to the place first, so it is near the camera rather than 100 km off
        var focus = Focus(shot);
        OriginShifter.Instance?.ShiftTo(focus.E, focus.N);
        _camera.GlobalPosition = _origin.ToWorld(focus.E, focus.N, 0) with { Y = 3000f };
        _set = null;
        if (shot.Set != null)
        {
            _set = shot.Set();
            AddChild(_set);
            _set.GlobalPosition = _origin.ToWorld(shot.SetAt.E, shot.SetAt.N, SetAltitude);
            _props.Add(_set);
        }
        if (shot.Moon > 0f)
        {
            var moon = new DirectionalLight3D
            {
                Name = "Moon",
                LightColor = new Color(0.62f, 0.72f, 1f),
                LightEnergy = shot.Moon,
                ShadowEnabled = false,
                // high in the south-east, pointing down and north-west
                Rotation = new Vector3(Mathf.DegToRad(-55f), Mathf.DegToRad(150f), 0f),
            };
            AddChild(moon);
            _props.Add(moon);
        }
        _shotCamera = CameraFor(shot);
        _settledFor = 0;
        _drawnFor = 0;
        _waitWall = 0;
        Enter(Phase.Stage);
    }

    /// <summary>A camera path through <paramref name="shot"/>'s keys, its points placed in this shot's world.</summary>
    private ShotCamera CameraFor(Shot shot) => new(shot)
    {
        Place = PlaceSpot,
        Actor = i => i >= 0 && i < _actors.Count ? _actors[i].Frame : null,
        // in a set, far under the ground, the camera's floor is the set's own
        Surface = p => shot.Set == null && _chunks.TryGetSurface(p, out float g) ? g : null,
        SetPoint = SetPoint,
        Road = (key, at) => _routes.Point(key, at, _chunks),
        Seat = i => i >= 0 && i < _actors.Count ? _actors[i].Seat : null,
    };

    /// <summary>A point of the shot's set in this frame's world space (null: no set).</summary>
    private Vector3? SetPoint(Vector3 local) => _set != null && IsInstanceValid(_set) ? _set.GlobalTransform * local : null;

    /// <summary>Where a shot happens: the place its set stands for, its first world key's spot, else its first actor's.</summary>
    private static Spot Focus(Shot shot)
    {
        if (shot.Set != null) return shot.SetAt;
        foreach (var k in shot.Keys) if (k.Eye.World is { } s) return s;
        if (shot.Cast.Count > 0) return shot.Cast[0].At;
        foreach (var k in shot.Keys) if (k.Look.World is { } s) return s;
        return new Spot(2600000, 1200000, 500);
    }

    // ---- UFO sightings and herds (#863) ------------------------------------------------------

    private readonly List<int> _stagedUfos = new(), _stagedHerds = new();

    /// <summary>A shot's herd, grazing round its spot until the next shot.</summary>
    private void StageHerd(Cows c)
    {
        if (Farming.Herds.Instance is not { } herds) { GD.PrintErr("[trailer] no herds in this world: the cows are left out"); return; }
        _stagedHerds.Add(herds.Stage(c.At.E, c.At.N, c.Count, c.Breed, c.Radius));
    }

    /// <summary>A shot's sighting, its clock at <see cref="Saucers.From"/> as the camera rolls.</summary>
    private void StageUfo(Saucers u)
    {
        if (Occasions.UfoSightings.Instance is not { } sightings) { GD.PrintErr("[trailer] no UFO sightings in this world: left out"); return; }
        float ground = _chunks.TryGetSurface(_origin.ToWorld(u.At.E, u.At.N, 0), out float g) ? g : 0f;
        float anchor = u.At.Agl ? ground + u.At.H : u.At.H;
        _stagedUfos.Add(sightings.Stage(u.Kind, u.At.E, u.At.N, anchor, Mathf.DegToRad(u.Heading), u.Count, -u.From, u.Seed));
    }

    private void Unstage()
    {
        foreach (int id in _stagedUfos) Occasions.UfoSightings.Instance?.Unstage(id);
        foreach (int h in _stagedHerds) Farming.Herds.Instance?.Unstage(h);
        _stagedUfos.Clear();
        _stagedHerds.Clear();
    }

    private Vector3? PlaceSpot(Spot s)
    {
        var at = _origin.ToWorld(s.E, s.N, s.Agl ? 0 : s.H);
        if (!s.Agl) return at;
        if (!_chunks.TryGetSurface(at, out float g)) return null;
        return at with { Y = g + s.H };
    }

    /// <summary>A prop: a solid box on the ground, its face to <see cref="Prop.Bearing"/> (or what <see cref="Prop"/> asks for instead).</summary>
    private void Build(Prop prop)
    {
        int before = _props.Count;
        BuildOne(prop);
        // a beam over it lifts it (#882): the seam of #861 over what was just built
        if (prop.Beamable && _props.Count > before && _props[^1] is { } built and not Vehicles.VehicleBody)
        {
            var lift = new BeamableProp(built);
            AddChild(lift);
            _props.Add(lift);
        }
    }

    private void BuildOne(Prop prop)
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
        else if (prop.InSet is { } local)
        {
            if (SetPoint(local) is not { } inSet) return;
            at = inSet;
        }
        // a spot on the ground: what the props below stand on (a set's point and an actor's are placed already)
        bool onGround = prop.Actor < 0 && prop.Route == null && prop.InSet == null;
        if (prop.Seated is { } sitter)
        {
            // on the ground under its spot, its hip H over it (the figure's origin was left at sea level, #882)
            if (onGround) at.Y = (FloorAt(at) ?? at.Y) + prop.At.H;
            // someone sitting there, the hip at the spot: the passengers' figure, in the character's looks and clothes
            var palette = (Avatar.HumanPalette.Default with { Outfit = Avatar.Outfit.Of(sitter.Wears) }).With(sitter.Looks);
            var figure = new MeshInstance3D
            {
                Name = $"Prop{_props.Count}",
                Mesh = Avatar.SeatedFigure.Build(palette, new Avatar.SeatAnchor(0, new Vector3(0, 0.46f, 0), 0.05f, 0f)),
                MaterialOverride = Avatar.HumanMeshBuilder.FigureMaterial(),
            };
            AddChild(figure);
            figure.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(-bearing)), at);
            _props.Add(figure);
            return;
        }
        if (prop.Placed != Items.PlacedKind.None)
        {
            // a placed object's look only (a burning fire, a workbench, a camo net): never placed, so nothing is saved
            float ground = FloorAt(at, terrain: prop.InSet == null) ?? at.Y;
            var payload = prop.Placed == Items.PlacedKind.Campfire ? Crafting.CampfireClock.Lit(World.WorldClock.EnvNow) : "";
            if (Items.PlacedObjects.VisualOf(new Items.PlacedObject(0, prop.Placed, "", 0, 0, 0, Quaternion.Identity, payload)) is not { } look)
            {
                GD.PrintErr($"[trailer] no look for placed {prop.Placed}");
                return;
            }
            look.Name = $"Prop{_props.Count}";
            AddChild(look);
            look.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(-bearing)), at with { Y = ground + prop.At.H });
            _props.Add(look);
            return;
        }
        if (prop.Piece is { } piece)
        {
            // a hammer's piece: its mesh and its collision, standing on the ground, its face to the bearing
            float pg = FloorAt(at, terrain: prop.InSet == null) ?? at.Y;
            var built = new StaticBody3D { Name = $"Prop{_props.Count}" };
            built.AddChild(new MeshInstance3D { Mesh = UnitSport.Build.StructureMeshes.Mesh(piece, prop.Material), MaterialOverride = Items.ItemDefs.Material });
            foreach (var (shape, shapeAt) in UnitSport.Build.StructureMeshes.Colliders(piece, prop.Material))
                built.AddChild(new CollisionShape3D { Shape = shape, Transform = shapeAt });
            AddChild(built);
            built.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(-bearing)), at with { Y = pg + prop.At.H });
            _props.Add(built);
            return;
        }
        if (prop.Vehicle != RideKind.OnFoot)
        {
            // a real parked vehicle (a beam lifts it), dropped onto the ground at its spot, taken away after the shot
            if (Vehicles.VehicleManager.Instance is not { } vehicles || Rideable.Create(prop.Vehicle) is not { } ride) return;
            var lv95 = _origin.ToGlobal(at with { Y = (FloorAt(at) ?? at.Y) + 0.6f });
            string name = $"film_{_shot?.Number ?? 0}_{_props.Count}";
            var state = new Vehicles.VehicleState(prop.Vehicle, lv95, Mathf.DegToRad(-bearing), Vector3.Zero, ride.MaxHealth,
                EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0, Setup: prop.Setup);
            if (vehicles.Place(state, name) == null || vehicles.GetNodeOrNull<Node3D>(name) is not { } parked)
            {
                GD.PrintErr($"[trailer] the parked {prop.Vehicle} was refused");
                return;
            }
            _props.Add(parked);
            return;
        }
        if (prop.Item != Items.ItemId.None)
        {
            // an item set down: its own mesh, its lowest point on the floor (a path's, a road's: the
            // pot put on the bare ground was under the Gornergrat's path; in a set, the table), no collision
            float floor = FloorAt(at, terrain: prop.InSet == null) ?? at.Y;
            var item = new MeshInstance3D
            {
                Name = $"Prop{_props.Count}",
                Mesh = Items.ItemDefs.HandMesh(prop.Item),
                MaterialOverride = Items.ItemDefs.Material,
            };
            AddChild(item);
            float yaw = Mathf.DegToRad(-bearing);
            float bottom = item.Mesh.GetAabb().Position.Y * prop.Scale;
            item.GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw).Scaled(Vector3.One * prop.Scale),
                at with { Y = floor - bottom + prop.At.H });
            _props.Add(item);
            return;
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

    /// <summary>
    /// The top of what is underfoot at <paramref name="at"/>: the collision there (a path, a road, a
    /// pavement drawn over the ground) or the ground's own surface, whichever is higher; null when
    /// neither is in yet. In a set, far under the ground, only the collision (<paramref name="terrain"/> false).
    /// </summary>
    private float? FloorAt(Vector3 at, bool terrain = true)
    {
        float? floor = terrain && _chunks.TryGetSurface(at, out float g) ? g : null;
        var from = at with { Y = (floor ?? at.Y) + 3f };
        var hit = _camera.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 8f));
        if (hit.Count > 0)
        {
            float y = hit["position"].AsVector3().Y;
            floor = floor is { } f ? Mathf.Max(f, y) : y;
        }
        return floor;
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

        bool placed = eye != null || shot.Keys[0].Eye.World == null;
        bool drawn = placed && _chunks.SettledNear(_camera.GlobalPosition, SettleRings);
        bool settled = drawn && _chunks.CompleteNear(_camera.GlobalPosition, SettleRings);
        _settledFor = settled ? _settledFor + delta : 0;
        _drawnFor = drawn ? _drawnFor + delta : 0;
        if (_settledFor < SettleHold && _drawnFor < CompleteGrace && Wall() < StageTimeout) return;
        if (_settledFor < SettleHold && _drawnFor >= CompleteGrace)
            GD.PrintErr($"[trailer] shot {shot.Number}: the ground was drawn but never complete in {CompleteGrace:F0} s; filming it as it is");
        else if (_settledFor < SettleHold)
        {
            GD.PrintErr($"[trailer] shot {shot.Number}: the ground never settled in {StageTimeout:F0} s; filming it as it is");
            _failures++;
        }
        GD.Print($"[trailer] shot {shot.Number}: ground in after {Wall():F1} s, placing {shot.Cast.Count} actor(s)");
        for (int i = 0; i < shot.Cast.Count; i++)
            _actors.Add(new Actor(shot.Cast[i], i, this, _chunks, _origin, _routes, k => k >= 0 && k < _actors.Count ? _actors[k] : null)
            {
                SetPoint = SetPoint,
                RampTop = RampTop,
            });
        foreach (var craft in shot.Craft) BuildCraft(craft);
        foreach (var prop in shot.Props.Where(p => p.Actor < 0 && p.Route == null)) Build(prop);
        foreach (var herd in shot.Herds) StageHerd(herd);
        if (shot.Zone is { } zone && PlaceSpot(zone.Centre) is { } centre)
        {
            // the Battle Royale's wall standing on its circle, no match needed (its middle at the ground)
            var wall = new BattleRoyale.ZoneWall { Name = "Zone" };
            AddChild(wall);
            wall.Place(centre, zone.Radius);
            _props.Add(wall);
        }
        Enter(Phase.Place);
    }

    private void Place()
    {
        // the craft as they stand at the start (a ramp an actor comes down is open before it is placed)
        foreach (var c in _craft) c.Show(0, 0f);
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
        _moving = 0;
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
        foreach (var ufo in shot.Ufos) StageUfo(ufo);
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
            else
            {
                // where in the song it starts, for tools/trailer.sh to lay the music under a cut of any shots
                File.WriteAllText(Path.ChangeExtension(path, ".start"), shot.Start.ToString("F3", CultureInfo.InvariantCulture));
                // its English lines and the frames they show on, for the cut's subtitle tracks (#862)
                File.WriteAllText(Path.ChangeExtension(path, ".cues.json"), System.Text.Json.JsonSerializer.Serialize(
                    new CueFile(shot.Number, _fps, _framesWanted, ShotCues.Of(shot, _fps)), SubtitleTrack.Json));
            }
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
        _captions.SetChat(shot.Chat);
        _cued.Clear();
        _captions.SetBubbles(shot.Bubbles, i => i >= 0 && i < shot.Cast.Count ? shot.Cast[i].Who?.Colour : null);
        _photo = null;
        _photoAsked = false;
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[trailer] shot {shot.Number}: rolling, {shot.Length:F2} s ({_framesWanted} frames)"));
        Enter(Phase.Roll);
        Frame(0);
        if (_set != null)
        {
            // where the camera starts in the set's own metres: a key outside its walls films the world beyond them
            var inSet = _set.GlobalTransform.AffineInverse() * _camera.GlobalPosition;
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[trailer] shot {shot.Number}: camera at ({inSet.X:F2}, {inSet.Y:F2}, {inSet.Z:F2}) in the set"));
        }
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
        // the photo's picture is rendered a few frames before its flash, from the camera alone
        // (PhotoCapture: no captions, no flash in it)
        if (shot.Photo is { } at && !_photoAsked && _t >= at - 0.15)
        {
            _photoAsked = true;
            _ = TakePhoto();
        }
        _captions.Show(shot, _t, _photo);
        _captions.ShowBubbles(_t, shot.Length, _headOnScreen ??= HeadOnScreen);
        PlayCues(shot);
        int screen = -1;
        for (int i = 0; i < shot.Screens.Count; i++)
            if (_t >= shot.Screens[i].T && _t < shot.Screens[i].T + shot.Screens[i].Seconds) screen = i;
        ShowScreen(screen);
    }

    private System.Func<int, Vector2?>? _headOnScreen;

    /// <summary>
    /// Actor <paramref name="i"/>'s head on the frame, through the film's camera; behind the camera
    /// it is mirrored to under the frame, so the tail points down and out toward the speaker.
    /// </summary>
    private Vector2? HeadOnScreen(int i)
    {
        if (i < 0 || i >= _actors.Count || _actors[i].Body is not { } body || !IsInstanceValid(body)) return null;
        var head = body.HeadTop;
        var at = _camera.UnprojectPosition(head);
        if (!_camera.IsPositionBehind(head)) return at;
        var size = GetViewport().GetVisibleRect().Size;
        return new Vector2(size.X - at.X, size.Y * 2f);
    }

    private async System.Threading.Tasks.Task TakePhoto()
    {
        var eye = _camera;
        if (_shot!.PhotoFrom is { } view)
        {
            // the photographer's view: a camera of its own, the film's left where it is
            eye = new Camera3D { Name = "PhotoEye" };
            AddChild(eye);
            CameraFor(_shot with { Keys = [view with { T = 0 }], KeysFrom = 0, Smooth = 0, Shake = 0 }).Apply(eye, 0, 0);
        }
        // a screenshot of one of the game's own screens instead of a render (the map, the studio)
        var image = _shot.PhotoFile is { } file && File.Exists(file) ? Image.LoadFromFile(file) : await Items.PhotoCapture.Render(this, eye, eye.Fov);
        if (eye != _camera) eye.QueueFree();
        _photo = ImageTexture.CreateFromImage(image);
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

    // --- real terrain only (#882) --------------------------------------------------------------

    private bool _terrainChecked;
    private double _terrainWait;
    private readonly bool _scouting = CmdArgs.Has("--trailer-scout");
    private static readonly bool AllowGenerated = CmdArgs.Has("--trailer-allow-generated");

    /// <summary>
    /// Before the first shot, once the terrain's manifest is in: how many real tiles this run has
    /// (none: the terrain drive is not there, and every shot would be the generated fill: the run
    /// stops), then every shot's ground (<see cref="FilmCoverage"/>): each tile within 5 km of a
    /// camera key's eye and of an actor's spot (8 km for an eye over 2,200 m) must be a real one.
    /// A shot that is not is a failure of the run, its missing tiles listed and written to
    /// <c>test_output/trailer/&lt;film&gt;/missing_tiles.txt</c> for <c>MapSetup --tiles-file</c>.
    /// <c>--trailer-allow-generated</c> lets a deliberate test on generated or fixture ground pass.
    /// False while the manifest is not in yet.
    /// </summary>
    private bool CheckTerrain()
    {
        _terrainWait += GetProcessDeltaTime();
        if (_chunks.Source == null && _terrainWait < 60) return false;
        _terrainChecked = true;
        int real = _chunks.AvailableTileCount;
        GD.Print($"[trailer] real tiles: {real} (chunks {TerrainPaths.FindChunkDir()})");
        if (_scouting) return true;
        if (real == 0 && !AllowGenerated)
        {
            GD.PrintErr("[trailer] no real tiles: the terrain is not there (is its drive mounted? terrain_location.json, CHUNKS); "
                + "every shot would be the generated fill. --trailer-allow-generated to film it anyway");
            Enter(Phase.Finished);
            GD.Print("[trailer] RESULT: FAILED (no real terrain)");
            GetTree().Quit(3);
            return false;
        }
        var peaks = TilePeaks();
        var all = new SortedSet<(int, int)>();
        foreach (var shot in _shots)
        {
            var missing = FilmCoverage.Missing(Needs(shot, peaks), (e, n) => _chunks.AvailableTiles.Contains(new Terrain.Format.TileId(e, n)));
            if (missing.Count == 0) continue;
            foreach (var t in missing) all.Add(t);
            GD.PrintErr($"[trailer] shot {shot.Number}: {missing.Count} tile(s) round its cameras and actors are not real "
                + $"(the generated fill would show): {string.Join(" ", missing.Select(t => $"{t.E}-{t.N}"))}");
            if (!AllowGenerated) _failures++;
        }
        if (all.Count > 0)
        {
            string film = CmdArgs.Value("--trailer-film") ?? "story";
            string dir = ProjectSettings.GlobalizePath($"res://test_output/trailer/{film}");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "missing_tiles.txt");
            File.WriteAllText(file, FilmCoverage.TilesFile(all));
            GD.PrintErr($"[trailer] {all.Count} missing tile(s) in {file} (MapSetup --tiles-file)"
                + (AllowGenerated ? "; --trailer-allow-generated: filmed anyway" : ""));
        }
        else GD.Print("[trailer] every shot's ground is real");
        return true;
    }

    /// <summary>Each tile's highest point, from the manifest (an eye's height over a tile it is put above); empty without one.</summary>
    private static Dictionary<(int, int), float> TilePeaks()
    {
        var peaks = new Dictionary<(int, int), float>();
        try
        {
            string path = Path.Combine(TerrainPaths.FindChunkDir(), "manifest.json");
            if (!File.Exists(path)) return peaks;
            foreach (var t in Terrain.Format.TerrainManifest.FromJson(File.ReadAllText(path)).Tiles) peaks[(t.E, t.N)] = t.Max;
        }
        catch (System.Exception e) { GD.PrintErr($"[trailer] manifest unreadable for the eyes' heights: {e.Message}"); }
        return peaks;
    }

    /// <summary>The points a shot needs real ground round: its world camera eyes and looks, its actors' spots, its craft, saucers and herds.</summary>
    private static IEnumerable<FilmCoverage.Need> Needs(Shot shot, Dictionary<(int, int), float> peaks)
    {
        // a set is far under the ground: its film shows no terrain
        if (shot.Set != null) yield break;
        FilmCoverage.Need At(Spot s, bool eye)
        {
            double alt = !s.Agl ? s.H
                : peaks.TryGetValue(((int)System.Math.Floor(s.E / 1000), (int)System.Math.Floor(s.N / 1000)), out float top) ? top + s.H : double.NaN;
            return new FilmCoverage.Need(s.E, s.N, alt, eye);
        }
        foreach (var k in shot.Keys)
        {
            if (k.Eye.World is { } eye) yield return At(eye, true);
            else if (k.Eye.Actor >= 0 && k.Eye.Actor < shot.Cast.Count) yield return At(shot.Cast[k.Eye.Actor].At, true);
        }
        foreach (var c in shot.Cast)
        {
            yield return At(c.At, false);
            if (c.Board is { } board) yield return At(board, false);
        }
        foreach (var c in shot.Craft) yield return At(c.At, false);
        foreach (var u in shot.Ufos) yield return At(u.At, false);
        foreach (var h in shot.Herds) yield return At(h.At, false);
    }

    // --- the film kit (#882) ------------------------------------------------------------------

    /// <summary>
    /// The occasions a shot asks for (<see cref="Shot.Occasion"/>): the calendar's back first, then
    /// every known one not asked for stopped and those asked for started, so nothing carries over from
    /// the shot before (Christmas's snow did, #881). Null leaves the calendar's.
    /// </summary>
    private void SetOccasions(string? wanted)
    {
        if (wanted == _occasion) return;
        RunCommand?.Invoke("/occasion auto");
        _occasion = wanted;
        if (wanted == null || Occasions.OccasionManager.Instance is not { } occasions) return;
        var ids = wanted == "none" ? new HashSet<string>()
            : wanted.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries).ToHashSet();
        foreach (var e in occasions.Known)
            RunCommand?.Invoke(ids.Contains(e.Id) ? $"/occasion start {e.Id}" : $"/occasion stop {e.Id}");
    }

    /// <summary>A UFO prop at its spot (<see cref="Shot.Craft"/>), on the ground under it or above.</summary>
    private void BuildCraft(Craft craft)
    {
        var prop = new CraftProp(craft) { Name = $"Craft{_craft.Count}" };
        AddChild(prop);
        var at = _origin.ToWorld(craft.At.E, craft.At.N, 0);
        float ground = _chunks.TryGetSurface(at, out float g) ? g : 0f;
        prop.Put(at, ground);
        _craft.Add(prop);
        _props.Add(prop);
    }

    /// <summary>The craft's beams that are on, as real ones (<see cref="Player.TractorBeams.Scripted"/>): what is under them rises.</summary>
    private void AddCraftBeams(List<Player.TractorBeams.Beam> beams)
    {
        foreach (var c in _craft)
            if (IsInstanceValid(c) && c.BeamApex is { } apex) beams.Add(new Player.TractorBeams.Beam(null, apex, Vector3.Zero));
    }

    /// <summary>The boards under the ramps walked down this shot, by ramp.</summary>
    private readonly Dictionary<RampOf, StaticBody3D> _ramps = new();

    /// <summary>
    /// The top of a landed saucer's open ramp (<see cref="Cast.DownRamp"/>), its board made solid
    /// the first time: feet, compass bearing down it, the saucer's body when it is an actor's. Null
    /// until the ramp is down.
    /// </summary>
    private (Vector3 Feet, float Bearing, PhysicsBody3D? Host)? RampTop(RampOf ramp)
    {
        Node3D frame;
        PhysicsBody3D? host = null;
        if (ramp.OfCraft)
        {
            if (ramp.Index < 0 || ramp.Index >= _craft.Count || !_craft[ramp.Index].RampDown) return null;
            frame = _craft[ramp.Index];
        }
        else
        {
            if (ramp.Index < 0 || ramp.Index >= _actors.Count || _actors[ramp.Index] is not { Ready: true, Body: { } body }
                || body.Saucer is not { } ufo || ufo.State.Ramp < 0.95f) return null;
            frame = body.Visual as Node3D ?? body;
            host = body;
        }
        if (!_ramps.TryGetValue(ramp, out var board) || !IsInstanceValid(board))
        {
            board = SaucerRamp.Board();
            frame.AddChild(board);
            _ramps[ramp] = board;
            if (host != null) board.AddCollisionExceptionWith(host);
        }
        var xf = frame.GlobalTransform;
        var down = xf.Basis * SaucerRamp.Down;
        return (xf * SaucerRamp.At(0.5f), Mathf.RadToDeg(Mathf.Atan2(down.X, -down.Z)), host);
    }

    /// <summary>The shot's item cues due by now (<see cref="Shot.ItemCues"/>), each once, from its actor's hand.</summary>
    private void PlayCues(Shot shot)
    {
        for (int i = 0; i < shot.ItemCues.Count; i++)
        {
            var cue = shot.ItemCues[i];
            if (_t < cue.T || !_cued.Add(i)) continue;
            if (cue.Actor < 0 || cue.Actor >= _actors.Count || _actors[cue.Actor].Body is not { } who || !IsInstanceValid(who)
                || Items.ItemEvents.Instance is not { } events)
            {
                GD.PrintErr($"[trailer] shot {shot.Number}: cue {i} ({cue.Kind}) has no actor {cue.Actor}");
                continue;
            }
            var ahead = (-who.GlobalBasis.Z with { Y = 0 }).Normalized();
            var at = cue.At is { } spot ? PlaceSpot(spot) ?? _origin.ToWorld(spot.E, spot.N, who.GlobalPosition.Y)
                : Items.ItemEvents.MuzzleOf(who, ahead) + Vector3.Up * cue.Up;
            // a fishing line keeps no direction (a remote angler's float stays where it landed)
            var dir = cue.Kind is Items.ItemEventKind.FishCast or Items.ItemEventKind.FishEnd ? Vector3.Zero : ahead;
            events.PlayAs(who, cue.Kind, at, dir, cue.Extra);
        }
    }

    /// <summary>Shows the shot's game screen <paramref name="index"/> over the frame (-1: none), under the captions.</summary>
    private void ShowScreen(int index)
    {
        if (index == _screenShown) return;
        _screenShown = index;
        if (_screenLayer != null && IsInstanceValid(_screenLayer)) _screenLayer.QueueFree();
        _screenLayer = null;
        if (index < 0 || _shot == null || index >= _shot.Screens.Count) return;
        var cue = _shot.Screens[index];
        Control? page = cue.Kind switch
        {
            GameScreen.Map => Ui.MapScreen.Create(),
            GameScreen.Landing => Ui.MapScreen.CreateLanding(cue.At is { } a ? (a.E, a.N) : (Core.SpawnPoint.DefaultLv95E, Core.SpawnPoint.DefaultLv95N), _ => { }),
            GameScreen.Png when cue.Png != null && File.Exists(cue.Png) => new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(Image.LoadFromFile(cue.Png)),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            },
            _ => null,
        };
        if (page == null)
        {
            GD.PrintErr($"[trailer] shot {_shot.Number}: screen {index} ({cue.Kind}) could not be shown");
            return;
        }
        // its own layer, under the captions (120) and the bubbles; a dark backdrop, as behind the menus
        _screenLayer = new CanvasLayer { Name = "FilmScreen", Layer = 90 };
        var back = new ColorRect { Color = new Color(0.05f, 0.06f, 0.08f) };
        back.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _screenLayer.AddChild(back);
        page.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _screenLayer.AddChild(page);
        AddChild(_screenLayer);
        if (page is Ui.Screen screen) screen.OnShown();
    }

    // --- the game's own layers off the picture ------------------------------------------------

    /// <summary>Every CanvasLayer but the captions hidden: walked once, then followed through NodeAdded.</summary>
    private void HideHud()
    {
        if (_hud == null)
        {
            _hud = new List<CanvasLayer>();
            foreach (var node in GetTree().Root.FindChildren("*", "CanvasLayer", true, false))
                if (node is CanvasLayer layer && layer != _captions && layer != _screenLayer) _hud.Add(layer);
            GetTree().NodeAdded += OnNodeAdded;
        }
        _hud.RemoveAll(l => !IsInstanceValid(l));
        foreach (var layer in _hud) layer.Visible = false;
    }

    private void OnNodeAdded(Node node)
    {
        if (node is CanvasLayer layer && layer != _captions && layer != _screenLayer && !IsAncestorOf(layer)) _hud!.Add(layer);
    }
}
