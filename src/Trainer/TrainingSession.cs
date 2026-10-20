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
/// reading and the throttle is held; without, the keys pedal as on any ride. The course's grade
/// (its profile, not the terrain's facets) goes to the trainer as SIM through <see cref="TrainerLink"/>.
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
    /// <summary>How far the bike is off the course's line, flat metres: the pilot's hold on it.</summary>
    public float OffCourse { get; private set; }

    private Course? _course;
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

    /// <summary>A course in LV95 with road heights, and the profile along it.</summary>
    private sealed record Course(List<(double E, double N, double H)> Points, CourseProfile Profile);

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
            var source = _chunks.Source;
            MatchResult? match = source == null ? null : await TrackMatcher.MatchAsync(track, source);
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
            profile.Add((s, p.Elevation));
        }
        return new Course(points, new CourseProfile(profile));
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
        Status = "Going to the start...";
        TrainerLink.Instance?.Reconnect();
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
        var world = c.Points.Select(p => _origin.ToWorld(p.E, p.N, p.H)).ToList();
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
            _route = RaceRoute.FromPoints(world, world.Select(_ => 6f).ToList(), Terrain.Format.RoadClass.Road, _origin.Frame);
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
        _pilot = AutoPilot.For(_route, me);
        if (_pilot == null) return;
        _profileWas = GameSettings.Current.RideProfile;
        // watts only mean something in the real-world model (game-sim-profile)
        GameSettings.Current.RideProfile = RideProfile.Sim;
        var pilot = _pilot;
        me.RideControls = () => Controls(me, pilot);
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
        Along = _route.Arc[_near];
        OffCourse = Off(_route.Centre, _near, me.GlobalPosition);
        GradeHere = c.Profile.GradeAt(Along);
        link?.FeedGrade(GradeHere);

        if (Along >= c.Profile.Length - 3)
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
        string sim = link?.GradeSent is { } g ? string.Create(CultureInfo.InvariantCulture, $"   trainer {g * 100:0.0}%") : "";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{CourseName}\n{power}{cadence}{hr}\n{me.RideSpeed * 3.6f:0.0} km/h   {GradeHere * 100:+0.0;-0.0;0.0}%{sim}\n"
            + $"{Along / 1000:0.00} / {c.Profile.Length / 1000:0.00} km   {c.Profile.AscentLeft(Along):0} m up left   {Clock(RideSeconds)}");
        if (text == _hudShown) return;
        _hudShown = text;
        _hudText.Text = text;
    }

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
