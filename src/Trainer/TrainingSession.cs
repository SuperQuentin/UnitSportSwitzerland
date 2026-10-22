using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Gpx;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Trainer;

/// <summary>
/// One home-trainer training (#901, <c>docs/plans/home-trainer-rides.md</c>), started from the phone's
/// Training app: a GPX course snapped onto the roads, ridden on the road bike with the trainer's watts.
///
/// <para>
/// The rider does not steer: the route's lean pilot (<see cref="AutoPilot.For"/>, the one races and
/// NPCs ride on) holds the bike on the course and only brakes for a bend it could not take. The
/// pedals are the rider's: with power arriving, <see cref="Bicycle.RiderWatts"/> is the trainer's
/// reading and the throttle is held; without, the keys pedal as on any ride. The road's grade
/// (the way's profile, not the terrain's facets) goes to the trainer as SIM through <see cref="TrainerLink"/>,
/// or the trainer holds a power (ERG, off by default).
/// </para>
///
/// <para>
/// What the pilot rides is the <b>way</b>: the course, a few kilometres at a time, until the rider takes
/// another road at a junction (<see cref="Choose"/>, the steering), turns round (<see cref="UTurn"/>) or
/// heads back to it (<see cref="Rejoin"/>). Off the course the way follows the roads, straight on at every
/// junction, and becomes the course again wherever it meets it going the course's way
/// (<see cref="JunctionChoice"/>, on the road network's walking graph).
/// </para>
/// </summary>
public sealed partial class TrainingSession : Node, IOriginShiftAware
{
    public const string NodeName = "Training";
    public static TrainingSession? Instance { get; private set; }

    public enum Phase { Idle, Loading, Ready, Travelling, Mounting, Riding, Finished }

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly Func<FootPlayer?> _player;

    public Phase State { get; private set; }
    /// <summary>What the Training app shows under the course: loading, snapped, why it failed.</summary>
    public string Status { get; private set; } = "No course yet: import a GPX.";
    public string? CourseName { get; private set; }
    public string? CoursePath { get; private set; }
    public double CourseKm => _course?.Profile.Length / 1000.0 ?? 0;
    public double CourseAscent => _course?.Profile.Ascent ?? 0;

    /// <summary>Metres along the course; the rider's progress.</summary>
    public double Along { get; private set; }
    public double RideSeconds { get; private set; }
    public double GradeHere { get; private set; }
    /// <summary>How far the bike is off the way's line, flat metres: the pilot's hold on it.</summary>
    public float OffCourse { get; private set; }

    /// <summary>Riding the course itself, not a road off it.</summary>
    public bool OnCourse => _way.Count > 0 && _way[Math.Min(_near, _way.Count - 1)].Course >= 0;
    /// <summary>On a way the rider asked for back to the course.</summary>
    public bool Rejoining { get; private set; }
    /// <summary>The next junction on the way, within <see cref="LookAhead"/> m; null with none.</summary>
    public Junction? NextJunction { get; private set; }
    /// <summary>Metres from the rider to <see cref="NextJunction"/>.</summary>
    public double JunctionIn => NextJunction is { } j && _wayArc.Count > 0 ? j.Arc - _wayArc[_near] : 0;
    /// <summary>A turn asked for the next junction: -1 left, +1 right, 0 none.</summary>
    public int TurnAsked { get; private set; }
    /// <summary>The way's flat length ahead of the rider: where the pilot stops if nothing is added.</summary>
    public double WayLeft => _wayArc.Count > 0 ? _wayArc[^1] - _wayArc[_near] : 0;

    /// <summary>How far ahead junctions are shown and a turn asked for is taken.</summary>
    public const double LookAhead = 250;
    /// <summary>The way holds this much course or road ahead; more is added when less than <see cref="ExtendBelow"/> is left.</summary>
    private const double Window = 3000, ExtendBelow = 800;
    /// <summary>A road off the course becomes the course again only past this many metres from where it left it (a kerb's curve hugs the road it leaves).</summary>
    private const double SpliceAfter = 20;
    /// <summary>A road within this many metres of a course point, going its way, is the course.</summary>
    private const double CourseReach = 3;
    /// <summary>The width of road the pilot's line may use, metres.</summary>
    private const float RideWidth = 2.5f;
    /// <summary>Metres of way laid behind its start (<see cref="BuildRoute"/>).</summary>
    private const double RunUp = 8;

