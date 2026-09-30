using Godot;
using UnitSport.Terrain;

namespace UnitSport.Gpx;

public enum CameraMode
{
    Chase = 0,
    FirstPerson = 1,
    Cinematic = 2,
    Free = 3,

    /// <summary>Absolute Cinema: a director cuts between shots on its own.</summary>
    Cinema = 4,

    /// <summary>Absolute Racing: the director with the car-battle shots (low, tilted, the chaser in frame).</summary>
    Racing = 5,
}

/// <summary>
/// Camera for track playback. Chase and first-person ride the avatar, cinematic stands
/// off and pans as the runner passes, and Free hands control back to the spectator cam.
/// </summary>
public partial class PlaybackCamera : Camera3D
{
    [Export] public CameraMode Mode { get; set; } = CameraMode.Chase;

    private RacePlayback _race = null!;
    private ChunkManager _chunks = null!;
    private Cinema.Director? _director;
    private Cinema.ShotContext? _shotContext;
    private Vector3 _cinematicAnchor;
    private double _sinceAnchorPick = double.MaxValue;
    private float _yaw, _pitch;

    /// <summary>Distance at which the cinematic camera picks a fresh vantage point.</summary>
    private const float CinematicRange = 140f;

    // --- sightline cut --------------------------------------------------------------------

    /// <summary>Radius of the dissolved corridor once it is fully open, in metres.</summary>
    private const float CutRadius = 1.4f;

    /// <summary>How fast it opens and closes, per second. Low enough to read as a fade.</summary>
    private const float CutRamp = 5f;

    private float _cut;

    /// <summary>The running shot's name, for the HUD.</summary>
    public string CinemaShot => _director?.CurrentShot ?? "-";

    public int CinemaCuts => _director?.Cuts ?? 0;
    public int CinemaRejected => _director?.Rejected ?? 0;

    /// <summary>
    /// Multiplies how long the director holds each shot. Stored here as well as on the director,
    /// because the director is created lazily on first entering Cinema and would otherwise
    /// silently discard a setting made before that.
    /// </summary>
    public float CinemaPacing
    {
        get => _pacing;
        set
        {
            _pacing = value;
            if (_director != null) _director.Pacing = value;
        }
    }

    private float _pacing = 1f;

    /// <summary>Every shot the director can be pinned to — "Auto" plus this, for the HUD list.</summary>
    public static IReadOnlyList<string> CinemaShotNames => Cinema.Director.ShotNames;

    /// <summary>
    /// Pins Cinema to one named shot, or null to give the choice back to the director. Stored
    /// here too, same reason as <see cref="CinemaPacing"/>: the director is built lazily on first
    /// entering Cinema, and a forced shot picked before that must not be forgotten.
    /// </summary>
    public string? ForcedCinemaShot
    {
        get => _forcedShot;
        set
        {
            _forcedShot = value;
            _director?.SetForced(value);
        }
    }

    private string? _forcedShot;

    /// <summary>
    /// Hands the director the analysed run. Until this is called Cinema falls back to a chase,
    /// because a director with no timeline has nothing to cut ahead of.
    /// </summary>
    public void SetCinemaPlan(IReadOnlyList<Cinema.CinemaEvent> events, ulong seed)
    {
        // kept, so a director made later (switching Cinema <-> Racing) starts from the same plan
        _events = events;
        _seed = seed;
        _director ??= NewDirector(Mode == CameraMode.Racing);
        _director.Prepare(events, seed);
    }

    private IReadOnlyList<Cinema.CinemaEvent> _events = System.Array.Empty<Cinema.CinemaEvent>();
    private ulong _seed;

    private bool _directorIsRacing;

    private Cinema.Director NewDirector(bool racing = false)
    {
        _directorIsRacing = racing;
        var d = racing ? Cinema.Director.ForRacing() : new Cinema.Director();
        d.Pacing = _pacing;
        if (_events.Count > 0) d.Prepare(_events, _seed);
        d.SetForced(_forcedShot);
        return d;
    }

    public static PlaybackCamera Create(RacePlayback race, ChunkManager chunks) => new()
    {
        Name = "PlaybackCamera",
        _race = race,
        _chunks = chunks,
        Near = 0.08f,
        Far = Core.GameSettings.Current.CameraFar,
        Fov = 70f,
    };

    /// <summary>Raised when the mode changes, so the session can prepare Cinema's plan.</summary>
    public event Action? ModeChanged;

