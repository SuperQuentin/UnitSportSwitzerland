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
    private PopupMenu? _keyMenu;

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

    private static string Hints(InputDevice device) => device == InputDevice.KeyboardMouse
        ? "Space play  ·  J / L back / forward  ·  ← → frame (Shift 1 s)  ·  S split, Shift+S cut all, Ctrl+click a clip: blade  ·  Del delete  ·  Ctrl+D duplicate  ·  "
          + "M marker, Ctrl+← → between markers, double-click a beat  ·  wheel on the timeline zooms (Shift scrolls), = / − too  ·  "
          + "right mouse + WASD QE fly, Tab orbit an actor  ·  I camera key, V look through, right-click a key: options  ·  drop a song on the window"
        : "Y play  ·  LT / RT shuttle  ·  X split / cut all  ·  R3 marker on the beat  ·  View: camera key  ·  right stick turn, LB / RB zoom or dolly (on the timeline: zoom it)  ·  D-pad on the timeline: frame  ·  B back";

    public override void _Ready()
    {
        _chunks = _world.Chunks;
        var origin = _chunks?.Origin ?? _world.StudioPlayer?.Origin ?? new WorldOrigin(2_600_000, 1_200_000);
        _stage = new MovieStage(MovieSession.Project, origin);
        _world.AddChild(_stage);
        _origin = origin;
        _camera = new StudioCamera(origin) { Target = FocusPoint, PadBusy = () => _timeline?.HasFocus() == true };
        _camera.TookOver += () => _lookThrough.SetPressedNoSignal(false);
        _world.AddChild(_camera);
        _chunks?.AddAnchor(_camera);
        Freeze();

        // the world shows through: a viewport catcher for orbit and zoom, then the panel along the bottom
        var view = new Control { MouseFilter = MouseFilterEnum.Stop, FocusMode = FocusModeEnum.None };
        view.SetAnchorsPreset(LayoutPreset.FullRect);
        view.GuiInput += OnViewInput;
        AddChild(view);

        var top = UiKit.VBox(2);
        top.SetAnchorsPreset(LayoutPreset.TopWide);
        top.OffsetLeft = 28; top.OffsetTop = 22; top.OffsetRight = -380;
        top.MouseFilter = MouseFilterEnum.Ignore;
        _title = UiKit.Text("", 24, Colors.White, bold: true);
        top.AddChild(_title);
        _hint = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextDim);
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        top.AddChild(_hint);
        AddChild(top);

        // the movie as a whole, top right: out of the way of the transport and the edits below
        var filePanel = new PanelContainer();
        filePanel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.86f, 12, 8));
        filePanel.SetAnchorsPreset(LayoutPreset.TopRight);
        filePanel.GrowHorizontal = GrowDirection.Begin;
        filePanel.OffsetRight = -16; filePanel.OffsetTop = 16;
        var file = UiKit.HBox(6);
        filePanel.AddChild(file);
        Tool(file, "New", "Start an empty movie", NewMovie);
        Tool(file, "Open", "Open a saved movie", OpenMovie);
        Tool(file, "Save", "Save this movie", SaveMovie);
        Tool(file, "Close", "Back to the game (Esc)", () => Shell.Back());
        AddChild(filePanel);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.86f, 14, 14));
        panel.SetAnchorsPreset(LayoutPreset.BottomWide);
        panel.GrowVertical = GrowDirection.Begin;
        panel.OffsetLeft = 16; panel.OffsetRight = -16; panel.OffsetBottom = -16;
        AddChild(panel);
        var column = UiKit.VBox(8);
        panel.AddChild(column);

        // rows that wrap rather than run off a narrow (or big-UI) screen
        var transport = Row();
        column.AddChild(transport);
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
        transport.AddChild(_time);
        transport.AddChild(UiKit.Spacer(w: 10));
        transport.AddChild(UiKit.Text("Zoom", UiTheme.FontSmall, UiTheme.TextDim));
        Tool(transport, "−", "Zoom out (− , the wheel on the timeline)", () => _timeline.ZoomBy(0.66f));
        Tool(transport, "Fit", "The whole movie in view", () => _timeline.Fit());
        Tool(transport, "+", "Zoom in (= , the wheel on the timeline)", () => _timeline.ZoomBy(1.5f));

        var edits = Row();
        column.AddChild(edits);
        Tool(edits, "Split", "Cut the selected clip at the playhead, or every lane if it is not under it (S)", Split);
        Tool(edits, "Cut all", "Cut every clip under the playhead (Shift+S); Ctrl+click a clip cuts it there", CutAll);
        Tool(edits, "Duplicate", "A copy after it (Ctrl+D)", Duplicate);
        Tool(edits, "Delete", "Remove the selected clip or camera key (Del)", Delete);
        edits.AddChild(UiKit.Spacer(w: 10));
        Tool(edits, "Music…", "Add a song (.wav, .ogg, .mp3) at the playhead; or drop it on the window", PickMusic);
        _worldSound = new CheckButton { Text = "World sound", ButtonPressed = true, TooltipText = "The puppets' live engines and steps; off when the recorded game sound plays instead" };
        _worldSound.Toggled += WorldSound;
        edits.AddChild(_worldSound);
        edits.AddChild(UiKit.Spacer(w: 10));
        // the camera track: a key from the view, the view through the track, and the key's options
        Tool(edits, "◆ Key", "A camera key from this view at the playhead (I; pad View)", SetKey);
        _lookThrough = new CheckButton { Text = "Look through camera", TooltipText = "Play the movie through the camera track (V); moving the view takes it over" };
        edits.AddChild(_lookThrough);
        _ease = new OptionButton { TooltipText = "How the camera goes on to the next key" };
        foreach (var name in new[] { "Smooth", "Linear", "Cut" }) _ease.AddItem(name);
        _ease.ItemSelected += i => { if (_timeline.SelectedKey is { } k) k.Ease = (KeyEase)(int)i; };
        edits.AddChild(_ease);
        _aim = new OptionButton { TooltipText = "Keep the camera aimed at an actor from this key" };
        _aim.ItemSelected += i => { if (_timeline.SelectedKey is { } k) k.LookAt = (int)i - 1; };
        edits.AddChild(_aim);
        _lens = new HSlider { MinValue = CameraTrack.MinLens, MaxValue = CameraTrack.MaxLens, Step = 1, Value = 24, CustomMinimumSize = new Vector2(110, 0), TooltipText = "Lens, mm (the key's, or the view's)" };
        _lens.ValueChanged += OnLens;
        edits.AddChild(_lens);
        _lensLabel = UiKit.Text("24 mm", UiTheme.FontSmall, UiTheme.Text);
        _lensLabel.CustomMinimumSize = new Vector2(52, 0);
        edits.AddChild(_lensLabel);
        _deleteKey = Tool(edits, "✕", "Delete this camera key", () => { if (_timeline.SelectedKey is { } k) DeleteKey(k); });

        _timeline = new TimelineView(_stage);
        _timeline.SelectionChanged += () => { FocusSelected(); Frame(); };
        _timeline.KeySelectionChanged += ShowKeyOptions;
        _timeline.KeyMenuRequested += OpenKeyMenu;
        _timeline.ViewChanged += SyncScroll;
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 170), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_timeline);
        column.AddChild(scroll);
        _scroll = new HScrollBar { Step = 0, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _scroll.ValueChanged += v => { if (!_syncingScroll) _timeline.Left = v; };
        column.AddChild(_scroll);
        ShowKeyOptions();

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
            Frame();
        }).CallDeferred();
    }

    public override void OnShown() => _play.CallDeferred(Control.MethodName.GrabFocus);

    public override void _ExitTree()
    {
        PlayerInput.DeviceChanged -= ShowHints;
        GetWindow().FilesDropped -= OnFilesDropped;
        Audio.SfxBus.ApplyVolumes();   // the world's buses as the settings have them again
        if (IsInstanceValid(_camera))
        {
            // the world may be on its way out with the studio still open (leaving to the menu)
            if (_chunks != null && IsInstanceValid(_chunks)) _chunks.RemoveAnchor(_camera);
            _camera.QueueFree();
        }
        if (IsInstanceValid(_stage)) _stage.QueueFree();
        Thaw();
    }

    // ---- the world while the studio is open ----------------------------------------------------

    /// <summary>The player stops where it is, unseen, and nothing records the studio's puppets as play.</summary>
    private void Freeze()
    {
        _previousCamera = GetViewport().GetCamera3D();
        _camera.MakeCurrent();
        if (ReplayRecorder.Instance is { } recorder) recorder.Paused = true;
        if (_world.StudioPlayer is { } player)
        {
            _frozen = player;
            _frozenMode = player.ProcessMode;
            player.ProcessMode = ProcessModeEnum.Disabled;
            player.Visible = false;
        }
    }

    private void Thaw()
    {
        if (_frozen != null && IsInstanceValid(_frozen))
        {
            _frozen.ProcessMode = _frozenMode;
            _frozen.Visible = true;
        }
        if (_previousCamera != null && IsInstanceValid(_previousCamera)) _previousCamera.MakeCurrent();
        if (ReplayRecorder.Instance is { } recorder) recorder.Paused = false;
    }

    // ---- per frame -----------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        // a pad's triggers shuttle: the harder, the faster
        if (_import != null) StepImport();
        float shuttle = Input.GetActionStrength(Forward) - Input.GetActionStrength(Backward);
        if (_lookThrough.ButtonPressed) ShowThroughCamera();
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
            ? "Nothing recorded yet: play, then press F5 (or Pause > Save clip) to keep the last minutes."
            : Hints(PlayerInput.HintDevice);
    }

    /// <summary>The selected actor's puppet, or the first one on screen.</summary>
    private Vector3? FocusPoint()
    {
        if (_stage.Puppet(_focusLane) is { } focused) return focused.GlobalPosition;
        for (int lane = 0; lane < MovieSession.Project.Lanes.Count; lane++)
            if (_stage.Puppet(lane) is { } any) return any.GlobalPosition;
        return null;
    }

    /// <summary>The view jumps to the focused actor, close for a figure, further back for whatever it rides.</summary>
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
                case Key.D when k.CtrlPressed: Duplicate(); break;
                case Key.Tab: NextActor(); break;
                // flying: W A S D Q E belong to the view, not to the shortcuts above (S, D)
                case Key.W or Key.A or Key.D or Key.Q or Key.E when _camera.Steering: break;
                default: handled = false; break;
            }
            if (handled) GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventJoypadButton { Pressed: true } b)
        {
            if (b.ButtonIndex == JoyButton.Y) { Play(1); GetViewport().SetInputAsHandled(); }
            else if (b.ButtonIndex == JoyButton.X) { Split(); GetViewport().SetInputAsHandled(); }
            else if (b.ButtonIndex == JoyButton.RightStick) { Mark(); GetViewport().SetInputAsHandled(); }
            else if (b.ButtonIndex == JoyButton.Back) { SetKey(); GetViewport().SetInputAsHandled(); }
        }
    }

    // ---- actions ---------------------------------------------------------------------------------

    private static HFlowContainer Row()
    {
        var row = new HFlowContainer();
        row.AddThemeConstantOverride("h_separation", 6);
        row.AddThemeConstantOverride("v_separation", 6);
        return row;
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
        var key = MovieSession.Project.Camera.Set(new CameraKey
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
        for (int i = 0; i < 3; i++)
        {
            _stage.Seek(Math.Max(0, t0 - 4 + i * 3));
            _camera.Turn(new Vector2(0.9f, 0));
            SetKey();
            var key = _timeline.SelectedKey!;
            key.Ease = (KeyEase)i;
            key.Lens = new[] { 24f, 50f, 85f }[i];
            if (i == 0) key.LookAt = 0;
        }
        _timeline.SelectKey(MovieSession.Project.Camera.Keys[0]);
        _stage.Seek(t0);
        _timeline.ZoomBy(3f);
        ShowKeyOptions();
    }

    private void DeleteKey(CameraKey key)
    {
        MovieSession.Project.Camera.Remove(key);
        _timeline.SelectKey(null);
    }

    /// <summary>The view as the camera track has it at the playhead, aimed at an actor when the key says so.</summary>
    private void ShowThroughCamera()
    {
        if (MovieSession.Project.Camera.Sample(_stage.Time, out var pose)) _camera.ShowPose(pose, ActorAt);
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
        ShowHints();
    }
}
