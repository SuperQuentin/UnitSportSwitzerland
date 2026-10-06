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
    private bool _shownPlaying;

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
        ? "Space play  ·  J / L play back / forward  ·  ← → frame (Shift 1 s)  ·  S split  ·  Del delete  ·  Ctrl+D duplicate  ·  Tab next actor  ·  right-drag orbit, wheel zoom"
        : "Y play  ·  LT / RT shuttle  ·  X split  ·  D-pad on the timeline: frame  ·  right stick orbit, LB / RB zoom  ·  B back";

    public override void _Ready()
    {
        _chunks = _world.Chunks;
        var origin = _chunks?.Origin ?? _world.StudioPlayer?.Origin ?? new WorldOrigin(2_600_000, 1_200_000);
        _stage = new MovieStage(MovieSession.Project, origin);
        _world.AddChild(_stage);
        _camera = new StudioCamera { Target = FocusPoint };
        _world.AddChild(_camera);
        _chunks?.AddAnchor(_camera);
        Freeze();

        // the world shows through: a viewport catcher for orbit and zoom, then the panel along the bottom
        var view = new Control { MouseFilter = MouseFilterEnum.Stop, FocusMode = FocusModeEnum.None };
        view.SetAnchorsPreset(LayoutPreset.FullRect);
        view.GuiInput += OnViewInput;
        AddChild(view);

        var top = UiKit.VBox(2);
        top.SetAnchorsPreset(LayoutPreset.TopLeft);
        top.Position = new Vector2(28, 22);
        top.MouseFilter = MouseFilterEnum.Ignore;
        _title = UiKit.Text("", 24, Colors.White, bold: true);
        top.AddChild(_title);
        _hint = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextDim);
        top.AddChild(_hint);
        AddChild(top);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.86f, 14, 14));
        panel.SetAnchorsPreset(LayoutPreset.BottomWide);
        panel.GrowVertical = GrowDirection.Begin;
        panel.OffsetLeft = 16; panel.OffsetRight = -16; panel.OffsetBottom = -16;
        AddChild(panel);
        var column = UiKit.VBox(8);
        panel.AddChild(column);

        var transport = UiKit.HBox(6);
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
        transport.AddChild(UiKit.Spacer(expand: true));
        Tool(transport, "Split", "Cut the selected clip at the playhead (S)", Split);
        Tool(transport, "Duplicate", "A copy after it (Ctrl+D)", Duplicate);
        Tool(transport, "Delete", "Remove the selected clip (Del)", Delete);
        transport.AddChild(UiKit.Spacer(w: 12));
        Tool(transport, "New", "Start an empty movie", NewMovie);
        Tool(transport, "Open", "Open a saved movie", OpenMovie);
        Tool(transport, "Save", "Save this movie", SaveMovie);
        Tool(transport, "Close", "Back to the game (Esc)", () => Shell.Back());

        _timeline = new TimelineView(_stage);
        _timeline.SelectionChanged += () => { FocusSelected(); Frame(); };
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 150), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_timeline);
        column.AddChild(scroll);

        PlayerInput.DeviceChanged += ShowHints;
        ShowHints();
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
        if (IsInstanceValid(_camera))
        {
            _chunks?.RemoveAnchor(_camera);
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
        float shuttle = Input.GetJoyAxis(0, JoyAxis.TriggerRight) - Input.GetJoyAxis(0, JoyAxis.TriggerLeft);
        if (Math.Abs(shuttle) > 0.15f) { _stage.Playing = false; _stage.Seek(_stage.Time + shuttle * 2 * delta); }

        if (_stage.Time != _shownTime || _stage.Playing != _shownPlaying)
        {
            _shownTime = _stage.Time;
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
        if (MovieSession.Project.Find(_timeline.Selected) is { } clip) _focusLane = clip.Lane;
    }

    // ---- input -----------------------------------------------------------------------------------

    private void OnViewInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion m when (m.ButtonMask & (MouseButtonMask.Right | MouseButtonMask.Middle)) != 0:
                _camera.Orbit(m.Relative * 0.006f);
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
                case Key.Left: Step(k.ShiftPressed ? -1 : -1 / Channels.Rate); break;
                case Key.Right: Step(k.ShiftPressed ? 1 : 1 / Channels.Rate); break;
                case Key.Home: Seek(0); break;
                case Key.End: Seek(_stage.Duration); break;
                case Key.S when !k.CtrlPressed: Split(); break;
                case Key.Delete: Delete(); break;
                case Key.D when k.CtrlPressed: Duplicate(); break;
                case Key.Tab: NextActor(); break;
                default: handled = false; break;
            }
            if (handled) GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventJoypadButton { Pressed: true } b)
        {
            if (b.ButtonIndex == JoyButton.Y) { Play(1); GetViewport().SetInputAsHandled(); }
            else if (b.ButtonIndex == JoyButton.X) { Split(); GetViewport().SetInputAsHandled(); }
        }
    }

    // ---- actions ---------------------------------------------------------------------------------

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

    private void Split()
    {
        if (MovieSession.Project.Split(_timeline.Selected, _stage.Time) is { } right) _timeline.Select(right.Id);
        _stage.Apply();
    }

    private void Duplicate()
    {
        if (MovieSession.Project.Duplicate(_timeline.Selected) is { } copy) _timeline.Select(copy.Id);
        _stage.Apply();
    }

    private void Delete()
    {
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

    /// <summary>The stage and timeline on the session's project again: after New, Open or Save.</summary>
    private void Reload()
    {
        _stage.Use(MovieSession.Project);
        _focusLane = -1;
        _timeline.Select(0);
        _timeline.Fit();
        _shownTime = -1;
        ShowHints();
    }
}
