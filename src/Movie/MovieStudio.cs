using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Ui;

namespace UnitSport.Movie;

/// <summary>
/// The movie studio (#638), opened from the pause menu over the world being played. The live
/// player is frozen and hidden while it is open; puppets replay the movie's clips where they were
/// recorded, and a timeline at the bottom cuts them. Keyboard, mouse, pad (and VR, whose
/// controllers play a pad) all edit: see <see cref="Hints"/>.
/// </summary>
public partial class MovieStudio : Screen
{
    private readonly ClientWorld _world;
    private MovieStage _stage = null!;
    private StudioCamera _camera = null!;
    private TimelineView _timeline = null!;
    private ChunkManager? _chunks;
    private Camera3D? _previousCamera;
    private FootPlayer? _frozen;
    private ProcessModeEnum _frozenMode;
    private Label _time = null!, _title = null!, _hint = null!;
    private Button _play = null!, _reverse = null!;
    private OptionButton _speed = null!;
    private int _focusLane = -1;
    private double _shownTime = -1;
    private SoundImport? _import;
    private Modal? _importModal;
    private ProgressBar? _importBar;
    private Label? _importStatus;
    private CheckButton _worldSound = null!;
    private bool _shownPlaying;

    // the camera track (#669)
    private WorldOrigin _origin = null!;
    private CheckButton _lookThrough = null!;
    private OptionButton _ease = null!, _aim = null!;
    private HSlider _lens = null!;
    private Label _lensLabel = null!;
    private Button _deleteKey = null!;
    private HScrollBar _scroll = null!;
    private bool _syncingScroll;
    private int _aimLanes = -1;

    // several cameras (#675)
    private CameraGizmos _gizmos = null!;
    private OptionButton _camPicker = null!, _viewSource = null!;
    private double _xHeldSince = -1;
    private bool _xFired;
    private const double HoldToCut = 0.4;
    private PopupMenu? _keyMenu;
    private ScrollContainer _timelineBox = null!;
    // folded: only the transport row shows, the scene gets the screen back
    private Control _editsRow = null!, _sidePanel = null!;
    private Button _fold = null!;
    // the preview player (#779)
    private SubViewport _preview = null!;
    // the Shots screen (#777): the library, the picked shot's settings, the stills
    private ShotLibrary _library = null!;
    private ShotInspector _inspector = null!;
    private ShotThumbs _thumbs = null!;
    private Button _shotsTab = null!, _sequencesTab = null!;
    private bool _shotsMode;
    private int _inspected = -1;
    private ColorRect _fade = null!;
    private PreviewCamera _previewCamera = null!;
    private Label _pausedLabel = null!;
    private bool _pausedTree, _wasDisabled3D;
    private ProcessModeEnum _shellMode, _chunksMode;
    /// <summary>Control height in the panel: smaller than the menus' 38 px, so the scene keeps the screen.</summary>
    private const int ToolH = 28;
    private Button _fly = null!;

    private static readonly StringName Forward = PlayerInput.TriggerRight, Backward = PlayerInput.TriggerLeft;
    private static readonly double[] Speeds = { 0.1, 0.25, 0.5, 1, 2, 4 };

    private readonly double? _startAt;

    private MovieStudio(ClientWorld world, double? startAt)
    {
        _world = world;
        _startAt = startAt;
    }

    /// <summary>The studio over <paramref name="world"/>; at movie time <paramref name="startAt"/>, else the newest clip's start.</summary>
    public static MovieStudio Create(ClientWorld world, double? startAt = null) => new(world, startAt) { Name = "MovieStudio" };

