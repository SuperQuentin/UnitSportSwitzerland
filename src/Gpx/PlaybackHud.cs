using Godot;
using UnitSport.Ui;

namespace UnitSport.Gpx;

/// <summary>
/// On-screen race controls: timeline scrubber, play/pause, speed, camera switch and a
/// live leaderboard showing each runner's gap to the leader.
/// </summary>
public partial class PlaybackHud : CanvasLayer
{
    private static readonly double[] SpeedSteps = { 0.25, 0.5, 1, 2, 4, 8, 16, 32 };

    private RacePlayback _race = null!;
    private PlaybackCamera _camera = null!;
    private HSlider _timeline = null!;
    private Button _playButton = null!;
    private Button _speedButton = null!;
    private Button _cameraButton = null!;
    private Button _focusButton = null!;
    private Button _snapButton = null!;
    private Button _paceButton = null!;
    private Button _lensButton = null!;
    private OptionButton _shotButton = null!;
    private Button _arrowButton = null!;
    private HSlider _pathSlider = null!;
    private int _paceIndex = 1;   // 1x
    private static readonly float[] PaceSteps = { 0.5f, 1f, 1.5f, 2f, 3f };
    private string _snapState = "";
    private Button _exportButton = null!;
    private Button _fpsButton = null!;
    private int _fpsIndex = 1;
    private static readonly int[] FpsSteps = { 24, 30, 60 };

    /// <summary>The exporter, once the session has one. Null until then.</summary>
    public VideoExporter? Exporter { get; set; }

    /// <summary>Raised with the chosen frame rate when the player asks to export.</summary>
    public event Action<int>? ExportRequested;

    /// <summary>Raised with 0..1 as the course-line slider moves.</summary>
    public event Action<float>? PathOpacityChanged;

    /// <summary>Raised when the lens button is pressed; the session owns the post-process.</summary>
    public event Action? LensCycleRequested;

    /// <summary>Name of the lens in force, written back by whoever handles the cycle.</summary>
    public string LensName { get; set; } = "None";
    private Label _stats = null!;
    private Label _title = null!;
    private VBoxContainer _board = null!;
    private PanelContainer _boardPanel = null!;
    private PanelContainer _controls = null!;
    private VBoxContainer _controlRows = null!;
    private Button _toggleButton = null!;
    private bool _uiVisible = true;
    private int _speedIndex = 2;   // 1x

    /// <summary>
    /// Moves the playback multiplier one step. The button wraps (one button, so it has to);
    /// the D-pad clamps, because up/down pressed past the end should stay there rather than
    /// jump from 32x to 0.25x. Lives here so the button label cannot disagree with the clock.
    /// </summary>
    public void StepSpeed(int direction, bool wrap = false)
    {
        int n = SpeedSteps.Length;
        _speedIndex = wrap
            ? ((_speedIndex + direction) % n + n) % n
            : Math.Clamp(_speedIndex + direction, 0, n - 1);
        _race.Speed = SpeedSteps[_speedIndex];
        Refresh();
    }
    private bool _scrubbing;

    public event Action? AddRequested;
    public event Action? ClearRequested;

    /// <summary>Leave replay entirely and go back to the mode menu.</summary>
    public event Action? ExitRequested;

    private PanelContainer _finishPanel = null!;
    private Label _finishLabel = null!;

    public static PlaybackHud Create(RacePlayback race, PlaybackCamera camera) => new()
    {
        Name = "PlaybackHud",
        _race = race,
        _camera = camera,
    };