    private Course? _course;
    private RoadNetwork? _net;
    private List<WayPoint> _way = new();
    private List<double> _wayArc = new();
    private CourseProfile? _wayProfile;
    private bool _wayEnded;
    private double _courseAt;
    private double _junctionAt;
    private int _stick;
    private RaceRoute? _route;
    private AutoPilot? _pilot;
    private int _near;
    private double _joules;
    private RideProfile _profileWas;
    private float _settle;
    private CanvasLayer? _hud;
    private Label? _hudText;
    private string _hudShown = "";
    private double _hudAt;

    /// <summary>A course in LV95 with road heights, each point's flat distance along it, and the profile.</summary>
    private sealed record Course(List<(double E, double N, double H)> Points, double[] Arc, CourseProfile Profile);

    public event Action<string>? Said;

    public TrainingSession(ChunkManager chunks, WorldOrigin origin, Func<FootPlayer?> player)
    {
        _chunks = chunks;
        _origin = origin;
        _player = player;
        Name = NodeName;
    }

    public override void _EnterTree()
    {
        Instance = this;
        Teleporter.Arrived += OnArrived;
    }

    public override void _ExitTree()
    {
        Teleporter.Arrived -= OnArrived;
        if (Instance == this) Instance = null;
    }

    // ---------------------------------------------------------------- the course

