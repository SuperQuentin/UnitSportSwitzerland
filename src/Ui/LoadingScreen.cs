using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// Full-screen loading view between picking a mode and playing: the real stage in big type, a
/// bar that only ever moves forward, a detail line ("142 / 361 tiles"), Cancel — and under it,
/// in a dimmer italic, a rotating line of imaginary Swiss things being loaded. The jokes never
/// stand in for the real status; they keep the wait company.
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
    public event Action? CancelRequested;

    private Control _root = null!;
    private Label _stage = null!, _detail = null!, _joke = null!, _where = null!;
    private ProgressBar _bar = null!;
    private Button _cancel = null!;
    private float _shown;            // what the bar shows, eased toward the target
    private float _target;
    private double _nextJokeAt;
    private int _jokeIndex;
    private readonly int[] _order;
    private Tween? _jokeFade;

    public bool IsOpen => _root.Visible;

    public LoadingScreen()
    {
        Name = "LoadingScreen";
        Layer = 50;
        var rng = new Random();
        _order = Enumerable.Range(0, LoadingPhrases.All.Length).OrderBy(_ => rng.Next()).ToArray();
    }

    public override void _Ready()
    {
        _root = new Control { Theme = UiTheme.Get(), Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // a deep blue-black night gradient: the world builds behind it, unseen
        var bg = new TextureRect
        {
            Texture = new GradientTexture2D
            {
                Gradient = new Gradient
                {
                    Colors = new[] { new Color(0.035f, 0.045f, 0.07f), new Color(0.07f, 0.085f, 0.12f), new Color(0.03f, 0.035f, 0.05f) },
                    Offsets = new[] { 0f, 0.55f, 1f },
                },
                FillFrom = new Vector2(0, 0), FillTo = new Vector2(0.4f, 1),
                Width = 64, Height = 64,
            },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(bg);

        // the content sits low and left, like a film's title card
        var column = UiKit.VBox(10);
        column.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        column.OffsetLeft = 72; column.OffsetRight = -72; column.OffsetTop = -250; column.OffsetBottom = -64;
        column.GrowVertical = Control.GrowDirection.Begin;
        _root.AddChild(column);

        _where = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber);
        _where.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.Bold, SpacingGlyph = 2 });
        column.AddChild(_where);
        _stage = UiKit.Text("Loading", 34, UiTheme.Text, bold: true);
        column.AddChild(_stage);

        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 5) };
        column.AddChild(UiKit.Spacer(4));
        column.AddChild(_bar);

        var under = UiKit.HBox(12);
        _detail = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _detail.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        under.AddChild(_detail);
        _cancel = UiKit.Button("Cancel", minWidth: 110);
        _cancel.Pressed += () => CancelRequested?.Invoke();
        under.AddChild(_cancel);
        column.AddChild(under);

        _joke = UiKit.Text("", UiTheme.FontBody, UiTheme.TextFaint);
        _joke.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.Font, VariationTransform = new Transform2D(1, 0, 0.18f, 1, 0, 0) });
        column.AddChild(_joke);
    }

    /// <summary>Shows the screen for a session about to be built.</summary>
    public void Begin(string where)
    {
        _where.Text = where.ToUpperInvariant();
        _shown = _target = 0;
        _bar.Value = 0;
        _stage.Text = "Getting ready";
        _detail.Text = "";
        _cancel.Disabled = false;
        NextJoke(instant: true);
        _root.Visible = true;
        _root.Modulate = Colors.White;
        _cancel.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>The real progress: stage text, overall fraction (never goes backwards), detail.</summary>
    public void Report(string stage, float fraction, string detail)
    {
        _stage.Text = stage;
        _target = Mathf.Max(_target, Mathf.Clamp(fraction, 0, 1));
        _detail.Text = detail;
    }

    /// <summary>Fades out once the world is ready.</summary>
    public void Finish()
    {
        _target = 1;
        _cancel.Disabled = true;
        var tw = CreateTween();
        tw.TweenInterval(0.25f);
        tw.TweenProperty(_root, "modulate:a", 0f, 0.45f);
        tw.TweenCallback(Callable.From(() => _root.Visible = false));
    }

    public void Close() => _root.Visible = false;

    public override void _Process(double delta)
    {
        if (!_root.Visible) return;
        _shown = Mathf.Lerp(_shown, _target, MathX.Damp(6f, (float)delta));
        _bar.Value = _shown;
        // wall clock: the loading threads set the pace, and GameClock.Pace is holding the frames
        if (Core.RealClock.Now >= _nextJokeAt) NextJoke(instant: false);
    }

    private void NextJoke(bool instant)
    {
        _nextJokeAt = Core.RealClock.Now + 2.6;
        string text = LoadingPhrases.All[_order[_jokeIndex++ % _order.Length]] + "…";
        _jokeFade?.Kill();
        if (instant) { _joke.Text = text; _joke.Modulate = Colors.White; return; }
        _jokeFade = CreateTween();
        _jokeFade.TweenProperty(_joke, "modulate:a", 0f, 0.25f);
        _jokeFade.TweenCallback(Callable.From(() => _joke.Text = text));
        _jokeFade.TweenProperty(_joke, "modulate:a", 1f, 0.35f);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_root.Visible || _cancel.Disabled || !e.IsPressed() || e.IsEcho()) return;
        if (!e.IsActionPressed(PlayerInput.Menu) && !e.IsActionPressed("ui_cancel")) return;
        GetViewport().SetInputAsHandled();
        CancelRequested?.Invoke();
    }
}

/// <summary>Imaginary things being loaded, shown under the real ones while you wait.</summary>
public static class LoadingPhrases
{
    public static readonly string[] All =
    {
        "Calibrating cowbells",
        "Polishing the Matterhorn",
        "Counting the holes in the Emmental",
        "Synchronising cuckoo clocks",
        "Folding the Swiss Army knife",
        "Negotiating with the Gotthard tunnel",
        "Waxing the funiculars",
        "Herding marmots",
        "Tempering the chocolate",
        "Ironing the lakes flat",
        "Reticulating Alpine splines",
        "Stirring the fondue",
        "Teaching the yodel to echo",
        "Inflating the Toblerone peaks",
        "Neutralising",
        "Brushing the St. Bernards",
        "Making the trains early, just in case",
        "Sorting the rösti by crispiness",
        "Untangling the hairpin bends",
        "Stacking the firewood very neatly",
        "Asking the cows to face the camera",
        "Grating the raclette",
        "Measuring the Rhine to the millimetre",
        "Translating into four national languages",
        "Signing the hiking trails in yellow",
        "Rounding the Rösti divide",
        "Filling the glaciers back up",
        "Counting the sheep. Then the goats",
        "Putting the snow on the right side of the peaks",
        "Bribing the weather on the Jungfrau",
        "Dusting the Ricola herbs",
        "Aligning the chalets' window boxes",
    };
}
