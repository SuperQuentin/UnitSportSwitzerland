using Godot;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// The first-run tutorial (#517): a card in the top-left corner that walks a new player through
/// the game one thing at a time (look, walk, run and jump, the travel menu, the camera, the map,
/// the fly camera, F1). Each step ends when the player has <i>done</i> it, read from the world
/// rather than from a button on the card, so it takes no input of its own and needs no new
/// binding on any device. The steps and their rules are <see cref="TutorialSteps"/>.
///
/// <para>
/// Started by <see cref="GameShell"/> once the loading screen is gone, the first time a world is
/// entered from the menus; never in a command-line run (probes, screenshots) unless
/// <c>--tutorial</c>. Done or skipped (the pause menu) is saved as
/// <see cref="GameSettings.TutorialDone"/>; Settings › Gameplay plays it again.
/// </para>
/// </summary>
public partial class Tutorial : CanvasLayer
{
    /// <summary>The tutorial running now, for the pause menu's Skip entry.</summary>
    public static Tutorial? Current { get; private set; }

    private readonly Func<FootPlayer?> _walker;
    private readonly Func<bool> _flying, _mapOpen, _covered;
    private readonly IReadOnlyList<TutorialStep> _steps;

    private int _index;
    private TutorialSignals _signals;
    private double _doneFor = -1;   // >= 0: the step is met, shown ticked this long before the next
    private Vector3 _lastForward, _lastPos;
    private bool _hasForward, _hasPos;

    private PanelContainer _panel = null!;
    private Label _count = null!, _title = null!, _body = null!, _foot = null!;
    private ProgressBar _bar = null!;
    private float _shownProgress = -1;

    /// <param name="walker">The local player while on foot or mounted; null in the fly camera.</param>
    /// <param name="flying">The fly camera is the view.</param>
    /// <param name="mapOpen">The place search (M) is open.</param>
    /// <param name="covered">Something owns the screen (a menu, the travel menu, the map, a replay): the card hides and waits.</param>
    public Tutorial(Func<FootPlayer?> walker, Func<bool> flying, Func<bool> mapOpen, Func<bool> covered)
    {
        Name = "Tutorial";
        _walker = walker;
        _flying = flying;
        _mapOpen = mapOpen;
        _covered = covered;
        _steps = TutorialSteps.For(Permissions.CanSpawnVehicles, Permissions.InMatch);
    }