    /// <summary>The system's file picker for a GPX (the OS dialog, so it opens over the phone too).</summary>
    public void Browse()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Title = "A GPX course to ride",
        };
        dialog.AddFilter("*.gpx", "GPX tracks");
        dialog.FileSelected += path => { Load(path); dialog.QueueFree(); };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    /// <summary>Reads and snaps a GPX off the main thread; <see cref="State"/> is Ready when it is done.</summary>
    public async void Load(string path)
    {
        if (State is Phase.Travelling or Phase.Mounting or Phase.Riding) Stop("a new course");
        State = Phase.Loading;
        CoursePath = path;
        CourseName = System.IO.Path.GetFileNameWithoutExtension(path);
        Status = "Reading the course...";
        try
        {
            var track = await Task.Run(() => GpxParser.Parse(path));
            if (track.Points.Count < 2) throw new InvalidOperationException("the file holds no track");
            CourseName = string.IsNullOrWhiteSpace(track.Name) ? CourseName : track.Name;
            Status = "Snapping it to the roads...";
            // one network for the matcher and the junctions, a kilometre round the course for the roads off it
            var source = _chunks.Source;
            MatchResult? match = null;
            _net = null;
            if (source != null)
            {
                double minE = track.Points.Min(p => p.E), maxE = track.Points.Max(p => p.E);
                double minN = track.Points.Min(p => p.N), maxN = track.Points.Max(p => p.N);
                var net = await RoadNetwork.LoadAsync(source, minE, minN, maxE, maxN, pad: 1000);
                if (net.Edges.Count > 0)
                {
                    match = track.Points.Count < 4 ? new MatchResult(null, "track too short to match", 0)
                        : await Task.Run(() => TrackMatcher.Match(track, net));
                    await Task.Run(net.BuildWalkGraph);
                    _net = net;
                }
                else match = new MatchResult(null, "no road data covers this track", 0);
            }
            var used = match is { Ok: true } m ? m.Track! : track;
            _course = Build(used);
            State = Phase.Ready;
            Status = match is { Ok: true } snapped
                ? string.Create(CultureInfo.InvariantCulture, $"On the roads ({snapped.MatchedShare * 100:0}% of it matched).")
                : $"Not snapped ({match?.Status ?? "no map here"}): ridden as recorded.";
            TrainerLink.Instance?.AddRecent(path);
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[training] {CourseName}: {CourseKm:0.0} km, {CourseAscent:0} m up, {_course.Points.Count} points. {Status}"));
        }
        catch (Exception e)
        {
            _course = null;
            State = Phase.Idle;
            Status = $"Could not read it: {e.Message}";
            GD.PushWarning($"[training] {path}: {e.Message}");
        }
    }

    private static Course Build(GpxTrack track)
    {
        var points = new List<(double E, double N, double H)>();
        var arc = new List<double>();
        var profile = new List<(double, double)>();
        double s = 0;
        foreach (var p in track.Points)
        {
            if (points.Count > 0)
            {
                var (e0, n0, _) = points[^1];
                double step = Math.Sqrt((p.E - e0) * (p.E - e0) + (p.N - n0) * (p.N - n0));
                if (step < 0.5) continue;   // a standstill's repeated fixes are no road
                s += step;
            }
            points.Add((p.E, p.N, p.Elevation));
            arc.Add(s);
            profile.Add((s, p.Elevation));
        }
        return new Course(points, arc.ToArray(), new CourseProfile(profile));
    }

    // ---------------------------------------------------------------- start and stop

    public bool CanStart => _course != null && State is Phase.Ready or Phase.Finished && _player() != null;

    /// <summary>Travels to the course's start (behind the phone's screen when it is out), then mounts and rides.</summary>
    public void Start()
    {
        if (!CanStart || _course is not { } c) return;
        var (e, n, _) = c.Points[0];
        State = Phase.Travelling;
        Along = 0;
        RideSeconds = 0;
        _joules = 0;
        _near = 0;
        _courseAt = 0;
        _way.Clear();
        _wayArc.Clear();
        NextJunction = null;
        TurnAsked = 0;
        Rejoining = false;
        Status = "Going to the start...";
        TrainerLink.Instance?.Reconnect();
        TrainerLink.Instance?.SetErg(false);   // every ride starts in SIM
        if (Teleporter.Instance?.TeleportTo(e, n, $"the start of {CourseName}") != true)
            OnArrived();   // nothing to travel with (a check's flat world): start where we are
    }

    private void OnArrived()
    {
        if (State != Phase.Travelling) return;
        State = Phase.Mounting;
        _settle = 0;
    }

    /// <summary>Ends the training where it is: the bike is the rider's again.</summary>
    public void Stop(string why = "stopped")
    {
        if (State is not (Phase.Travelling or Phase.Mounting or Phase.Riding)) return;
        if (_player() is { } me) me.RideControls = null;
        GameSettings.Current.RideProfile = _profileWas;
        TrainerLink.Instance?.Flat();
        _pilot = null;
        _route = null;
        State = _course != null ? Phase.Ready : Phase.Idle;
        Status = $"Training {why} at {Along / 1000:0.0} km.";
        ShowHud(false);
        GD.Print($"[training] {why} at {Along:0} m after {RideSeconds:0} s");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_player() is not { } me) return;
        float dt = (float)delta;
        switch (State)
        {
            case Phase.Mounting: Mount(me, dt); break;
            case Phase.Riding: Ride(me, dt); break;
        }
    }

    /// <summary>On the start, facing the course, on the bike, and the pilot armed.</summary>
    private void Mount(FootPlayer me, float dt)
    {
        if (_course is not { } c) return;
        var world = c.Points.Take(64).Select(p => _origin.ToWorld(p.E, p.N, p.H)).ToList();
        var start = world[0];
        int ahead = Math.Min(world.Count - 1, AheadIndex(world, 0, 8f));
        var dir = RaceRoute.Flat(world[ahead] - start);
        float yaw = dir.LengthSquared() > 0.01f ? Mathf.Atan2(-dir.X, -dir.Z) : me.Rotation.Y;

        if (_route == null)
        {
            // the body to the start, on its feet, then onto the bike once it stands
            me.RideControls = null;
            float ground = _chunks.TryGetHeight(start, out float h) ? h : start.Y;
            me.DebugLaunch(new Vector3(start.X, ground + 1.2f, start.Z), Vector3.Zero);
            me.Rotation = new Vector3(0, yaw, 0);
            _way = CourseFrom(0);
            BuildRoute();
            return;
        }
        if (me.Ride != RideKind.RoadBike)
        {
            if (!me.IsOnFloor()) return;
            me.Rotation = new Vector3(0, yaw, 0);
            if (me.Ride != RideKind.OnFoot && me.Vehicle is { IsVehicle: true }) me.SetRide(RideKind.OnFoot);
            me.SetRide(RideKind.RoadBike);
            return;
        }
        if ((_settle += dt) < 0.3f) return;
        if (!Repilot(me)) return;
        _profileWas = GameSettings.Current.RideProfile;
        // watts only mean something in the real-world model (game-sim-profile)
        GameSettings.Current.RideProfile = RideProfile.Sim;
        State = Phase.Riding;
        Status = "Riding.";
        ShowHud(true);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[training] riding {CourseName}, {CourseKm:0.0} km"));
    }

    /// <summary>The pilot steers and brakes for bends; the pedals are the trainer's, or the keys'.</summary>
    private RideInput Controls(FootPlayer me, AutoPilot pilot)
    {
        var steer = pilot.Drive((float)GetPhysicsProcessDeltaTime(), State == Phase.Riding, World.RaceManager.Others(me));
        if (State != Phase.Riding) return steer;   // finished: the pilot pulls over and stops
        var link = TrainerLink.Instance;
        bool power = link?.PowerLive == true;
        float throttle = power ? 1f
            : Mathf.Max(PlayerInput.Strength(PlayerInput.Throttle), Mathf.Max(0f, -PlayerInput.Move.Y));
        float brake = Mathf.Max(steer.Brake, PlayerInput.Strength(PlayerInput.Brake));
        // a brake the pilot asks for is a bend: no pedalling through it
        if (steer.Brake > 0.05f) throttle = 0f;
        return steer with { Throttle = throttle, Brake = brake, Effort = !power && PlayerInput.Held(PlayerInput.TuckBoost) };
    }

    private void Ride(FootPlayer me, float dt)
    {
        if (_course is not { } c || _route == null) return;
        if (me.Ride != RideKind.RoadBike) { Stop("left the bike"); return; }
        var link = TrainerLink.Instance;
        if (link?.PowerLive == true)
        {
            if (me.Vehicle is Bicycle bike) bike.RiderWatts = link.Watts;
            _joules += link.Watts * dt;
        }
        RideSeconds += dt;

        _near = Nearest(_route.Centre, me.GlobalPosition, _near);
        var here = _way[_near];
        if (here.Course >= 0)
        {
            _courseAt = c.Arc[here.Course];
            Rejoining = false;
        }
        Along = _courseAt;
        OffCourse = Off(_route.Centre, _near, me.GlobalPosition);
        GradeHere = _wayProfile?.GradeAt(_wayArc[_near]) ?? 0;
        link?.FeedGrade(GradeHere);

        ReadKeys();
        LookForJunction(me);
        Extend(me);

        if (here.Course >= 0 && Along >= c.Profile.Length - 3)
        {
            State = Phase.Finished;
            link?.Flat();
            GameSettings.Current.RideProfile = _profileWas;
            string avg = RideSeconds > 0 ? $", {_joules / RideSeconds:0} W average" : "";
            Status = string.Create(CultureInfo.InvariantCulture, $"Finished {CourseName}: {CourseKm:0.0} km in {Clock(RideSeconds)}{avg}.");
            Said?.Invoke(Status);
            GD.Print($"[training] RESULT finished {Along:0} m in {RideSeconds:0} s{avg}");
            ShowHud(false);
            // the pilot rides on, past the line, and stops; then the bike is the rider's
            GetTree().CreateTimer(6.0).Timeout += () => { if (State == Phase.Finished && _player() is { } p) p.RideControls = null; };
            return;
        }
        UpdateHud(me);
    }

    public void OnOriginShifted(OriginShift shift) => _route?.Follow(_origin.Frame);

    // ---------------------------------------------------------------- the way: the course, or roads off it

    /// <summary>The course from point <paramref name="index"/> on, <see cref="Window"/> m of it.</summary>
    private List<WayPoint> CourseFrom(int index)
    {
        var way = new List<WayPoint>();
        if (_course is not { } c) return way;
        double until = c.Arc[Math.Min(index, c.Arc.Length - 1)] + Window;
        for (int i = index; i < c.Points.Count && c.Arc[i] <= until; i++)
            way.Add(new WayPoint(c.Points[i].E, c.Points[i].N, c.Points[i].H, -1, i));
        return way;
    }

    /// <summary>The pilot's route from <see cref="_way"/>, its lengths and profile; the rider is at its start.</summary>
    private void BuildRoute()
    {
        // a run-up behind the first point: a race's speed profile starts from a standing start there, and a
        // rider held to 0 m/s on it crept for 18 s before reaching the second point
        if (_way.Count >= 2)
        {
            var (a, b) = (_way[0], _way[1]);
            double len = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            if (len > 0.1)
                _way.Insert(0, a with { E = a.E - (b.E - a.E) / len * RunUp, N = a.N - (b.N - a.N) / len * RunUp, Node = -1 });
        }
        _wayArc = JunctionChoice.Arcs(_way);
        _wayProfile = new CourseProfile(_way.Select((p, i) => (_wayArc[i], p.H)).ToList());
        var world = _way.Select(p => _origin.ToWorld(p.E, p.N, p.H)).ToList();
        // narrow: the pilot's line keeps within a metre of the road's centre instead of racing across it,
        // which cut a junction's kerb into the corner's posts
        _route = RaceRoute.FromPoints(world, world.Select(_ => RideWidth).ToList(), Terrain.Format.RoadClass.Road, _origin.Frame);
        _near = 0;
        _wayEnded = false;
        NextJunction = null;
        _junctionAt = 0;
    }

    /// <summary>A new pilot on the route, steering the rider from where it is.</summary>
    private bool Repilot(FootPlayer me)
    {
        if (_route == null) return false;
        _pilot = AutoPilot.For(_route, me);
        if (_pilot == null) return false;
        var pilot = _pilot;
        me.RideControls = () => Controls(me, pilot);
        return true;
    }

    /// <summary>Rides <paramref name="way"/> from now on (it starts a few points behind the rider).</summary>
    private void SetWay(FootPlayer me, List<WayPoint> way)
    {
        if (way.Count < 2) return;
        _way = way;
        BuildRoute();
        _near = Nearest(_route!.Centre, me.GlobalPosition, 0);
        Repilot(me);
    }

    /// <summary>The way from a few points behind the rider up to (not including) index <paramref name="end"/>.</summary>
    private List<WayPoint> Kept(int end)
    {
        int from = Math.Max(0, _near - 3);
        return _way.GetRange(from, Math.Max(0, Math.Min(end, _way.Count) - from));
    }

    /// <summary>
    /// Appends road graph nodes to <paramref name="way"/> until one is on the course going its way, past
    /// <paramref name="skip"/> m; from there the course goes on. True when it met the course.
    /// </summary>
    private bool AppendRoads(List<WayPoint> way, IReadOnlyList<int> nodes, double skip)
    {
        if (_net is not { } g) return false;
        double run = 0;
        for (int k = 0; k < nodes.Count; k++)
        {
            var p = JunctionChoice.Point(g, nodes[k]);
            if (way.Count > 0) run += Math.Sqrt((p.E - way[^1].E) * (p.E - way[^1].E) + (p.N - way[^1].N) * (p.N - way[^1].N));
            var before = way.Count > 0 ? way[^1] : p;
            way.Add(p);
            if (run < skip) continue;
            if (CourseAt(p.E, p.N, p.E - before.E, p.N - before.N) is int c)
            {
                way.AddRange(CourseFrom(c + 1));
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The course point near (<paramref name="e"/>, <paramref name="n"/>) that a rider going
    /// (<paramref name="de"/>, <paramref name="dn"/>) would be riding the course's way at, from a little
    /// behind the last course metre ridden to 3 km past it; or null.
    /// </summary>
    private int? CourseAt(double e, double n, double de, double dn)
    {
        if (_course is not { } c || de * de + dn * dn < 1e-6) return null;
        int i = IndexAt(c.Arc, _courseAt - 30);
        for (; i < c.Points.Count && c.Arc[i] <= _courseAt + Window; i++)
        {
            var (pe, pn, _) = c.Points[i];
            if ((pe - e) * (pe - e) + (pn - n) * (pn - n) > CourseReach * CourseReach) continue;
            if (Math.Abs(JunctionChoice.Bearing(CourseDir(i), (de, dn))) < 45) return i;
        }
        return null;
    }

    /// <summary>The first index whose distance is at least <paramref name="s"/>.</summary>
    private static int IndexAt(double[] arc, double s)
    {
        int i = Array.BinarySearch(arc, s);
        return Math.Max(0, i >= 0 ? i : ~i);
    }

    /// <summary>The course's direction at point <paramref name="i"/>.</summary>
    private (double E, double N) CourseDir(int i)
    {
        var p = _course!.Points;
        int a = Math.Max(0, Math.Min(i, p.Count - 2)), b = a + 1;
        return (p[b].E - p[a].E, p[b].N - p[a].N);
    }

    /// <summary>The training's keys: the steering picks the next junction's road; the rest have a method each.</summary>
    private void ReadKeys()
    {
        if (UiFocus.TextEntryActive) return;
        float x = PlayerInput.Move.X;
        int stick = x < -0.6f ? -1 : x > 0.6f ? 1 : 0;
        if (stick != 0 && stick != _stick) Choose(stick);
        _stick = stick;
        var link = TrainerLink.Instance;
        if (Input.IsActionJustPressed(PlayerInput.RideRejoin)) Rejoin();
        if (Input.IsActionJustPressed(PlayerInput.RideUTurn)) UTurn();
        if (Input.IsActionJustPressed(PlayerInput.RideErg)) link?.SetErg(!link.Erg);
        if (Input.IsActionJustPressed(PlayerInput.RidePowerUp)) link?.StepErg(1);
        if (Input.IsActionJustPressed(PlayerInput.RidePowerDown)) link?.StepErg(-1);
    }

    /// <summary>
    /// Takes the road one step to <paramref name="side"/> (-1 left, +1 right) of the one the way takes at the
    /// next junction; asked before one is in sight, it waits for it. The intent a key, the stick, the phone
    /// and a check all call.
    /// </summary>
    public void Choose(int side)
    {
        if (State != Phase.Riding || side == 0) return;
        if (_net == null) { Say("No road map here: the course is ridden as it is."); return; }
        TurnAsked = Math.Sign(side);
        _junctionAt = 0;   // look now
    }

    private void LookForJunction(FootPlayer me)
    {
        if (_net is not { } g || _wayArc.Count == 0) return;
        if (RideSeconds >= _junctionAt)
        {
            _junctionAt = RideSeconds + 0.2;
            NextJunction = JunctionChoice.Next(g, _way, _wayArc, _near, _wayArc[_near], LookAhead);
        }
        if (TurnAsked == 0 || NextJunction is not { } j) return;
        int side = TurnAsked;
        TurnAsked = 0;
        int pick = JunctionChoice.Pick(j, side);
        if (pick < 0) { Say($"No road to the {(side < 0 ? "left" : "right")} at the next junction."); return; }
        Take(me, j, j.Branches[pick]);
    }

    /// <summary>The way up to junction <paramref name="j"/>, then <paramref name="branch"/>, straight on, until it meets the course.</summary>
    private void Take(FootPlayer me, Junction j, Branch branch)
    {
        int cut = _near;
        while (cut + 1 < _way.Count && _wayArc[cut + 1] < j.Arc - 0.5) cut++;
        var way = Kept(cut + 1);
        way.Add(JunctionChoice.Point(_net!, j.Node));
        bool course = AppendRoads(way, JunctionChoice.Follow(_net!, j.Node, branch.First, Window), SpliceAfter);
        SetWay(me, way);
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[training] junction {j.Node} in {j.Arc - _wayArc[0]:0} m: road at {branch.Bearing:0} deg, {(course ? "back on the course" : "off the course")}"));
    }

    /// <summary>More way ahead when it runs short: the course's next kilometres, or the road straight on.</summary>
    private void Extend(FootPlayer me)
    {
        if (_wayEnded || WayLeft > ExtendBelow || _course is not { } c) return;
        var last = _way[^1];
        var way = Kept(_way.Count);
        int before = way.Count;
        if (last.Course >= 0)
        {
            if (last.Course < c.Points.Count - 1) way.AddRange(CourseFrom(last.Course + 1));
        }
        else if (_net is { } g && _way.Count >= 2 && last.Node >= 0 && _way[^2].Node >= 0)
        {
            var on = JunctionChoice.Follow(g, _way[^2].Node, last.Node, Window);
            AppendRoads(way, on.Skip(1).ToList(), 0);
        }
        if (way.Count == before) { _wayEnded = true; return; }   // the course's end, or a dead end
        SetWay(me, way);
    }

    /// <summary>Back to the course from a road off it: the shortest way from the road ahead (never straight back) to the course within 3 km of where it was left.</summary>
    public void Rejoin()
    {
        if (State != Phase.Riding || _player() is not { } me) return;
        if (OnCourse) { Say("On the course already."); return; }
        if (_net is not { } g || _course is not { } c) { Say("No road map here to find the way back."); return; }
        int k = _near + 1;
        while (k < _way.Count && !(_way[k].Node >= 0 && _way[k - 1].Node >= 0)) k++;
        if (k >= _way.Count) { Say("No road ahead to find the way back from: turn round first."); return; }

        // the course's nodes ahead of where it was left, and the course point each one is
        var goals = new Dictionary<int, int>();
        int from = IndexAt(c.Arc, _courseAt);
        for (int i = from; i < c.Points.Count && c.Arc[i] <= _courseAt + Window; i++)
            foreach (int node in g.NodesNear(c.Points[i].E, c.Points[i].N, CourseReach))
                goals.TryAdd(node, i);
        var route = JunctionChoice.RouteTo(g, _way[k - 1].Node, _way[k].Node, (prev, node) =>
        {
            if (!goals.TryGetValue(node, out int at)) return false;
            var (pe, pn, _) = g.Pos(prev);
            var (ne, nn, _) = g.Pos(node);
            return Math.Abs(JunctionChoice.Bearing(CourseDir(at), (ne - pe, nn - pn))) < 60;
        }, 2 * Window);
        if (route == null)
        {
            Say(InputHints.Format("No way back to the course ahead: turn round ({ride_uturn}) and try again."));
            return;
        }
        var way = Kept(k + 1);
        way.AddRange(route.Skip(1).Select(n => JunctionChoice.Point(g, n)));
        way.AddRange(CourseFrom(goals[route[^1]] + 1));
        SetWay(me, way);
        Rejoining = true;
        Say(string.Create(CultureInfo.InvariantCulture, $"Back to the course: {RouteLength(g, route) / 1000:0.0} km."));
    }

    private static double RouteLength(IWalkGraph g, List<int> route)
    {
        double s = 0;
        for (int i = 1; i < route.Count; i++)
        {
            var (ae, an, _) = g.Pos(route[i - 1]);
            var (be, bn, _) = g.Pos(route[i]);
            s += Math.Sqrt((ae - be) * (ae - be) + (an - bn) * (an - bn));
        }
        return s;
    }

    /// <summary>Stops and turns round, then rides the road back, straight on, until it meets the course going its way.</summary>
    public void UTurn()
    {
        if (State != Phase.Riding || _player() is not { } me || _route == null) return;
        if (_net is not { } g) { Say("No road map here to turn round on."); return; }
        var at = _origin.ToGlobal(me.GlobalPosition);
        // backwards: against the way here
        var a = _way[Math.Max(0, Math.Min(_near, _way.Count - 2))];
        var z = _way[Math.Max(1, Math.Min(_near + 1, _way.Count - 1))];
        var back = (E: a.E - z.E, N: a.N - z.N);
        // the nearest road node, and its road that goes most nearly back
        int node = -1, next = -1;
        double best = double.MaxValue;
        foreach (int n in g.NodesNear(at.E, at.N, 12))
        {
            var (ne, nn, _) = g.Pos(n);
            double d = (ne - at.E) * (ne - at.E) + (nn - at.N) * (nn - at.N);
            if (d < best) { best = d; node = n; }
        }
        double turn = double.MaxValue;
        if (node >= 0)
            foreach (var (to, _) in g.Next(node))
            {
                var (ne, nn, _) = g.Pos(node);
                var (te, tn, _) = g.Pos(to);
                double t = Math.Abs(JunctionChoice.Bearing(back, (te - ne, tn - nn)));
                if (t < turn) { turn = t; next = to; }
            }
        if (node < 0 || next < 0 || turn > 60) { Say("No road to turn round on here."); return; }

        var way = new List<WayPoint> { new(at.E, at.N, at.Alt), JunctionChoice.Point(g, node) };
        AppendRoads(way, JunctionChoice.Follow(g, node, next, Window), SpliceAfter);
        var (ne2, nn2, _) = g.Pos(node);
        var (te2, tn2, _) = g.Pos(next);
        var dir = RaceRoute.Flat(_origin.ToWorld(te2, tn2, 0) - _origin.ToWorld(ne2, nn2, 0));
        me.PlaceAt(me.GlobalPosition, Mathf.Atan2(-dir.X, -dir.Z));
        SetWay(me, way);
        Rejoining = false;
        Say("Turned round.");
    }

    private void Say(string text)
    {
        GD.Print($"[training] {text}");
        Said?.Invoke(text);
    }

    // ---------------------------------------------------------------- the HUD

    private void ShowHud(bool on)
    {
        if (on && _hud == null)
        {
            _hud = new CanvasLayer { Layer = 11, Name = "TrainingHud" };
            var panel = new PanelContainer { OffsetLeft = 20, OffsetTop = 20 };
            var box = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.55f), ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8 };
            box.SetCornerRadiusAll(10);
            panel.AddThemeStyleboxOverride("panel", box);
            _hudText = new Label();
            _hudText.AddThemeFontSizeOverride("font_size", 22);
            panel.AddChild(_hudText);
            _hud.AddChild(panel);
            AddChild(_hud);
        }
        if (_hud != null) _hud.Visible = on;
        _hudShown = "";
    }

    private void UpdateHud(FootPlayer me)
    {
        if (_hudText == null || _course is not { } c || RealClock.Now < _hudAt) return;
        _hudAt = RealClock.Now + 0.25;
        var link = TrainerLink.Instance;
        string power = link?.PowerLive == true ? $"{link.Watts} W" : "no trainer: the keys pedal";
        string cadence = link?.Cadence is { } rpm ? $"   {rpm:0} rpm" : "";
        string hr = link?.HeartRate is { } bpm ? $"   {bpm} bpm" : "";
        string sim = link?.Erg == true ? $"   ERG {link.ErgTarget} W"
            : link?.GradeSent is { } g ? string.Create(CultureInfo.InvariantCulture, $"   trainer {g * 100:0.0}%") : "";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{CourseName}\n{power}{cadence}{hr}\n{me.RideSpeed * 3.6f:0.0} km/h   {GradeHere * 100:+0.0;-0.0;0.0}%{sim}\n"
            + $"{Along / 1000:0.00} / {c.Profile.Length / 1000:0.00} km   {c.Profile.AscentLeft(Along):0} m up left   {Clock(RideSeconds)}")
            + WayLines();
        if (text == _hudShown) return;
        _hudShown = text;
        _hudText.Text = text;
    }

    /// <summary>The HUD's lines about the way: off the course, the junction ahead and its roads, the turn asked for.</summary>
    private string WayLines()
    {
        string lines = "";
        if (!OnCourse)
            lines += Rejoining ? "\nOff the course: on the way back to it"
                : InputHints.Format("\nOff the course: {ride_rejoin} back to it, {ride_uturn} turn round");
        if (TurnAsked != 0) lines += TurnAsked < 0 ? "\nNext junction: left" : "\nNext junction: right";
        if (NextJunction is { } j)
        {
            var roads = j.Branches.Select(b => b.Taken ? $"[{Way(b.Bearing)}]" : Way(b.Bearing));
            lines += string.Create(CultureInfo.InvariantCulture, $"\nJunction in {JunctionIn:0} m: {string.Join("  ", roads)}");
        }
        else if (WayLeft < 150 && !(_course is { } c && _way[^1].Course == c.Points.Count - 1))
            lines += InputHints.Format("\nDead end ahead: {ride_uturn} turn round");
        return lines;
    }

    /// <summary>A road's bearing as an arrow and a word.</summary>
    private static string Way(double bearing) => Math.Abs(bearing) switch
    {
        < 20 => "↑ straight",
        < 60 => bearing < 0 ? "↖ bear left" : "↗ bear right",
        < 135 => bearing < 0 ? "← left" : "→ right",
        _ => bearing < 0 ? "↙ sharp left" : "↘ sharp right",
    };

    private static string Clock(double s) => TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- helpers

    /// <summary>The nearest course point to <paramref name="pos"/>, searched forward from the last one (a course that loops back on itself never jumps ahead).</summary>
    private static int Nearest(IReadOnlyList<Vector3> centre, Vector3 pos, int from)
    {
        int best = from;
        float bestD = float.MaxValue;
        int lo = Math.Max(0, from - 5), hi = Math.Min(centre.Count - 1, from + 60);
        for (int i = lo; i <= hi; i++)
        {
            float d = RaceRoute.Flat(centre[i] - pos).LengthSquared();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Flat distance from <paramref name="pos"/> to the course's segments either side of point <paramref name="i"/>.</summary>
    private static float Off(IReadOnlyList<Vector3> centre, int i, Vector3 pos)
    {
        float best = RaceRoute.Flat(centre[i] - pos).Length();
        for (int j = Math.Max(1, i); j <= Math.Min(centre.Count - 1, i + 1); j++)
        {
            var a = RaceRoute.Flat(centre[j - 1]);
            var ab = RaceRoute.Flat(centre[j]) - a;
            float t = ab.LengthSquared() > 1e-6f ? Mathf.Clamp((RaceRoute.Flat(pos) - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            best = Mathf.Min(best, (RaceRoute.Flat(pos) - (a + ab * t)).Length());
        }
        return best;
    }

    private static int AheadIndex(IReadOnlyList<Vector3> pts, int from, float metres)
    {
        float s = 0;
        for (int i = from + 1; i < pts.Count; i++)
        {
            s += pts[i].DistanceTo(pts[i - 1]);
            if (s >= metres) return i;
        }
        return pts.Count - 1;
    }
}
