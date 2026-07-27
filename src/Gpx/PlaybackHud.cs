using Godot;

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
    private Label _stats = null!;
    private Label _title = null!;
    private VBoxContainer _board = null!;
    private PanelContainer _boardPanel = null!;
    private PanelContainer _controls = null!;
    private Button _toggleButton = null!;
    private bool _uiVisible = true;
    private int _speedIndex = 2;   // 1x
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

        var top = new HBoxContainer();
        rows.AddChild(top);

        _title = new Label { Text = "no track" };
        _title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
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

        var buttons = new HBoxContainer();
        rows.AddChild(buttons);

        _playButton = Button("Pause", () => { _race.TogglePlay(); Refresh(); });
        buttons.AddChild(_playButton);
        buttons.AddChild(Button("<< 10s", () => _race.Seek(_race.Time - 10)));
        buttons.AddChild(Button("10s >>", () => _race.Seek(_race.Time + 10)));
        buttons.AddChild(Button("Restart", () => _race.Seek(0)));

        _speedButton = Button("1x", () =>
        {
            _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
            _race.Speed = SpeedSteps[_speedIndex];
            Refresh();
        });
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

        buttons.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
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

        _finishLabel = new Label
        {
            Text = "Finished",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _finishLabel.AddThemeFontSizeOverride("font_size", 20);
        _finishLabel.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
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

    private static StyleBoxFlat Panel()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 0.82f),
            ContentMarginLeft = 12, ContentMarginRight = 12,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        style.SetCornerRadiusAll(4);
        return style;
    }

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
        _cameraButton.Text = $"Cam: {_camera.Mode}";
        _focusButton.Text = $"Follow: {_race.FocusIndex + 1}";
        _timeline.MaxValue = Math.Max(1, _race.Duration);

        var focused = _race.Focused;
        _title.Text = focused == null
            ? "no track loaded — press G"
            : $"{focused.Track.Name}  —  {focused.Track.Length / 1000:0.00} km" +
              (focused.Track.HasTiming ? "" : "  (no timing, assumed pace)");
    }

    public override void _Process(double _)
    {
        if (!_uiVisible) return;   // nothing on screen to update
        if (!_scrubbing) _timeline.SetValueNoSignal(_race.Time);
        if (Math.Abs(_timeline.MaxValue - Math.Max(1, _race.Duration)) > 0.01) Refresh();

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

        var header = new Label { Text = $"Race — {_race.Runners.Count} runner(s)" };
        header.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        _board.AddChild(header);

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