    public override void _Ready()
    {
        Layer = 10;

        // --- leaderboard, top left ------------------------------------------------
        _boardPanel = new PanelContainer
        {
            OffsetLeft = 12, OffsetTop = 12, OffsetRight = 360, OffsetBottom = 200,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _boardPanel.AddThemeStyleboxOverride("panel", Panel());
        AddChild(_boardPanel);
        _board = new VBoxContainer();
        _boardPanel.AddChild(_board);

        // --- controls, bottom -----------------------------------------------------
        _controls = new PanelContainer
        {
            AnchorLeft = 0, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1,
            OffsetTop = -104, OffsetLeft = 12, OffsetRight = -12, OffsetBottom = -12,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _controls.AddThemeStyleboxOverride("panel", Panel());
        AddChild(_controls);

        var rows = new VBoxContainer();
        _controls.AddChild(rows);
        _controlRows = rows;

        var top = new HBoxContainer();
        rows.AddChild(top);

        _title = UiTheme.Title("no track", 0);
        top.AddChild(_title);

        top.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _stats = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _stats.AddThemeColorOverride("font_color", new Color(0.85f, 0.87f, 0.90f));
        top.AddChild(_stats);

        _timeline = new HSlider
        {
            MinValue = 0,
            MaxValue = Math.Max(1, _race.Duration),
            Step = 0.05,
            CustomMinimumSize = new Vector2(0, 22),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        // dragging must not fight the clock writing back into the slider
        _timeline.DragStarted += () => _scrubbing = true;
        _timeline.DragEnded += _ => _scrubbing = false;
        _timeline.ValueChanged += v => { if (_scrubbing) _race.Seek(v); };
        rows.AddChild(_timeline);

        // A flow container, not a box: the controls total 1240 px of minimum width and the frame
        // is 1152, so a single row silently cut the last button off the right edge. Flowing wraps
        // to a second line instead, and keeps working if the row grows again or the window is
        // made narrow.
        var buttons = new HFlowContainer();
        rows.AddChild(buttons);

        _playButton = Button("Pause", () => { _race.TogglePlay(); Refresh(); });
        buttons.AddChild(_playButton);
        buttons.AddChild(Button("<< 10s", () => _race.Seek(_race.Time - 10)));
        buttons.AddChild(Button("10s >>", () => _race.Seek(_race.Time + 10)));
        buttons.AddChild(Button("Restart", () => _race.Seek(0)));

        _speedButton = Button("1x", () => StepSpeed(+1, wrap: true));
        buttons.AddChild(_speedButton);

        _cameraButton = Button("Cam: Chase", () =>
        {
            _camera.AdoptCurrentOrientation();
            _camera.CycleMode();
            Refresh();
        });
        buttons.AddChild(_cameraButton);

        _focusButton = Button("Follow: 1", () => { _race.CycleFocus(); Refresh(); });
        buttons.AddChild(_focusButton);

        // Snapping is a claim about the data, not a display option, so the button reports what
        // happened — "matching…", "3/4 on roads", or the reason it could not — rather than just
        // sitting pressed. A track over open mountainside genuinely has no roads to follow, and
        // that has to be visible or the toggle looks broken.
        _snapButton = Button("Roads: off", () =>
        {
            _race.SetSnapToRoads(!_race.SnapToRoads);
            Refresh();
        });
        _snapButton.CustomMinimumSize = new Vector2(132, 26);
        _snapButton.TooltipText =
            "Snap the recorded track onto the mapped road network (R)";
        buttons.AddChild(_snapButton);

        // Cutting rhythm is taste, not a fact, so it is a control rather than a constant. It
        // scales every shot's min and max duration; the durations themselves are already in
        // screen seconds, so a scene runs as long at 32x as it does at 1x.
        _paceButton = Button("Pace: 1x", () =>
        {
            _paceIndex = (_paceIndex + 1) % PaceSteps.Length;
            _camera.CinemaPacing = PaceSteps[_paceIndex];
            Refresh();
        });
        _paceButton.CustomMinimumSize = new Vector2(92, 26);
        _paceButton.TooltipText = "How long Absolute Cinema holds each shot";
        buttons.AddChild(_paceButton);

        _lensButton = Button("Lens: None", () =>
        {
            LensCycleRequested?.Invoke();
            Refresh();
        });
        _lensButton.CustomMinimumSize = new Vector2(132, 26);
        _lensButton.TooltipText =
            "Simulated optics: barrel distortion, chromatic aberration and vignette";
        buttons.AddChild(_lensButton);

        // A manual override, not a replacement for the director: picking a shot here pins the
        // camera to it (Begin is still tested, so it never opens on a bad placement) and leaves
        // the event timeline and pacing to mean nothing until "Auto" is chosen again.
        _shotButton = new OptionButton { CustomMinimumSize = new Vector2(150, 26) };
        _shotButton.AddItem("Shot: Auto");
        foreach (string name in PlaybackCamera.CinemaShotNames) _shotButton.AddItem(name);
        _shotButton.TooltipText = "Override Absolute Cinema's choice of shot";
        _shotButton.ItemSelected += index =>
        {
            _camera.ForcedCinemaShot = index == 0 ? null : _shotButton.GetItemText((int)index);
        };
        buttons.AddChild(_shotButton);

        // The course line is drawn for orientation, and orientation is exactly what you do not
        // want burnt into a video. A slider rather than a toggle because the useful setting for
        // an export is usually a faint one, not none.
        var path = new HBoxContainer();
        path.AddChild(new Label { Text = "Path" });
        _pathSlider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Value = 100, Step = 1,
            CustomMinimumSize = new Vector2(110, 22),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        _pathSlider.TooltipText = "Opacity of the course line";
        _pathSlider.ValueChanged += v => PathOpacityChanged?.Invoke((float)v / 100f);
        path.AddChild(_pathSlider);
        buttons.AddChild(path);

        // Not gated to Cinema: the bubble works from any camera, so the toggle stays available
        // in every mode the way the button it sits next to (Path) does.
        _arrowButton = Button("Bubble: on", () =>
        {
            _camera.ZoomBubbleEnabled = !_camera.ZoomBubbleEnabled;
            Refresh();
        });
        _arrowButton.CustomMinimumSize = new Vector2(104, 26);
        _arrowButton.TooltipText =
            "Show a zoomed-in bubble of the runner when the camera is too far to spot them";
        buttons.AddChild(_arrowButton);

        // Export sits next to the camera and speed controls on purpose: those two are what it
        // records, so the button belongs beside the things it captures.
        _fpsButton = Button("30 fps", () =>
        {
            _fpsIndex = (_fpsIndex + 1) % FpsSteps.Length;
            Refresh();
        });
        _fpsButton.CustomMinimumSize = new Vector2(72, 26);
        _fpsButton.TooltipText = "Frame rate of the exported video";
        buttons.AddChild(_fpsButton);

        _exportButton = Button("Export video", () => ExportRequested?.Invoke(FpsSteps[_fpsIndex]));
        _exportButton.CustomMinimumSize = new Vector2(108, 26);
        _exportButton.Visible = Core.Platform.CanSpawnProcesses; // ffmpeg (#63)
        _exportButton.TooltipText =
            "Render the whole run to a video with the current camera and speed. "
            + "Slower than real time — it waits for terrain to load on every frame.";
        buttons.AddChild(_exportButton);

        buttons.AddChild(Button("+ Add ghost", () => AddRequested?.Invoke()));
        buttons.AddChild(Button("Clear", () => ClearRequested?.Invoke()));
        buttons.AddChild(Button("Exit replay", () => ExitRequested?.Invoke()));

        // --- finish banner, centred ------------------------------------------------
        // A race stops dead at the finish, so without this there is no sign anything
        // ended and no obvious way out of the mode.
        _finishPanel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.32f, AnchorBottom = 0.32f,
            OffsetLeft = -190, OffsetRight = 190, OffsetTop = -54, OffsetBottom = 54,
            Visible = false,
        };
        _finishPanel.AddThemeStyleboxOverride("panel", Panel());
        AddChild(_finishPanel);

        var finishRows = new VBoxContainer();
        finishRows.AddThemeConstantOverride("separation", 8);
        _finishPanel.AddChild(finishRows);

        _finishLabel = UiTheme.Title("Finished", 20);
        _finishLabel.HorizontalAlignment = HorizontalAlignment.Center;
        finishRows.AddChild(_finishLabel);

        var finishButtons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        finishButtons.AddChild(Button("Watch again", () => { _race.Seek(0); _race.Playing = true; }));
        finishButtons.AddChild(Button("Exit replay", () => ExitRequested?.Invoke()));
        finishRows.AddChild(finishButtons);

        // Always-on toggle, pinned top-right. It deliberately sits outside the panels it
        // hides, otherwise hiding the UI would also hide the only way to bring it back.
        _toggleButton = Button("Hide UI", ToggleUi);
        _toggleButton.AnchorLeft = 1;
        _toggleButton.AnchorRight = 1;
        _toggleButton.OffsetLeft = -104;
        _toggleButton.OffsetRight = -12;
        _toggleButton.OffsetTop = 12;
        _toggleButton.OffsetBottom = 38;
        _toggleButton.Modulate = new Color(1, 1, 1, 0.75f);
        AddChild(_toggleButton);

        Refresh();
    }

    /// <summary>Shows or hides the panels, leaving the toggle itself on screen.</summary>
    public void ToggleUi() => SetUiVisible(!_uiVisible);

    public void SetUiVisible(bool visible)
    {
        _uiVisible = visible;
        _boardPanel.Visible = visible;
        _controls.Visible = visible;
        if (!visible) _finishPanel.Visible = false;
        _toggleButton.Text = visible ? "Hide UI" : "Show UI";
        // fade the button right down when hidden so it stays out of screenshots
        _toggleButton.Modulate = new Color(1, 1, 1, visible ? 0.75f : 0.35f);
    }

    private static StyleBoxFlat Panel() => UiTheme.Flat(new Color(0.06f, 0.07f, 0.09f, 0.82f), 4, 12, 8);

    private static Button Button(string text, Action pressed)
    {
        var b = new Godot.Button { Text = text, CustomMinimumSize = new Vector2(88, 26) };
        b.Pressed += pressed;
        return b;
    }

    private void Refresh()
    {
        _playButton.Text = _race.Playing ? "Pause" : "Play";
        _speedButton.Text = SpeedSteps[_speedIndex] < 1
            ? $"{SpeedSteps[_speedIndex]:0.##}x"
            : $"{SpeedSteps[_speedIndex]:0}x";
        _cameraButton.Text = _camera.Mode switch
        {
            CameraMode.Cinema => $"Absolute Cinema — {_camera.CinemaShot}",
            CameraMode.Racing => $"Absolute Racing — {_camera.CinemaShot}",
            _ => $"Cam: {_camera.Mode}",
        };
        // Fixed at the width of the longest shot name. The size used to be recomputed from the
        // text on every refresh, which was harmless only while the label was stale — now that it
        // updates on every cut, a width that tracked the text would reflow the whole flow
        // container several times a minute.
        _cameraButton.CustomMinimumSize = new Vector2(250, 26);
        _paceButton.Text = $"Pace: {PaceSteps[_paceIndex]:0.##}x";
        _paceButton.Visible = _camera.Mode == CameraMode.Cinema;
        _shotButton.Visible = _camera.Mode == CameraMode.Cinema;
        _arrowButton.Text = _camera.ZoomBubbleEnabled ? "Bubble: on" : "Bubble: off";
        _lensButton.Text = $"Lens: {LensName}";
        _focusButton.Text = $"Follow: {_race.FocusIndex + 1}";
        _timeline.MaxValue = Math.Max(1, _race.Duration);

        _snapButton.Text = _race.Matching ? "Roads: matching…"
            : _race.SnapToRoads ? $"Roads: {(_race.SnapStatus.Length > 0 ? _race.SnapStatus : "on")}"
            : "Roads: off";
        _snapButton.Disabled = _race.Matching || _race.Runners.Count == 0;

        bool exporting = Exporter is { Running: true };
        _fpsButton.Text = $"{FpsSteps[_fpsIndex]} fps";
        _fpsButton.Disabled = exporting;
        _exportButton.Text = exporting ? $"Cancel  {Exporter!.Progress:P0}" : "Export video";
        _exportButton.Disabled = _race.Runners.Count == 0;

        var focused = _race.Focused;
        var course = focused?.Active;
        _title.Text = focused == null || course == null
            ? "no track loaded — press G"
            : $"{focused.Track.Name}  —  {course.Length / 1000:0.00} km" +
              (focused.Track.HasTiming ? "" : "  (no timing, assumed pace)") +
              (focused.UseSnapped && focused.Snapped != null ? "  (on roads)" : "");
    }

    public override void _Process(double _)
    {
        if (!_uiVisible) return;   // nothing on screen to update
        // The panel is anchored to the bottom edge, so its height is the offset to its top. Fixed
        // at 104 px it clipped the button row the moment that row wrapped to two lines — and a
        // control you cannot see is a control that does not exist.
        float wanted = _controlRows.GetCombinedMinimumSize().Y + 20;
        if (Math.Abs(_controls.OffsetTop + wanted) > 0.5f) _controls.OffsetTop = -wanted;

        if (!_scrubbing) _timeline.SetValueNoSignal(_race.Time);
        if (Math.Abs(_timeline.MaxValue - Math.Max(1, _race.Duration)) > 0.01) Refresh();

        // Road matching finishes on a worker thread, so nothing the player did marks the moment
        // the button should stop saying "matching". Watching the state it displays is enough —
        // and it is one string comparison, against wiring an event through for one label.
        // The director cuts on its own schedule, so — exactly like road matching — nothing the
        // player does marks the moment the label should change. CinemaCuts is the cheap signal:
        // it ticks once per cut, where comparing the shot NAME would miss a cut back to a shot of
        // the same name. Without this the button showed whichever shot happened to be running the
        // last time any control was pressed, which at 32x is many cuts ago.
        string snapState = $"{_race.Matching}|{_race.SnapToRoads}|{_race.SnapStatus}"
            + $"|{Exporter?.Running}|{Exporter?.Progress:F3}"
            + $"|{_camera.Mode}|{_camera.CinemaCuts}";
        if (snapState != _snapState)
        {
            _snapState = snapState;
            Refresh();
        }

        bool complete = _race.Complete;
        if (complete != _finishPanel.Visible)
        {
            _finishPanel.Visible = complete;
            if (complete)
            {
                var winner = _race.Standings.FirstOrDefault();
                _finishLabel.Text = _race.Runners.Count > 1 && winner != null
                    ? $"Finished — {winner.Track.Name} wins"
                    : "Finished";
            }
        }

        UpdateBoard();

        var focused = _race.Focused;
        if (focused == null) { _stats.Text = ""; return; }

        double pace = focused.Speed > 0.3 ? 1000.0 / focused.Speed / 60.0 : 0;
        string paceText = pace > 0 && pace < 30
            ? $"{(int)pace}:{(int)((pace - (int)pace) * 60):00} /km"
            : "--:-- /km";

        _stats.Text =
            $"{Format(_race.Time)} / {Format(_race.Duration)}   " +
            $"{focused.Distance / 1000:0.00} km   {focused.Speed * 3.6:0.0} km/h   {paceText}";
    }

    /// <summary>
    /// Standings with each runner's gap to the leader, in metres and — where the pace is
    /// known — the seconds that gap represents at the leader's current speed.
    /// </summary>
    private void UpdateBoard()
    {
        foreach (Node child in _board.GetChildren()) child.QueueFree();

        if (_race.Runners.Count == 0)
        {
            _board.AddChild(Hint("Press G to load a GPX track"));
            _board.AddChild(Hint("Space play/pause    C camera"));
            return;
        }

        _board.AddChild(UiTheme.Title($"Race — {_race.Runners.Count} runner(s)", 0));

        var ordered = _race.Standings.ToList();
        double leadDistance = ordered[0].Distance;
        double leadSpeed = ordered[0].Speed;
        int place = 0;

        foreach (var r in ordered)
        {
            place++;
            double behind = leadDistance - r.Distance;
            string gap = place == 1
                ? "leader"
                : leadSpeed > 0.5
                    ? $"-{behind:0} m ({behind / leadSpeed:0.0}s)"
                    : $"-{behind:0} m";

            var row = new Label
            {
                Text = $"{place}. {Trim(r.Track.Name)}  {r.Distance / 1000:0.00} km  {gap}" +
                       (r.Finished ? "  ✓" : ""),
            };
            // tint each row with that runner's colour so the board maps onto the avatars
            row.AddThemeColorOverride("font_color", r == _race.Focused ? r.Tint : r.Tint * 0.75f);
            _board.AddChild(row);
        }
    }

    private static Label Hint(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", new Color(0.85f, 0.87f, 0.90f));
        return l;
    }

    private static string Trim(string name) => name.Length <= 18 ? name : name[..17] + "…";

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