    public void CycleMode()
    {
        Mode = (CameraMode)(((int)Mode + 1) % 6);
        ModeChanged?.Invoke();
        if (Mode == CameraMode.Cinematic) _sinceAnchorPick = double.MaxValue;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Mode != CameraMode.Free) return;
        if (@event is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw -= m.Relative.X * 0.0022f;
            _pitch = Mathf.Clamp(_pitch - m.Relative.Y * 0.0022f, -1.55f, 1.55f);
        }
    }

    public override void _Process(double delta) => Step(delta);

    /// <summary>
    /// One camera update. Separated from <c>_Process</c> so the video exporter can drive it at
    /// the video's frame rate rather than the render loop's — the chase and cinematic modes both
    /// ease with <paramref name="delta"/>, and a frame that waited two seconds for terrain to
    /// stream would otherwise snap the camera across the gap.
    /// </summary>
    public void Step(double delta)
    {
        var focused = _race.Focused;
        if (focused?.Avatar == null)
        {
            // leave nothing dissolved behind when the race empties out
            if (_cut > 0) { _cut = 0; _chunks.SetSightlineCut(Vector3.Zero, Vector3.Zero, 0); }
            _bubble?.HideNow();
            return;
        }

        var target = focused.Avatar.GlobalPosition;
        var heading = focused.Heading;
        float dt = (float)delta;

        // Cinema's shots each set their own FOV through ShotContext.Place; the hand-driven modes
        // have none, so the lens is applied to the base angle here instead.
        if (Mode is not (CameraMode.Cinema or CameraMode.Racing)) Fov = 70f * LensProfile.Current.FovBias;

        switch (Mode)
        {
            case CameraMode.Chase:
            {
                // trail behind and above, easing so the view does not snap on corners
                var desired = target - heading * 7.5f + Vector3.Up * 3.0f;
                GlobalPosition = GlobalPosition.Lerp(desired, 1f - Mathf.Exp(-6f * dt));
                Aim(target + Vector3.Up * 1.2f);
                break;
            }

            case CameraMode.FirstPerson:
            {
                GlobalPosition = target + Vector3.Up * Runner.EyeHeight;
                Aim(GlobalPosition + heading);
                break;
            }

            case CameraMode.Cinematic:
            {
                _sinceAnchorPick += delta;
                // re-place once the runner has gone past, so the camera keeps "catching" them
                if (GlobalPosition.DistanceTo(target) > CinematicRange || _sinceAnchorPick > 14.0)
                {
                    _sinceAnchorPick = 0;
                    var side = new Vector3(-heading.Z, 0, heading.X);
                    float lateral = (GD.Randf() > 0.5f ? 1f : -1f) * (18f + GD.Randf() * 22f);
                    _cinematicAnchor = target + heading * 55f + side * lateral + Vector3.Up * (8f + GD.Randf() * 14f);
                }
                GlobalPosition = _cinematicAnchor;
                Aim(target + Vector3.Up * 1.0f);
                break;
            }

            case CameraMode.Cinema:
            case CameraMode.Racing:
            {
                bool racing = Mode == CameraMode.Racing;
                if (racing && !_directorIsRacing) _director = null;
                if (!racing && _directorIsRacing) _director = null;
                _director ??= NewDirector(racing);
                _shotContext ??= new Cinema.ShotContext
                {
                    Runner = focused, Camera = this, Chunks = _chunks,
                    Rng = new RandomNumberGenerator(),
                };

                // rebuilt when the focus changes, since every mount hangs off the runner
                if (!ReferenceEquals(_shotContext.Runner, focused))
                    _shotContext = new Cinema.ShotContext
                    {
                        Runner = focused, Camera = this, Chunks = _chunks,
                        Rng = _shotContext.Rng,
                    };

                // the nearest other car or runner, for the battle shots
                _shotContext.Rival = _race.Runners.Where(r => r != focused && r.Avatar != null)
                    .OrderBy(r => r.Avatar.GlobalPosition.DistanceSquaredTo(target)).FirstOrDefault();
                _shotContext.Time = _race.Time;
                _shotContext.Dt = dt;
                _shotContext.ClockSpeed = (float)_race.Speed;
                _director.Step(_shotContext);
                break;
            }

            case CameraMode.Free:
            {
                // right stick looks, as a rate; the mouse arrives as events in _UnhandledInput
                var look = Core.PlayerInput.LookRate;
                _yaw -= look.X * dt;
                _pitch = Mathf.Clamp(_pitch - look.Y * dt, -1.55f, 1.55f);

                var basis = new Basis(Vector3.Up, _yaw) * new Basis(Vector3.Right, _pitch);
                Basis = basis;

                // Left stick / WASD through the shared actions so a pad flies this too. Vertical
                // stays on E/Q and the triggers, not the Explore fly_up/fly_down actions: here
                // Space is play/pause, and Shift has always been this camera's boost.
                var stick = Core.PlayerInput.Move;
                bool typing = Core.UiFocus.TextEntryActive;
                float up = typing ? 0f
                    : (Input.IsPhysicalKeyPressed(Key.E) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.Q) ? 1f : 0f)
                      + Input.GetJoyAxis(0, JoyAxis.TriggerRight) - Input.GetJoyAxis(0, JoyAxis.TriggerLeft);
                var move = basis * new Vector3(stick.X, 0, stick.Y) + Vector3.Up * up;
                if (move.LengthSquared() > 1e-6f)
                {
                    bool boost = !typing && (Input.IsPhysicalKeyPressed(Key.Shift)
                        || Core.PlayerInput.Held(Core.PlayerInput.FlyBoost));
                    float speed = boost ? 120f : 25f;
                    GlobalPosition += move.Normalized() * speed * Mathf.Min(move.Length(), 1f) * dt;
                }
                break;
            }
        }

        UpdateSightlineCut(focused, dt);

        // Distance alone, deliberately not gated to one mode or one shot: a Locked-off tripod
        // and a spectator who has flown the Free camera off across the valley are the same
        // problem - the runner is somewhere on screen (or off it) too small to find.
        _bubble ??= ZoomBubble.Create(_chunks);
        if (_bubble.GetParent() == null) GetParent()?.AddChild(_bubble);
        _bubble.Enabled = _bubbleEnabled;

        // A cut (a new Cinema shot, a camera-mode change, following another runner) has already
        // moved the camera this frame, so hide the bubble before it is drawn from the new view -
        // otherwise it slides across the screen to catch up. Cuts are counted, not named: two
        // consecutive shots can share a name.
        int cuts = CinemaCuts;
        if (cuts != _bubbleCuts || Mode != _bubbleMode || !ReferenceEquals(focused, _bubbleRunner))
        {
            if (_bubbleRunner != null) _bubble.OnCameraCut();
            _bubbleCuts = cuts;
            _bubbleMode = Mode;
            _bubbleRunner = focused;
        }
        _bubble.UpdateFrame(focused, this, dt);
    }

    private ZoomBubble? _bubble;
    private bool _bubbleEnabled = true;
    private int _bubbleCuts;
    private CameraMode _bubbleMode;
    private Runner? _bubbleRunner;

    /// <summary>
    /// Turns the zoom bubble on or off. Stored on the camera rather than only on the bubble,
    /// same reason as <see cref="CinemaPacing"/>: the bubble is created lazily on the first
    /// frame with a focused runner, and a toggle made before that must not be forgotten.
    /// </summary>
    public bool ZoomBubbleEnabled
    {
        get => _bubbleEnabled;
        set
        {
            _bubbleEnabled = value;
            if (_bubble != null) _bubble.Enabled = value;
        }
    }

    /// <summary>
    /// Clears whatever stands between the lens and the runner.
    ///
    /// <para>
    /// Only the tracking modes use it. Free and first-person do not: in one the player is the
    /// operator and in the other the lens is inside the runner's own head, so there is nothing
    /// in between to remove.
    /// </para>
    ///
    /// <para>
    /// The radius is RAMPED, never switched. Snapping a corridor open the frame an obstruction
    /// appears reads as geometry popping out of existence; easing it over about a fifth of a
    /// second reads as a fade, which is the whole point. It also rides out the single-frame
    /// flicker a ray gives when it grazes an edge.
    /// </para>
    /// </summary>
    private void UpdateSightlineCut(Runner focused, float dt)
    {
        bool tracking = Mode is CameraMode.Cinema or CameraMode.Chase or CameraMode.Cinematic;
        var head = focused.HeadWorld;

        bool blocked = false;
        if (tracking)
        {
            var space = GetWorld3D().DirectSpaceState;
            var query = PhysicsRayQueryParameters3D.Create(GlobalPosition, head);
            blocked = space.IntersectRay(query).Count > 0;
        }

        float target = blocked ? CutRadius : 0f;
        _cut = dt > 0
            ? Mathf.Lerp(_cut, target, 1f - Mathf.Exp(-CutRamp * dt))
            : target;

        // settle to exactly zero, so the shaders take their disabled branch rather than
        // evaluating a corridor a millimetre wide for every fragment in the world
        if (_cut < 0.02f) _cut = 0f;

        _chunks.SetSightlineCut(GlobalPosition, head, _cut);
    }

    /// <summary>
    /// Points the camera at a world position. Avoids Camera3D.LookAt, which raises a Godot
    /// error when the target coincides with the camera — and an error raised inside a C#
    /// callback can crash the runtime via Godot's stack-capture path.
    /// </summary>
    private void Aim(Vector3 target)
    {
        var dir = target - GlobalPosition;
        if (dir.LengthSquared() < 1e-6f) return;
        dir = dir.Normalized();
        // guard the near-vertical case, where "up" stops defining a roll
        var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.999f ? Vector3.Forward : Vector3.Up;

        // right = forward x up, NOT up x forward. The two differ by a sign, and that sign is the
        // difference between a rotation and a REFLECTION: with the operands the wrong way round
        // this basis has determinant -1, so the camera renders the entire world mirrored. It is
        // invisible on symmetric scenery and obvious the moment you follow a route - every turn
        // you took comes back the other way.
        var right = dir.Cross(up).Normalized();
        Basis = new Basis(right, right.Cross(dir).Normalized(), -dir);
    }

    /// <summary>Called when switching into Free so it starts where the last view was.</summary>
    public void AdoptCurrentOrientation()
    {
        _yaw = Rotation.Y;
        _pitch = Rotation.X;
    }
}