    /// <summary>Shown the first time a world is entered from the menus, or with <c>--tutorial</c>.</summary>
    public static bool Wanted(bool fromMenus) =>
        CmdArgs.Has("--tutorial") || (fromMenus && !GameSettings.Current.TutorialDone && !CmdArgs.Has("--autostart"));

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        PlayerInput.DeviceChanged -= ShowStep;
    }

    public override void _Ready()
    {
        Layer = 11;   // the prompt bar's: over the feel HUD, under the inventory and the menus

        _panel = new PanelContainer { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 14));
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _panel.OffsetLeft = 18; _panel.OffsetTop = 18;
        _panel.CustomMinimumSize = new Vector2(400, 0);
        AddChild(_panel);

        var column = UiKit.VBox(6);
        column.MouseFilter = Control.MouseFilterEnum.Ignore;
        _panel.AddChild(column);

        var head = UiKit.HBox(10);
        head.MouseFilter = Control.MouseFilterEnum.Ignore;
        _title = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _count = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber);
        head.AddChild(_title);
        head.AddChild(_count);
        column.AddChild(head);

        _body = UiKit.Text("", UiTheme.FontBody, UiTheme.Text, wrap: true);
        _body.CustomMinimumSize = new Vector2(372, 0);
        column.AddChild(_body);

        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        _bar.AddThemeStyleboxOverride("background", UiTheme.Flat(new Color(1, 1, 1, 0.08f), 2, 0, 0));
        _bar.AddThemeStyleboxOverride("fill", UiTheme.Flat(UiTheme.Amber, 2, 0, 0));
        column.AddChild(_bar);

        _foot = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint);
        column.AddChild(_foot);

        PlayerInput.DeviceChanged += ShowStep;
        ShowStep();
        GD.Print($"[tutorial] started: {string.Join(", ", _steps.Select(s => s.Goal))}");
    }

    /// <summary>The pause menu's Skip tutorial: done for good.</summary>
    public void Skip()
    {
        GD.Print($"[tutorial] skipped at {_steps[_index].Goal}");
        Finish();
    }

    public override void _Process(double delta)
    {
        bool covered = _covered();
        _panel.Visible = !covered;
        // the map step is met by opening the map, which covers the card
        if (_mapOpen()) _signals.MapOpened = true;
        if (covered) { _hasForward = false; return; }

        if (_doneFor >= 0)
        {
            _doneFor += delta;
            if (_doneFor >= 0.9) Next();
            return;
        }

        Observe(delta);
        var goal = _steps[_index].Goal;
        SetProgress(TutorialSteps.Progress(goal, _signals));
        if (!TutorialSteps.Met(goal, _signals)) return;

        // ticked: green bar and title for a moment, then the next card
        _doneFor = 0;
        SetProgress(1);
        _bar.AddThemeStyleboxOverride("fill", UiTheme.Flat(UiTheme.Good, 2, 0, 0));
        _title.AddThemeColorOverride("font_color", UiTheme.Good);
        GD.Print($"[tutorial] {goal} done after {_signals.Seconds:F1} s (looked {_signals.LookedDegrees:F0}°, walked {_signals.WalkedMeters:F1} m, ran {_signals.Ran}, jumped {_signals.Jumped})");
    }

    /// <summary>What the player did this frame, added to the step's <see cref="TutorialSignals"/>.</summary>
    private void Observe(double delta)
    {
        _signals.Seconds += (float)delta;

        // looking: the angle the view turned, whatever turned it (mouse, stick, head); a jump of
        // more than 30° in one frame is a camera switch, not looking
        if (GetViewport().GetCamera3D() is { } camera)
        {
            var forward = -camera.GlobalBasis.Z;
            if (_hasForward)
            {
                float turned = Mathf.RadToDeg(_lastForward.AngleTo(forward));
                if (turned < 30) _signals.LookedDegrees += turned;
            }
            _lastForward = forward;
            _hasForward = true;
        }

        var walker = _walker();
        if (walker != null && IsInstanceValid(walker))
        {
            if (walker.Vehicle != null) _signals.Rode = true;
            // walked: horizontal distance on foot; more than 3 m in a frame is a teleport or an origin shift
            var pos = walker.GlobalPosition;
            if (_hasPos && walker.Vehicle == null)
            {
                float step = MathX.FlatDistance(pos, _lastPos);
                if (step < 3) _signals.WalkedMeters += step;
            }
            _lastPos = pos;
            _hasPos = true;
        }
        else _hasPos = false;

        if (_flying()) _signals.Flew = true;

        if (UiFocus.TextEntryActive) return;   // typing "m" in the chat is not opening the map
        if (Input.IsActionJustPressed(PlayerInput.Jump)) _signals.Jumped = true;
        if (Input.IsActionJustPressed(PlayerInput.Sprint)) _signals.Ran = true;
        if (Input.IsActionJustPressed(PlayerInput.CameraToggle)) _signals.CameraToggled = true;
        if (Input.IsActionJustPressed(PlayerInput.Help)) _signals.HelpOpened = true;
    }

    private void SetProgress(float p)
    {
        // the bar only on change (perf-no-per-frame-allocations: UI only when it changes)
        p = MathF.Round(p, 2);
        if (p == _shownProgress) return;
        _shownProgress = p;
        _bar.Value = p;
    }

    private void Next()
    {
        _doneFor = -1;
        _signals = default;
        _hasForward = _hasPos = false;
        if (++_index >= _steps.Count) { Finish(); return; }
        _bar.AddThemeStyleboxOverride("fill", UiTheme.Flat(UiTheme.Amber, 2, 0, 0));
        _title.AddThemeColorOverride("font_color", UiTheme.Text);
        ShowStep();
        // the new card slides in a little, so the change is seen out of the corner of an eye
        _panel.Modulate = new Color(1, 1, 1, 0);
        var tw = _panel.CreateTween().SetIgnoreTimeScale(true);
        tw.TweenProperty(_panel, "modulate:a", 1f, 0.25f);
    }

    /// <summary>The current step's words, for the device in hand (again when it changes).</summary>
    private void ShowStep()
    {
        if (_index >= _steps.Count) return;
        var step = _steps[_index];
        _title.Text = step.Title;
        _count.Text = $"{_index + 1} / {_steps.Count}";
        _body.Text = InputHints.Format(TutorialSteps.Body(step, InputHints.Pad, InputHints.Vr));
        _foot.Text = step.Goal == TutorialGoal.Done ? "" : InputHints.Format("Tutorial  ·  {menu} › Skip tutorial");
        _shownProgress = -1;
    }

    private void Finish()
    {
        if (Current == this) Current = null;
        GameSettings.Current.TutorialDone = true;
        // only this one value: a command-line --rings must not become the saved choice
        GameSettings.SaveOnly(nameof(GameSettings.TutorialDone), true);
        SetProcess(false);
        var tw = _panel.CreateTween().SetIgnoreTimeScale(true);
        tw.TweenProperty(_panel, "modulate:a", 0f, 0.4f);
        tw.TweenCallback(Callable.From(QueueFree));
        GD.Print("[tutorial] finished");
    }
}