    public override void _Ready()
    {
        _chunks = _world.Chunks;
        var origin = _chunks?.Origin ?? _world.StudioPlayer?.Origin ?? new WorldOrigin(2_600_000, 1_200_000);
        _stage = new MovieStage(MovieSession.Project, origin);
        _world.AddChild(_stage);
        _origin = origin;
        _camera = new StudioCamera(origin) { Target = FocusPoint, PadBusy = () => _timeline?.HasFocus() == true };
        _camera.TookOver += () => _lookThrough.SetPressedNoSignal(false);
        _gizmos = new CameraGizmos(_stage, origin, ActorAt);
        _world.AddChild(_gizmos);
        _camera.FlyingChanged += () =>
        {
            _fly.Text = _camera.Flying ? "✈ Flying (F)" : "✈ Fly";
            _hint.Text = _camera.Flying
                ? "Flying: mouse to look  ·  W A S D move, Space / E up, Shift / Q down, Ctrl faster, wheel speed  ·  I camera key  ·  F or Esc: the cursor back"
                : "";
        };
        _world.AddChild(_camera);
        _chunks?.AddAnchor(_camera);

        // full screen (#779): an editor of its own, the scene in a preview player rather than behind the panels
        var backdrop = new Panel { MouseFilter = MouseFilterEnum.Stop };
        backdrop.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.075f, 0.085f) });
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);
        var root = UiKit.VBox(6);
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.OffsetLeft = 10; root.OffsetTop = 8; root.OffsetRight = -10; root.OffsetBottom = -8;
        AddChild(root);

        // the movie as a whole along the top
        var bar = UiKit.HBox(6);
        root.AddChild(bar);
        _title = UiKit.Text("", UiTheme.FontBody, Colors.White, bold: true);
        bar.AddChild(_title);
        // the modes (#777): set up shots, or cut them into sequences
        bar.AddChild(UiKit.Spacer(w: 16));
        var tabs = new ButtonGroup();
        _shotsTab = Tool(bar, "Shots", "Set up the shots: rigs, framing, shake, beat links (Ctrl+1)", () => SetMode(true));
        _sequencesTab = Tool(bar, "Sequences", "Cut the shots into the movie on the timeline (Ctrl+2)", () => SetMode(false));
        foreach (var t in new[] { _shotsTab, _sequencesTab }) { t.ToggleMode = true; t.ButtonGroup = tabs; }
        _sequencesTab.ButtonPressed = true;
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _pausedLabel = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        bar.AddChild(_pausedLabel);
        Tool(bar, "New", "Start an empty movie", NewMovie);
        Tool(bar, "Open", "Open a saved movie", OpenMovie);
        Tool(bar, "Save", "Save this movie", SaveMovie);
        Tool(bar, "Back to game", "Close the editor and play on where you were (Esc)", BackToGame);

        // the player and the camera tools above, the timeline below; the divider drags
        var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(split);
        var upper = UiKit.HBox(8);
        upper.SizeFlagsVertical = SizeFlags.ExpandFill;
        upper.SizeFlagsStretchRatio = 1.6f;
        split.AddChild(upper);

        _thumbs = new ShotThumbs(_stage, origin, ActorAt);
        AddChild(_thumbs);
        _library = new ShotLibrary(_thumbs) { Visible = false };
        _library.Picked += i => { _timeline.Pick(i); RefreshCameras(); };
        _library.AddShot.Pressed += NewShot;
        _library.AddGroup.Pressed += NewGroup;
        upper.AddChild(_library);

        var playerColumn = UiKit.VBox(4);
        playerColumn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        upper.AddChild(playerColumn);
        var aspect = new AspectRatioContainer
        {
            Ratio = 16f / 9f, StretchMode = AspectRatioContainer.StretchModeEnum.Fit,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        playerColumn.AddChild(aspect);
        // the preview: the same world through a camera that follows the studio's; the mouse on it orbits and zooms
        var screen = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Stop, FocusMode = FocusModeEnum.None };
        screen.GuiInput += OnViewInput;
        aspect.AddChild(screen);
        _preview = new SubViewport { HandleInputLocally = false };
        screen.AddChild(_preview);
        // a fade to black over the player (#776): transitions and Fade beat links
        _fade = new ColorRect { Color = new Color(0, 0, 0, 0), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _fade.SetAnchorsPreset(LayoutPreset.FullRect);
        screen.AddChild(_fade);
        _previewCamera = new PreviewCamera(_camera);
        _preview.AddChild(_previewCamera);
        _hint = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextDim);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        playerColumn.AddChild(_hint);

        // the transport, under the player
        var transport = Row();
        transport.Alignment = FlowContainer.AlignmentMode.Center;
        playerColumn.AddChild(transport);
        Tool(transport, "|◀", "To the start (Home)", () => Seek(0));
        _reverse = Tool(transport, "◀", "Play backwards (J)", () => Play(-1));
        _play = Tool(transport, "▶", "Play / pause (Space)", () => Play(1));
        Tool(transport, "▶|", "To the end (End)", () => Seek(_stage.Duration));
        Tool(transport, "-1f", "One frame back (←)", () => Step(-1 / Channels.Rate));
        Tool(transport, "+1f", "One frame on (→)", () => Step(1 / Channels.Rate));
        _speed = new OptionButton { TooltipText = "Playback speed" };
        foreach (double s in Speeds) _speed.AddItem($"{s:0.##}×");
        _speed.Selected = Array.IndexOf(Speeds, 1.0);
        transport.AddChild(_speed);
        _time = UiKit.Text("", UiTheme.FontBody, UiTheme.Amber, bold: true);
        _time.CustomMinimumSize = new Vector2(150, 0);
        _time.HorizontalAlignment = HorizontalAlignment.Center;
        transport.AddChild(_time);
        _fold = Tool(transport, "Enlarge", "The player over the whole editor, to frame a shot (H); again to bring the panels back", ToggleFold);

        // the cameras and the sound, beside the player
        var side = Row();
        side.CustomMinimumSize = new Vector2(300, 0);
        upper.AddChild(side);
        _sidePanel = side;
        _inspector = new ShotInspector(() => MovieSession.Project, () => _stage.Time) { Visible = false };
        _inspector.FrameFromView += () => KeyFromView(add: false);
        _inspector.AddMove += () => KeyFromView(add: true);
        _inspector.Changed += () => { _thumbs.Invalidate(); _timeline.Resync(); };
        upper.AddChild(_inspector);
        // the camera track: a key from the view, the view through the track, and the key's options
        _fly = Tool(side, "✈ Fly", "Fly the camera freely: mouse to look, WASD, Space / Shift up and down, Ctrl faster, wheel speed; F or Esc for the cursor (pad: L3)", () => _camera.SetFlying(!_camera.Flying));
        _camPicker = new OptionButton { TooltipText = "The camera keys go to (1-9; click a camera's name on the timeline)" };
        _camPicker.ItemSelected += i => _timeline.Pick((int)i);
        side.AddChild(_camPicker);
        Tool(side, "+ Cam", "Another camera, its own keys and point of view", AddCamera);
        Tool(side, "◆ Key", "A key of the picked camera from this view at the playhead (I, also while flying; pad View)", SetKey);
        Tool(side, "✂ Cut to", "The movie cuts to the picked camera at the playhead (C; pad: hold X)", CutToPicked);
        _lookThrough = new CheckButton { Text = "Look through", TooltipText = "Play the movie through the cameras (V); moving the view takes it over" };
        side.AddChild(_lookThrough);
        _viewSource = new OptionButton { TooltipText = "Look through the Program (the cut movie) or the picked camera" };
        _viewSource.AddItem("Program");
        _viewSource.AddItem("Picked camera");
        side.AddChild(_viewSource);
        _ease = new OptionButton { TooltipText = "How the camera goes on to the next key" };
        foreach (var name in new[] { "Smooth", "Linear", "Cut" }) _ease.AddItem(name);
        _ease.ItemSelected += i => { if (_timeline.SelectedKey is { } k) k.Ease = (KeyEase)(int)i; };
        side.AddChild(_ease);
        _aim = new OptionButton { TooltipText = "Keep the camera aimed at an actor from this key" };
        _aim.ItemSelected += i => { if (_timeline.SelectedKey is { } k) k.LookAt = (int)i - 1; };
        side.AddChild(_aim);
        _lens = new HSlider { MinValue = CameraTrack.MinLens, MaxValue = CameraTrack.MaxLens, Step = 1, Value = 24, CustomMinimumSize = new Vector2(150, 0), TooltipText = "Lens, mm (the key's, or the view's)" };
        _lens.ValueChanged += OnLens;
        side.AddChild(_lens);
        _lensLabel = UiKit.Text("24 mm", UiTheme.FontSmall, UiTheme.Text);
        _lensLabel.CustomMinimumSize = new Vector2(52, 0);
        side.AddChild(_lensLabel);
        _deleteKey = Tool(side, "✕", "Delete this camera key", () => { if (_timeline.SelectedKey is { } k) DeleteKey(k); });
        Tool(side, "Music…", "Add a song (.wav, .ogg, .mp3) at the playhead; or drop it on the window", PickMusic);
        _worldSound = new CheckButton { Text = "World sound", ButtonPressed = true, TooltipText = "The puppets' live engines and steps; off when the recorded game sound plays instead" };
        _worldSound.Toggled += WorldSound;
        side.AddChild(_worldSound);

        // the edits and the timeline
        var lower = UiKit.VBox(4);
        lower.SizeFlagsVertical = SizeFlags.ExpandFill;
        split.AddChild(lower);
        _editsRow = lower;
        var edits = Row();
        lower.AddChild(edits);
        Tool(edits, "Split", "Cut the selected clip at the playhead, or every lane if it is not under it (S)", Split);
        Tool(edits, "Cut all", "Cut every clip under the playhead (Shift+S); Ctrl+click a clip cuts it there", CutAll);
        Tool(edits, "Duplicate", "A copy after it (Ctrl+D)", Duplicate);
        Tool(edits, "Delete", "Remove the selected clip or camera key (Del)", Delete);
        edits.AddChild(UiKit.Spacer(w: 10));
        edits.AddChild(UiKit.Text("Zoom", UiTheme.FontSmall, UiTheme.TextDim));
        Tool(edits, "−", "Zoom out (− , the wheel on the timeline)", () => _timeline.ZoomBy(0.66f));
        Tool(edits, "Fit", "The whole movie in view", () => _timeline.Fit());
        Tool(edits, "+", "Zoom in (= , the wheel on the timeline)", () => _timeline.ZoomBy(1.5f));

        _timeline = new TimelineView(_stage);
        // picking a clip only picks it: the camera is the studio's own, it does not jump to the actor
        _timeline.SelectionChanged += FocusSelected;
        _timeline.KeySelectionChanged += ShowKeyOptions;
        _timeline.KeyMenuRequested += OpenKeyMenu;
        _timeline.CutMenuRequested += OpenCutMenu;
        _timeline.CameraMenuRequested += OpenCameraMenu;
        _timeline.PickedCameraChanged += RefreshCameras;
        _timeline.ViewChanged += SyncScroll;
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_timeline);
        lower.AddChild(scroll);
        _timelineBox = scroll;
        _scroll = new HScrollBar { Step = 0, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _scroll.ValueChanged += v => { if (!_syncingScroll) _timeline.Left = v; };
        lower.AddChild(_scroll);
        foreach (var row in new Control[] { bar, transport, side, edits })
            foreach (var c in row.GetChildren().OfType<BaseButton>()) Small(c);
        ShowKeyOptions();
        Freeze();

        PlayerInput.DeviceChanged += ShowHints;
        ShowHints();
        GetWindow().FilesDropped += OnFilesDropped;
        // the recorded game sound and the puppets' live sound would play twice
        if (MovieSession.Project.Audio.Any(a => a.Game)) _worldSound.ButtonPressed = false;
        Callable.From(() =>
        {
            _timeline.Fit();
            // the newest clip, where the last grab put it: what the player just did
            if (MovieSession.Project.Clips.Count > 0)
            {
                var last = MovieSession.Project.Clips.MaxBy(c => c.Start)!;
                _timeline.Select(last.Id);
                Seek(_startAt ?? last.Start);
            }
            else Seek(0);
            // a free camera of its own, placed once looking at the action
            Callable.From(PlaceCamera).CallDeferred();
        }).CallDeferred();
    }

    public override void OnShown() => _play.CallDeferred(Control.MethodName.GrabFocus);

    public override void _ExitTree()
    {
        PlayerInput.DeviceChanged -= ShowHints;
        GetWindow().FilesDropped -= OnFilesDropped;
        if (IsInstanceValid(_camera) && _camera.Flying) _camera.SetFlying(false);
        Audio.SfxBus.ApplyVolumes();   // the world's buses as the settings have them again
        if (IsInstanceValid(_camera))
        {
            // the world may be on its way out with the studio still open (leaving to the menu)
            if (_chunks != null && IsInstanceValid(_chunks)) _chunks.RemoveAnchor(_camera);
            _camera.QueueFree();
        }
        if (IsInstanceValid(_stage)) _stage.QueueFree();
        if (IsInstanceValid(_gizmos)) _gizmos.QueueFree();
        Thaw();
    }

    // ---- the world while the studio is open ----------------------------------------------------

    /// <summary>The player stops where it is, unseen, and nothing records the studio's puppets as play.</summary>
    private void Freeze()
    {
        _previousCamera = GetViewport().GetCamera3D();
        _camera.MakeCurrent();
        if (ReplayRecorder.Instance is { } recorder) recorder.Paused = true;
        // the recorded world takes the live one's place (#775)
        ReplayWatch.HideLive();
        // editing time (#779): offline the game stops until "Back to game"; online it cannot, and the
        // player stays as it was left (it only goes out of the preview, where its puppet stands)
        bool online = Net.NetLink.Online(this);
        if (_world.StudioPlayer is { } player)
        {
            _frozen = player;
            _frozenMode = player.ProcessMode;
            if (!online) player.ProcessMode = ProcessModeEnum.Disabled;
            player.Visible = false;
        }
        if (!online)
        {
            _pausedTree = true;
            ProcessMode = ProcessModeEnum.Always;
            _stage.ProcessMode = _camera.ProcessMode = _gizmos.ProcessMode = ProcessModeEnum.Always;
            _shellMode = Shell.ProcessMode;
            Shell.ProcessMode = ProcessModeEnum.Always;   // the menus and their Esc
            if (_chunks != null)
            {
                // the terrain keeps loading where the camera flies
                _chunksMode = _chunks.ProcessMode;
                _chunks.ProcessMode = ProcessModeEnum.Always;
            }
            GetTree().Paused = true;
        }
        _pausedLabel.Text = online ? "Online: the game goes on" : "Game paused";
        // nothing draws the game's own view behind the editor: the preview is the only 3D drawn
        _wasDisabled3D = GetTree().Root.Disable3D;
        GetTree().Root.Disable3D = true;
    }

    private void Thaw()
    {
        GetTree().Root.Disable3D = _wasDisabled3D;
        if (_pausedTree)
        {
            _pausedTree = false;
            GetTree().Paused = false;
            if (IsInstanceValid(Shell)) Shell.ProcessMode = _shellMode;
            if (_chunks != null && IsInstanceValid(_chunks)) _chunks.ProcessMode = _chunksMode;
        }
        if (_frozen != null && IsInstanceValid(_frozen))
        {
            _frozen.ProcessMode = _frozenMode;
            _frozen.Visible = true;
        }
        ReplayWatch.ShowLive();
        if (_previousCamera != null && IsInstanceValid(_previousCamera)) _previousCamera.MakeCurrent();
        if (ReplayRecorder.Instance is { } recorder) recorder.Paused = false;
    }

    // ---- per frame -----------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        // a pad's triggers shuttle: the harder, the faster
        if (_import != null) StepImport();
        float shuttle = Input.GetActionStrength(Forward) - Input.GetActionStrength(Backward);
        if (_lookThrough.ButtonPressed) ShowThroughCamera(); else SetFade(0);
        if (_shotsMode) StepShots();
        _gizmos.Hidden = _lookThrough.ButtonPressed;
        if (_camPicker.ItemCount != MovieSession.Project.Cameras.Count) RefreshCameras();
        // a pad's X held: cut to the picked camera (a tap splits, on release); a hold is real time, whatever the game does
        if (_xHeldSince >= 0 && !_xFired && RealClock.Now - _xHeldSince >= HoldToCut)
        {
            _xFired = true;
            CutToPicked();
        }
        if (MovieSession.Project.Lanes.Count != _aimLanes) FillAim();
        if (Math.Abs(shuttle) > 0.15f) { _stage.Playing = false; _stage.Seek(_stage.Time + shuttle * 2 * delta); }

        // text only when the second shown changes, not every frame of playback
        double shown = Math.Round(_stage.Time) * 10000 + Math.Round(_stage.Duration);
        if (shown != _shownTime || _stage.Playing != _shownPlaying)
        {
            _shownTime = shown;
            _shownPlaying = _stage.Playing;
            _time.Text = $"{MovieSession.Clock(_stage.Time)} / {MovieSession.Clock(_stage.Duration)}";
            _play.Text = _stage.Playing && _stage.Speed > 0 ? "❚❚" : "▶";
            _reverse.Text = _stage.Playing && _stage.Speed < 0 ? "❚❚" : "◀";
        }
    }

    private void ShowHints()
    {
        var p = MovieSession.Project;
        _title.Text = $"Movie studio  ·  {p.Name}";
        _hint.Text = p.Clips.Count == 0
            ? "Nothing recorded yet: play, then press F5 (or Pause > Movie studio) to edit the last minutes."
            : "";
    }

    /// <summary>The selected actor's puppet, or the first one on screen.</summary>
    private Vector3? FocusPoint()
    {
        if (_stage.Puppet(_focusLane) is { } focused) return focused.GlobalPosition;
        for (int lane = 0; lane < MovieSession.Project.Lanes.Count; lane++)
            if (_stage.Puppet(lane) is { } any) return any.GlobalPosition;
        return null;
    }

    /// <summary>The free camera behind whoever is on screen, close for a figure, further back for a ride.</summary>
    private void PlaceCamera()
    {
        var puppet = _stage.Puppet(_focusLane);
        for (int lane = 0; lane < MovieSession.Project.Lanes.Count && puppet == null; lane++) puppet = _stage.Puppet(lane);
        if (puppet != null) _camera.PlaceNear(puppet.GlobalPosition, puppet.Ride == RideKind.OnFoot ? 7f : 24f);
        else _camera.PlaceNear(_camera.GlobalPosition, 7f);
    }

    /// <summary>Follow the focused actor (Tab): the view orbits them, close for a figure, further back for a ride.</summary>
    private void Frame()
    {
        var puppet = _stage.Puppet(_focusLane);
        if (puppet == null)
            for (int lane = 0; lane < MovieSession.Project.Lanes.Count && puppet == null; lane++) puppet = _stage.Puppet(lane);
        _camera.Snap(puppet == null || puppet.Ride == RideKind.OnFoot ? 7f : 24f);
    }

    private void FocusSelected()
    {
        if (MovieSession.Project.Find(_timeline.Selected) is { Audio: false } clip) _focusLane = clip.Lane;
    }

    // ---- input -----------------------------------------------------------------------------------

    private void OnViewInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                _camera.Steering = true;   // held: W A S D, Q E fly the view
                break;
            case InputEventMouseMotion m when (m.ButtonMask & (MouseButtonMask.Right | MouseButtonMask.Middle)) != 0:
                _camera.Turn(m.Relative * 0.006f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _camera.Zoom(0.88f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _camera.Zoom(1.14f);
                break;
        }
    }

    public override void _Input(InputEvent e)
    {
        if (Modal.Current != null || UiFocus.TextEntryActive || !Visible) return;
        if (_camera.Flying) { FlyingInput(e); return; }
        if (e is InputEventKey { Pressed: true } k)
        {
            bool handled = true;
            switch (k.PhysicalKeycode)
            {
                case Key.Space: Play(1); break;
                case Key.L: Play(1); break;
                case Key.K: _stage.Playing = false; break;
                case Key.J: Play(-1); break;
                case Key.Left when k.CtrlPressed: ToMarker(-1); break;
                case Key.Right when k.CtrlPressed: ToMarker(1); break;
                case Key.Left: Step(k.ShiftPressed ? -1 : -1 / Channels.Rate); break;
                case Key.Right: Step(k.ShiftPressed ? 1 : 1 / Channels.Rate); break;
                case Key.M: Mark(); break;
                case Key.Home: Seek(0); break;
                case Key.End: Seek(_stage.Duration); break;
                case Key.S when !k.CtrlPressed && k.ShiftPressed: CutAll(); break;
                case Key.S when !k.CtrlPressed && !_camera.Steering: Split(); break;
                case Key.I: SetKey(); break;
                case Key.V: _lookThrough.ButtonPressed = !_lookThrough.ButtonPressed; break;
                case Key.Equal: _timeline.ZoomBy(1.5f); break;
                case Key.Minus: _timeline.ZoomBy(0.66f); break;
                case Key.Delete: Delete(); break;
                case >= Key.Key1 and <= Key.Key9 when !k.CtrlPressed: _timeline.Pick((int)(k.PhysicalKeycode - Key.Key1)); break;
                case Key.C when !k.CtrlPressed: CutToPicked(); break;
                case Key.D when k.CtrlPressed: Duplicate(); break;
                case Key.Tab: NextActor(); break;
                case Key.H: ToggleFold(); break;
                case Key.Key1 when k.CtrlPressed: SetMode(true); break;
                case Key.Key2 when k.CtrlPressed: SetMode(false); break;
                case Key.N when _shotsMode: NewShot(); break;
                case Key.G when _shotsMode: KeyFromView(add: false); break;
                case Key.F: _camera.SetFlying(true); break;
                // flying: W A S D Q E belong to the view, not to the shortcuts above (S, D)
                case Key.W or Key.A or Key.D or Key.Q or Key.E when _camera.Steering: break;
                default: handled = false; break;
            }
            if (handled) GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventJoypadButton { ButtonIndex: JoyButton.X } x)
        {
            // X: a tap splits, held it cuts to the picked camera (#675)
            if (x.Pressed) { _xHeldSince = RealClock.Now; _xFired = false; }
            else if (_xHeldSince >= 0)
            {
                if (!_xFired) Split();
                _xHeldSince = -1;
            }
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventJoypadButton { Pressed: true } b)
        {
            if (b.ButtonIndex == JoyButton.Y) { Play(1); GetViewport().SetInputAsHandled(); }
            else if (b.ButtonIndex == JoyButton.RightStick) { Mark(); GetViewport().SetInputAsHandled(); }
            else if (b.ButtonIndex == JoyButton.Back) { SetKey(); GetViewport().SetInputAsHandled(); }
            else if (b.ButtonIndex == JoyButton.LeftStick) { _camera.SetFlying(true); GetViewport().SetInputAsHandled(); }
        }
    }

    /// <summary>
    /// While flying, every key and button belongs to the camera (it polls them): none reaches a button
    /// with focus or closes the studio. Only I (a key), F, Esc / L3 / Start (stop flying) and the pad's
    /// View (a key) do something here.
    /// </summary>
    private void FlyingInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.PhysicalKeycode == Key.I) SetKey();
            else if (k.PhysicalKeycode is Key.F or Key.Escape) _camera.SetFlying(false);
        }
        else if (e is InputEventJoypadButton { Pressed: true } b)
        {
            if (b.ButtonIndex == JoyButton.Back) SetKey();
            else if (b.ButtonIndex is JoyButton.LeftStick or JoyButton.Start) _camera.SetFlying(false);
        }
        if (e is InputEventKey or InputEventJoypadButton or InputEventJoypadMotion) GetViewport().SetInputAsHandled();
    }

    // ---- actions ---------------------------------------------------------------------------------

    /// <summary>A control at the panel's smaller size: a smaller font, less padding. In the tree, so its theme is found.</summary>
    private static void Small(Control c)
    {
        c.CustomMinimumSize = new Vector2(c.CustomMinimumSize.X, ToolH);
        c.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        foreach (var name in SmallStyles)
        {
            if (c.GetThemeStylebox(name)?.Duplicate() is not StyleBox box) continue;
            box.ContentMarginTop = box.ContentMarginBottom = 2;
            box.ContentMarginLeft = Math.Min(box.ContentMarginLeft, 10);
            box.ContentMarginRight = Math.Min(box.ContentMarginRight, 10);
            c.AddThemeStyleboxOverride(name, box);
        }
    }

    private static readonly string[] SmallStyles = { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" };

    private static HFlowContainer Row()
    {
        var row = new HFlowContainer();
        row.AddThemeConstantOverride("h_separation", 4);
        row.AddThemeConstantOverride("v_separation", 4);
        return row;
    }

    /// <summary>The player over the whole editor, to frame a shot, or the panels back (#779): the transport stays.</summary>
    private void ToggleFold()
    {
        bool show = !_editsRow.Visible;
        _editsRow.Visible = _sidePanel.Visible = show;
        _fold.Text = show ? "Enlarge" : "Shrink";
    }

    // ---- the Shots screen (#777) -----------------------------------------------------------------

    /// <summary>
    /// Shots (the library, the player looking through the picked shot, its settings) or Sequences (the
    /// camera tools and the timeline). The player and the transport stay in both.
    /// </summary>
    private void SetMode(bool shots)
    {
        _shotsMode = shots;
        _shotsTab.SetPressedNoSignal(shots);
        _sequencesTab.SetPressedNoSignal(!shots);
        _library.Visible = _inspector.Visible = shots;
        _sidePanel.Visible = !shots;
        _editsRow.Visible = !shots;
        if (shots)
        {
            // the player films through the shot being set up, until the view is flown to frame it
            _viewSource.Selected = 1;
            _lookThrough.ButtonPressed = true;
            _inspected = -1;
            _thumbs.Invalidate();
        }
        else _thumbs.Hovered = -1;
    }

    /// <summary>A new shot from where the view is, at the playhead, picked and shown in the settings.</summary>
    private void NewShot()
    {
        var p = MovieSession.Project;
        var cam = p.AddCamera();
        cam.Name = $"Shot {p.Cameras.Count}";
        if (_timeline.PickedCamera >= 0 && _timeline.PickedCamera < p.Cameras.Count - 1) cam.Group = p.Cameras[_timeline.PickedCamera].Group;
        _timeline.Pick(p.Cameras.Count - 1);
        RefreshCameras();
        SetKey();
        _thumbs.Invalidate();
    }

    /// <summary>
    /// <c>--movieshots</c>, for screenshots (#777): the Shots screen with a chase (engine shake, a lens beat
    /// link) and a lead on the first actor, a wide world shot, in two groups, the chase picked and hovered.
    /// </summary>
    public void ScreenshotShots()
    {
        var p = MovieSession.Project;
        if (p.Lanes.Count == 0) return;
        SetMode(true);
        var chase = p.Camera;
        chase.Name = "Wing chase"; chase.Group = "Take-off"; chase.Rig = ShotRig.Chase; chase.Target = 0; chase.Smoothing = 0.5f;
        chase.Keys.Clear();
        chase.Shake = new ShakeSettings { Style = ShakeStyle.Engine, Strength = 0.4f };
        chase.Links.Add(new BeatLink { Param = BeatParam.Lens, Shape = BeatShape.Pulse, Amount = 12, Every = 1 });
        var lead = p.AddCamera();
        lead.Name = "Lead low"; lead.Group = "Take-off"; lead.Rig = ShotRig.Lead; lead.Target = 0; lead.Smoothing = 0.3f;
        var wide = p.AddCamera();
        wide.Name = "Runway wide"; wide.Group = "Landing";
        _timeline.Pick(2);
        SetKey();
        _timeline.Pick(0);
        RefreshCameras();
        _inspected = -1;
        _thumbs.Invalidate();
        Callable.From(() => _thumbs.Hovered = 1).CallDeferred();
    }

    /// <summary>The picked shot into a new group, named in a prompt.</summary>
    private void NewGroup()
    {
        var p = MovieSession.Project;
        int i = _timeline.PickedCamera;
        if (i < 0 || i >= p.Cameras.Count) return;
        Modal.Prompt(this, "New group", "The picked shot goes in it", "", "Take-off",
            name => name.Trim().Length == 0 ? "Give it a name" : null, name =>
        {
            if (name.Trim().Length == 0) return;
            p.Cameras[i].Group = name.Trim();
            _inspected = -1;
            _thumbs.Invalidate();
        });
    }

    /// <summary>
    /// "Frame from view" (or with <paramref name="add"/>, "Add move"): the picked shot keyed from the view
    /// at the playhead, in its actor's frame if it follows one.
    /// </summary>
    private void KeyFromView(bool add)
    {
        var p = MovieSession.Project;
        int i = _timeline.PickedCamera;
        var at = _origin.ToGlobal(_camera.GlobalPosition);
        var key = p.FrameFromView(i, _stage.Time, _stage.Mount, at.E, at.N, at.Alt, _camera.GlobalTransform.Basis.GetRotationQuaternion(), _camera.Lens, add);
        if (key == null)
        {
            Modal.Inform(this, "Nothing to follow here", "The actor this shot follows has no clip at this time.");
            return;
        }
        _timeline.SelectKey(key);
        _timeline.Resync();
        _inspected = -1;
        _thumbs.Invalidate();
        // back to looking through the shot, now framed
        _lookThrough.ButtonPressed = true;
    }

    /// <summary>The Shots screen per frame: the library's rows, and the settings of the picked shot.</summary>
    private void StepShots()
    {
        var p = MovieSession.Project;
        _library.Refresh(p, _timeline.PickedCamera);
        if (_inspected != _timeline.PickedCamera)
        {
            _inspected = _timeline.PickedCamera;
            _inspector.Show(_inspected);
        }
    }

    /// <summary>Out of the editor, straight back into the game where it was left.</summary>
    private void BackToGame() => Shell.CloseAll();

    /// <summary>Esc / B goes back to the game too, not to the pause menu it may have been opened from.</summary>
    public override bool OnBack()
    {
        if (_camera.Flying) return false;
        Callable.From(BackToGame).CallDeferred();
        return false;
    }

    private static Button Tool(Container into, string text, string tip, Action pressed)
    {
        var b = UiKit.Button(text);
        b.TooltipText = tip;
        b.Pressed += pressed;
        into.AddChild(b);
        return b;
    }

    private void Play(int direction) => _stage.TogglePlay(direction * Speeds[Math.Max(0, _speed.Selected)]);

    private void Seek(double t)
    {
        _stage.Playing = false;
        _stage.Seek(t);
    }

    private void Step(double seconds) => Seek(_stage.Time + seconds);

    private void NextActor()
    {
        int lanes = MovieSession.Project.Lanes.Count;
        for (int k = 1; k <= lanes; k++)
        {
            int lane = (_focusLane + k) % lanes;
            if (_stage.Puppet(lane) == null) continue;
            _focusLane = lane;
            if (MovieSession.Project.ActiveClip(lane, _stage.Time) is { } clip) _timeline.Select(clip.Id);
            Frame();
            return;
        }
    }

    /// <summary>The selected clip cut at the playhead; with none selected under it, every lane (#669).</summary>
    private void Split()
    {
        var p = MovieSession.Project;
        if (p.Find(_timeline.Selected) is { } c && c.Covers(_stage.Time))
        {
            if (p.Split(c.Id, _stage.Time) is { } right) _timeline.Select(right.Id);
        }
        else CutAll();
        _stage.Apply();
    }

    private void CutAll()
    {
        MovieSession.Project.CutAll(_stage.Time);
        _stage.Apply();
    }

    private void Duplicate()
    {
        if (MovieSession.Project.Duplicate(_timeline.Selected) is { } copy) _timeline.Select(copy.Id);
        _stage.Apply();
    }

    private void Delete()
    {
        if (_timeline.SelectedKey is { } key) { DeleteKey(key); return; }
        if (_timeline.SelectedCut is { } cut) { MovieSession.Project.RemoveCut(cut); _timeline.SelectCut(null); return; }
        if (MovieSession.Project.Delete(_timeline.Selected)) _timeline.Select(0);
        _stage.Seek(_stage.Time);
    }

    private void NewMovie()
    {
        Modal.Confirm(this, "Start a new movie?", "The clips here are dropped unless you saved them.", "New movie", () =>
        {
            MovieSession.NewProject();
            Reload();
        }, danger: true);
    }

    private void OpenMovie()
    {
        var saved = MovieSession.Saved();
        if (saved.Count == 0) { Modal.Inform(this, "No saved movies", $"Saved movies go to {MovieSession.Folder}."); return; }
        var answers = saved.Take(8).Select(name => (name, (Action)(() =>
        {
            if (MovieSession.Load(name) is { } error) Modal.Inform(this, "Could not open it", error);
            else Reload();
        }))).ToArray();
        Modal.Choose(this, "Open a movie", "The newest first.", answers);
    }

    private void SaveMovie()
    {
        Modal.Prompt(this, "Save movie", $"In {MovieSession.Folder}", MovieSession.Project.Name, "Name",
            name => name.Trim().Length == 0 ? "Give it a name" : null, name =>
        {
            if (MovieSession.Save(name) is { } error) Modal.Inform(this, "Could not save it", error);
            else Reload();   // saving compacts: lanes and clips are renumbered
        });
    }

    // ---- sound (#656) ----------------------------------------------------------------------------

    private void PickMusic()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.wav, *.ogg, *.mp3 ; Sound" },
            UseNativeDialog = true,
            Title = "Add a song to the movie",
        };
        dialog.FileSelected += path => { Import(path); dialog.QueueFree(); };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    private void OnFilesDropped(string[] files)
    {
        if (files.FirstOrDefault(AudioDecode.Supported) is { } song) Import(song);
    }

    /// <summary>Copies, decodes and analyses <paramref name="path"/> behind a progress bar; it lands at the playhead.</summary>
    public void Import(string path)
    {
        if (_import != null) return;
        _import = SoundImport.Start(path, out var error);
        if (_import == null) { Modal.Inform(this, "Could not add the song", error ?? "Unknown error"); return; }
        _importModal = Modal.Progress(this, "Adding a song", System.IO.Path.GetFileName(path), () => _import = null,
            out var bar, out var status);
        _importBar = bar;
        _importStatus = status;
    }

    private void StepImport()
    {
        var job = _import!;
        bool done = job.Step();
        if (_importBar != null) _importBar.Value = job.Progress;
        if (_importStatus != null && _importStatus.Text != job.Stage) _importStatus.Text = job.Stage;
        if (!done) return;
        _import = null;
        _importModal?.CloseModal();
        var asset = job.Result();
        var clip = MovieSession.Project.AddAudio(MovieSession.Project.AudioLaneFor("Music"), asset, _stage.Time);
        _timeline.Resync();
        _timeline.Fit();
        _timeline.Select(clip.Id);
        ShowHints();
    }

    /// <summary>The world's own sound buses (engines, steps, radios) on or off: the movie's sound is on Master.</summary>
    private static void WorldSound(bool on)
    {
        foreach (var bus in new[] { Audio.SfxBus.Name, Audio.SfxBus.Player, Audio.SfxBus.Music })
            if (AudioServer.GetBusIndex(bus) is var i and >= 0) AudioServer.SetBusMute(i, !on);
        if (on) Audio.SfxBus.ApplyVolumes();
    }

    /// <summary>A marker at the playhead, on the nearest beat within a quarter second; or the one there goes.</summary>
    private void Mark() => _timeline.ToggleMarkerAt(_stage.Time, 0.25, ghostsOnly: false);

    private void ToMarker(int direction)
    {
        var markers = MovieSession.Project.Markers;
        double now = _stage.Time;
        double? to = direction > 0 ? markers.Where(m => m > now + 1e-3).Cast<double?>().FirstOrDefault()
            : markers.Where(m => m < now - 1e-3).Cast<double?>().LastOrDefault();
        if (to is { } t) Seek(t);
    }

    // ---- the camera track (#669) -------------------------------------------------------------------

    /// <summary>A key from the view as it is now, at the playhead; one within a frame is replaced.</summary>
    private void SetKey()
    {
        var at = _origin.ToGlobal(_camera.GlobalPosition);
        var q = _camera.GlobalTransform.Basis.GetRotationQuaternion();
        var key = Picked.Set(new CameraKey
        {
            T = _stage.Time, E = at.E, N = at.N, Alt = at.Alt, Qx = q.X, Qy = q.Y, Qz = q.Z, Qw = q.W, Lens = _camera.Lens,
        }, 1 / Channels.Rate);
        _timeline.SelectKey(key);
        ShowKeyOptions();
    }

    private Vector3? ActorAt(int lane) => _stage.Puppet(lane)?.GlobalPosition;

    /// <summary>
    /// <c>--moviekeys</c>, for screenshots: three camera keys a few seconds apart from views round the
    /// actor (a smooth one aimed at it, a linear one, a cut), the first picked, the timeline zoomed in.
    /// </summary>
    public void ScreenshotKeys()
    {
        double t0 = _stage.Time;
        var p = MovieSession.Project;
        if (p.Cameras.Count < 2) p.AddCamera();
        for (int cam = 0; cam < 2; cam++)
        {
            _timeline.Pick(cam);
            for (int i = 0; i < 3; i++)
            {
                _stage.Seek(Math.Max(0, t0 - 4 + i * 3 + cam));
                _camera.Turn(new Vector2(cam == 0 ? 0.9f : -1.4f, 0));
                if (FocusPoint() is { } actor) _camera.PlaceNear(actor, cam == 0 ? 14f : 40f);
                SetKey();
                var key = _timeline.SelectedKey!;
                key.Ease = (KeyEase)i;
                key.Lens = new[] { 24f, 50f, 85f }[i];
                if (i == 0 || cam == 1) key.LookAt = 0;
            }
        }
        p.CutTo(Math.Max(0, t0 - 5), 0, 0);
        p.CutTo(Math.Max(0, t0 - 2), 1, 0);
        p.CutTo(t0 + 1, 0, 0);
        _timeline.Pick(1);
        _timeline.SelectKey(p.Cameras[1].Keys[0]);
        _stage.Seek(t0);
        _timeline.ZoomBy(3f);
        ShowKeyOptions();
    }

    // ---- several cameras (#675) --------------------------------------------------------------------

    private CameraTrack Picked => MovieSession.Project.Cameras[Math.Clamp(_timeline.PickedCamera, 0, MovieSession.Project.Cameras.Count - 1)];

    private void AddCamera()
    {
        MovieSession.Project.AddCamera();
        RefreshCameras();
        _timeline.Pick(MovieSession.Project.Cameras.Count - 1);
    }

    /// <summary>The movie cuts to the picked camera at the playhead (a cut within a frame changes camera instead).</summary>
    private void CutToPicked()
    {
        var cut = MovieSession.Project.CutTo(_stage.Time, _timeline.PickedCamera, 1 / Channels.Rate);
        _timeline.SelectCut(cut);
    }

    /// <summary>The picker lists the cameras by name, the picked one chosen.</summary>
    private void RefreshCameras()
    {
        var cameras = MovieSession.Project.Cameras;
        _camPicker.Clear();
        for (int i = 0; i < cameras.Count; i++) _camPicker.AddItem($"{i + 1}  {cameras[i].Name}");
        _camPicker.Selected = Math.Clamp(_timeline.PickedCamera, 0, cameras.Count - 1);
    }

    /// <summary>Right-click on a cut: the camera it cuts to, or delete it.</summary>
    private void OpenCutMenu(CameraCut cut, Vector2 at)
    {
        _keyMenu?.QueueFree();
        var menu = new PopupMenu();
        var cameras = MovieSession.Project.Cameras;
        for (int i = 0; i < cameras.Count; i++)
        {
            menu.AddRadioCheckItem($"Cut to {cameras[i].Name}", i);
            menu.SetItemChecked(i, cut.Camera == i);
        }
        menu.AddSeparator();
        menu.AddItem("Delete cut", 999);
        menu.IdPressed += id =>
        {
            if (id == 999) { MovieSession.Project.RemoveCut(cut); _timeline.SelectCut(null); }
            else cut.Camera = (int)id;
        };
        AddChild(menu);
        _keyMenu = menu;
        menu.Popup(new Rect2I((Vector2I)at, Vector2I.Zero));
    }

    /// <summary>Right-click on a camera's name: rename or delete it.</summary>
    private void OpenCameraMenu(int index, Vector2 at)
    {
        _keyMenu?.QueueFree();
        var menu = new PopupMenu();
        menu.AddItem("Rename…", 0);
        menu.AddItem("Delete camera", 1);
        menu.SetItemDisabled(1, MovieSession.Project.Cameras.Count <= 1);
        menu.IdPressed += id =>
        {
            var cam = MovieSession.Project.Cameras[index];
            if (id == 0)
                Modal.Prompt(this, "Rename camera", null, cam.Name, "Name",
                    name => name.Trim().Length == 0 ? "Give it a name" : null, name => { cam.Name = name.Trim(); RefreshCameras(); });
            else
                Modal.Confirm(this, $"Delete {cam.Name}?", "Its keys and the cuts to it go too.", "Delete", () =>
                {
                    MovieSession.Project.RemoveCamera(index);
                    _gizmos.Reset();
                    _timeline.SelectKey(null);
                    _timeline.SelectCut(null);
                    _timeline.Pick(Math.Min(index, MovieSession.Project.Cameras.Count - 1));
                    RefreshCameras();
                }, danger: true);
        };
        AddChild(menu);
        _keyMenu = menu;
        menu.Popup(new Rect2I((Vector2I)at, Vector2I.Zero));
    }

    private void DeleteKey(CameraKey key)
    {
        foreach (var cam in MovieSession.Project.Cameras) cam.Remove(key);
        _timeline.SelectKey(null);
    }

    /// <summary>The view as the camera track has it at the playhead, aimed at an actor when the key says so.</summary>
    private void ShowThroughCamera()
    {
        var p = MovieSession.Project;
        // the program shows the sequence's cuts with their transitions (#776); "picked camera" the one being worked on
        float fade = 0;
        if (_viewSource.Selected == 0)
        {
            var view = p.Program(_stage.Time, _stage.Mount);
            if (view.Ok) _camera.ShowPose(view.Pose, ActorAt);
            fade = view.Fade;
        }
        else if (p.ShotPose(_timeline.PickedCamera, _stage.Time, _stage.Mount, out var pose, out fade)) _camera.ShowPose(pose, ActorAt);
        SetFade(fade);
    }

    /// <summary>The preview player's black, for fades (#776); only while looking through.</summary>
    private void SetFade(float alpha)
    {
        if (!Mathf.IsEqualApprox(_fade.Color.A, alpha)) _fade.Color = new Color(0, 0, 0, alpha);
        bool on = alpha > 0.001f;
        if (_fade.Visible != on) _fade.Visible = on;
    }

    /// <summary>The key strip shows the selected key's options; with none, the lens is the view's own.</summary>
    private void ShowKeyOptions()
    {
        var key = _timeline.SelectedKey;
        _ease.Disabled = _aim.Disabled = _deleteKey.Disabled = key == null;
        if (key != null)
        {
            _ease.Selected = (int)key.Ease;
            FillAim();
            _aim.Selected = Math.Clamp(key.LookAt + 1, 0, _aim.ItemCount - 1);
        }
        _lens.SetValueNoSignal(key?.Lens ?? _camera.Lens);
        _lensLabel.Text = $"{_lens.Value:F0} mm";
    }

    /// <summary>"Free look" and one entry per actor lane, for the aim.</summary>
    private void FillAim()
    {
        var lanes = MovieSession.Project.Lanes;
        _aimLanes = lanes.Count;
        int keep = _aim.Selected;
        _aim.Clear();
        _aim.AddItem("Free look");
        foreach (var lane in lanes) _aim.AddItem($"Look at {lane.Label}");
        _aim.Selected = Math.Clamp(keep, 0, _aim.ItemCount - 1);
    }

    private void OnLens(double mm)
    {
        _lensLabel.Text = $"{mm:F0} mm";
        if (_timeline.SelectedKey is { } key) key.Lens = (float)mm;
        else _camera.Lens = (float)mm;
    }

    /// <summary>Right-click on a key: its motion, its aim, delete, in a menu where the pointer is.</summary>
    private void OpenKeyMenu(CameraKey key, Vector2 at)
    {
        _keyMenu?.QueueFree();
        var menu = new PopupMenu();
        menu.AddRadioCheckItem("Smooth", 0);
        menu.AddRadioCheckItem("Linear", 1);
        menu.AddRadioCheckItem("Cut", 2);
        menu.SetItemChecked((int)key.Ease, true);
        menu.AddSeparator();
        menu.AddRadioCheckItem("Free look", 10);
        menu.SetItemChecked(menu.ItemCount - 1, key.LookAt < 0);
        var lanes = MovieSession.Project.Lanes;
        for (int i = 0; i < lanes.Count; i++)
        {
            menu.AddRadioCheckItem($"Look at {lanes[i].Label}", 11 + i);
            menu.SetItemChecked(menu.ItemCount - 1, key.LookAt == i);
        }
        menu.AddSeparator();
        menu.AddItem("Set from the view", 98);
        menu.AddItem("Delete key", 99);
        menu.IdPressed += id =>
        {
            if (id <= 2) key.Ease = (KeyEase)(int)id;
            else if (id == 10) key.LookAt = -1;
            else if (id >= 11 && id < 98) key.LookAt = (int)id - 11;
            else if (id == 98) { _stage.Seek(key.T); SetKey(); }
            else if (id == 99) DeleteKey(key);
            ShowKeyOptions();
        };
        AddChild(menu);
        _keyMenu = menu;
        menu.Popup(new Rect2I((Vector2I)at, Vector2I.Zero));
    }

    private void SyncScroll()
    {
        _syncingScroll = true;
        _scroll.MaxValue = Math.Max(_stage.Duration + 5, _timeline.VisibleSeconds);
        _scroll.Page = _timeline.VisibleSeconds;
        _scroll.Value = _timeline.Left;
        _syncingScroll = false;
    }

    /// <summary>The stage and timeline on the session's project again: after New, Open or Save.</summary>
    private void Reload()
    {
        _stage.Use(MovieSession.Project);
        _focusLane = -1;
        _timeline.Select(0);
        _timeline.Fit();
        _timeline.Resync();
        _timeline.SelectKey(null);
        _shownTime = -1;
        _aimLanes = -1;
        _gizmos.Reset();
        _timeline.Pick(0);
        RefreshCameras();
        ShowHints();
    }
}
