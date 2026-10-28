using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Ui;

namespace UnitSport.Adventure.Together;

/// <summary>
/// The shared pot's panel (#1266 part 5.5, <c>ui/style-guide</c>, <c>ui/ux-guidelines</c>): bottom centre, a HUD glass
/// panel while the local player sits at a pot. The seats with their ingredient chips; the stirring pad (a faint
/// figure-eight guide, the last trace in a fixed 64-point ring, the split meter as a ring with its percentage); the
/// ingredients 1-4; one big action, the next useful thing (add cheese, add your bread, dip); the forfeit's three
/// choices when one's bread is lost; Leave (Esc). Rebuilt on a state change only; the pad redraws only while stirring.
/// Mouse: hold and trace an eight in the pad, a click dips. Pad: the right stick stirs, RB dips, the D-pad and A press
/// the buttons, B leaves. VR uses the pad's way (the controllers replay as a pad).
/// </summary>
public sealed partial class FonduePanel : CanvasLayer
{
    private TogetherService? _svc;
    private PanelContainer _root = null!;
    private Label _title = null!, _dips = null!, _split = null!, _forfeitLabel = null!;
    private HBoxContainer _seats = null!, _forfeit = null!;
    private Button _action = null!, _leave = null!;
    private readonly Button[] _ingredients = new Button[4];
    private readonly Button[] _forfeits = new Button[3];
    private StirPad _pad = null!;
    private readonly StirTracker _tracker = new();
    private int _shownDips = -1;
    private string _seatsKey = "";
    private bool _focused;
    private double _nextSample;
    private Vector2 _stick;

    /// <summary>The panel shows (checks).</summary>
    public bool Open => _root.Visible;
    /// <summary>The big action's text now (checks, screenshots).</summary>
    public string Action => _action.Text;
    /// <summary>Eights this client finished (checks).</summary>
    public int Eights => _tracker.Eights;

    public override void _Ready()
    {
        Name = "FonduePanel";
        Layer = 9;
        _root = new PanelContainer { Theme = UiTheme.Get(), Visible = false, CustomMinimumSize = new Vector2(480, 0) };
        _root.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 10));
        _root.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _root.GrowHorizontal = Control.GrowDirection.Both;
        _root.GrowVertical = Control.GrowDirection.Begin;
        // above the hotbar
        _root.OffsetBottom = -96;
        AddChild(_root);
        var col = UiKit.VBox(6);
        _root.AddChild(col);

        var head = UiKit.HBox(10);
        _title = UiKit.Text("Fondue", UiTheme.FontBody, UiTheme.Text, bold: true);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _dips = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _split = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        head.AddChild(_title);
        head.AddChild(_split);
        head.AddChild(_dips);
        col.AddChild(head);

        _seats = UiKit.HBox(12);
        col.AddChild(_seats);

        var body = UiKit.HBox(10);
        _pad = new StirPad { CustomMinimumSize = new Vector2(200, 140), MouseFilter = Control.MouseFilterEnum.Stop };
        _pad.Stirred += OnPadSample;
        _pad.Clicked += () => _svc?.AskDip();
        body.AddChild(_pad);
        var side = UiKit.VBox(6);
        side.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        string[] names = { "1  Cheese", "2  Bread", "3  Wine", "4  Kirsch" };
        for (int i = 0; i < 4; i++)
        {
            int k = i;
            _ingredients[i] = UiKit.Button(names[i], minWidth: 110);
            _ingredients[i].Pressed += () => Add(k);
            grid.AddChild(_ingredients[i]);
        }
        side.AddChild(grid);
        _action = UiKit.Button("Dip", primary: true);
        _action.CustomMinimumSize = new Vector2(0, 44);
        _action.Pressed += DoAction;
        side.AddChild(_action);
        _leave = UiKit.Button("Leave the table (Esc)");
        _leave.Pressed += () => _svc?.AskLeave();
        side.AddChild(_leave);
        body.AddChild(side);
        col.AddChild(body);

        _forfeit = UiKit.HBox(6);
        _forfeitLabel = UiKit.Text("Your bread is lost! Choose:", UiTheme.FontSmall, UiTheme.Amber, bold: true);
        _forfeit.AddChild(_forfeitLabel);
        string[] f = { "1  A line", "2  Buy a round", "3  Yodel" };
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            _forfeits[i] = UiKit.Button(f[i]);
            _forfeits[i].Pressed += () => _svc?.AskForfeit((Forfeit)k);
            _forfeit.AddChild(_forfeits[i]);
        }
        _forfeit.Visible = false;
        col.AddChild(_forfeit);
    }

    public override void _ExitTree()
    {
        if (_focused) UiFocus.Set(this, false);
    }

    public override void _Process(double delta)
    {
        var s = TogetherService.Instance;
        if (s != _svc)
        {
            if (_svc != null && IsInstanceValid(_svc)) _svc.PotChanged -= Refresh;
            _svc = s;
            if (s != null) s.PotChanged += Refresh;
            Refresh();
        }
        if (_svc?.MyPot is not { } pot) return;
        // the forfeit's countdown: shown while owed, the first choice taken by the server after 20 s
        bool owing = _svc.ForfeitUntil > GameClock.Now;
        if (_forfeit.Visible != owing)
        {
            _forfeit.Visible = owing;
            if (owing) _forfeits[0].GrabFocus();
        }
        // the right stick traces the eight (the pad, and VR's controllers), sampled at 30 Hz
        if (!pot.Raclette && GameClock.Now >= _nextSample)
        {
            _nextSample = GameClock.Now + 1.0 / 30;
            var v = Input.GetVector(PlayerInput.LookLeft, PlayerInput.LookRight, PlayerInput.LookUp, PlayerInput.LookDown);
            if (v.Length() > 0.35f) Sample(v * 0.5f + new Vector2(0.5f, 0.5f));
            else if (_stick.Length() > 0.35f) _tracker.Reset();
            _stick = v;
        }
    }

    public override void _Input(InputEvent e)
    {
        if (_svc?.MyPot is not { } pot || !_root.Visible) return;
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            bool owing = _svc.ForfeitUntil > GameClock.Now;
            int n = k.Keycode switch { Key.Key1 => 1, Key.Key2 => 2, Key.Key3 => 3, Key.Key4 => 4, _ => 0 };
            if (n > 0)
            {
                if (owing && n <= 3) _svc.AskForfeit((Forfeit)(n - 1));
                else Add(n - 1);
                GetViewport().SetInputAsHandled();
            }
            else if (k.Keycode == Key.Escape)
            {
                _svc.AskLeave();
                GetViewport().SetInputAsHandled();
            }
        }
        else if (e.IsActionPressed(PlayerInput.UseItem) && PlayerInput.LastDevice == InputDevice.Gamepad)
        {
            _svc.AskDip();
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.B })
        {
            _svc.AskLeave();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Add(int i)
    {
        if (_svc?.MyPot is not { } pot) return;
        var ing = i switch { 0 => Ingredient.Cheese, 1 => pot.Raclette ? Ingredient.Potato : Ingredient.Bread, 2 => Ingredient.Wine, _ => Ingredient.Kirsch };
        _svc.AskAdd(FondueRules.ItemOf(ing));
    }

    private void DoAction()
    {
        if (_svc?.MyPot is not { } pot) return;
        switch (NextAction(pot))
        {
            case 0: _svc.AskAdd(ItemId.Cheese); break;
            case 1: _svc.AskAdd(pot.Raclette ? ItemId.Potato : ItemId.Bread); break;
            default: _svc.AskDip(); break;
        }
    }

    /// <summary>The next useful thing: 0 add cheese, 1 add bread (potatoes), 2 dip (scrape), 3 stir.</summary>
    private int NextAction(PotView pot)
    {
        long self = _svc!.Self;
        if (!pot.Cooking) return pot.Raclette && pot.Cheese >= FondueRules.CheeseNeeded(pot.Seats.Length) ? 1 : 0;
        if (!pot.Raclette && !pot.HasBread(self)) return 1;
        return 2;
    }

    private void OnPadSample(Vector2 at) => Sample(at);

    private void Sample(Vector2 at)
    {
        _pad.Push(at);
        if (_tracker.Add(at.X, at.Y, GameClock.Now, out float q))
        {
            GD.Print(FormattableString.Invariant($"[fondue] an eight, q {q:F2}"));
            _svc?.AskStir(q);
        }
    }

    /// <summary>Feeds a scripted trace (the checks): the same path as the mouse's samples.</summary>
    public void FeedSample(Vector2 at) => Sample(at);

    private void Refresh()
    {
        var pot = _svc?.MyPot;
        bool show = pot != null;
        if (_root.Visible != show)
        {
            _root.Visible = show;
            if (show != _focused)
            {
                _focused = show;
                UiFocus.Set(this, show);
                if (show) MouseCapture.Mode = Input.MouseModeEnum.Visible;
                if (!show) MouseCapture.Capture();
                if (show) _action.GrabFocus();
            }
            _tracker.Reset();
        }
        if (pot == null) return;
        _title.Text = $"{(pot.Raclette ? "Raclette" : "Fondue")} · {SpotName(pot.Spot)}";
        if (pot.Dips != _shownDips)
        {
            _shownDips = pot.Dips;
            _dips.Text = pot.Cooking ? $"Dips left {pot.Dips}" : $"Cheese {pot.Cheese}/{FondueRules.CheeseNeeded(pot.Seats.Length)}";
        }
        _split.Text = pot.Splitted ? "Split!" : pot.Cooking ? $"Split {Mathf.RoundToInt(pot.Split * 100)} %" : "Not melted";
        _split.AddThemeColorOverride("font_color", pot.Splitted || pot.Split > 0.7f ? UiTheme.Bad : UiTheme.TextDim);
        _pad.Split = pot.Split;
        _pad.Splitted = pot.Splitted;
        _pad.Guide = GameSettings.Current.StirGuide && !pot.Raclette;
        _pad.QueueRedraw();
        _ingredients[1].Text = pot.Raclette ? "2  Potato" : "2  Bread";
        int next = NextAction(pot);
        _action.Text = next switch { 0 => "Add cheese", 1 => pot.Raclette ? "Add potatoes" : "Add your bread", _ => pot.Raclette ? "Scrape (click)" : "Dip (click)" };
        _forfeits[1].Text = $"2  Buy a round (CHF {FondueRules.RoundCost(pot.Seats.Length - 1)})";
        _forfeits[1].Disabled = TogetherService.Forfeits == ForfeitMode.Mild;
        _forfeits[1].TooltipText = _forfeits[1].Disabled ? "Off on this server" : "";
        string key = string.Join(",", pot.Seats) + "|" + Convert.ToHexString(pot.Bits);
        if (key == _seatsKey) return;
        _seatsKey = key;
        foreach (var c in _seats.GetChildren()) c.QueueFree();
        for (int i = 0; i < pot.Seats.Length; i++)
        {
            byte b = pot.Bits[i];
            string chips = (b & 1) != 0 ? "bread" : "";
            foreach (var ing in new[] { Ingredient.Cheese, Ingredient.Wine, Ingredient.Kirsch, Ingredient.Potato })
                if ((b & (1 << (1 + (int)ing))) != 0) chips += (chips.Length > 0 ? " · " : "") + ing.ToString().ToLowerInvariant();
            var seat = UiKit.VBox(0);
            seat.AddChild(UiKit.Text(_svc!.NameOfPeer(pot.Seats[i]), UiTheme.FontSmall, pot.Seats[i] == _svc.Self ? UiTheme.Amber : UiTheme.Text, bold: true));
            seat.AddChild(UiKit.Text(chips.Length > 0 ? chips : "nothing yet", UiTheme.FontTiny, UiTheme.TextFaint));
            _seats.AddChild(seat);
        }
    }

    private static string SpotName(PotSpot s) => s switch
    {
        PotSpot.Campfire => "at the fire",
        PotSpot.Hut => "at the hut",
        PotSpot.Festival => "at the long table",
        _ => "at the table",
    };

    /// <summary>
    /// The stirring pad: a faint lemniscate guide, the trace (a fixed 64-point ring), and the split meter as a ring round
    /// the pad (amber, red past 0.7). Drawn with <c>_Draw</c> on a change or a new sample only.
    /// </summary>
    private sealed partial class StirPad : Control
    {
        public event Action<Vector2>? Stirred;
        public event Action? Clicked;
        public float Split;
        public bool Splitted, Guide = true;
        private readonly Vector2[] _trace = new Vector2[64];
        private int _count, _head;
        private bool _down, _moved;
        private Vector2 _downAt;
        private readonly Vector2[] _guide = new Vector2[65];

        public StirPad()
        {
            for (int i = 0; i <= 64; i++)
            {
                var (x, y) = StirRules.Guide(i % 64);
                _guide[i] = new Vector2(x, y);
            }
        }

        public void Push(Vector2 unit)
        {
            _head = (_head + 1) % _trace.Length;
            _trace[_head] = unit;
            if (_count < _trace.Length) _count++;
            QueueRedraw();
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            {
                if (b.Pressed) { _down = true; _moved = false; _downAt = b.Position; }
                else
                {
                    if (_down && !_moved) Clicked?.Invoke();
                    _down = false;
                }
                AcceptEvent();
            }
            else if (e is InputEventMouseMotion m && _down)
            {
                if (m.Position.DistanceTo(_downAt) > 6) _moved = true;
                if (_moved) Stirred?.Invoke(new Vector2(m.Position.X / Size.X, m.Position.Y / Size.Y));
                AcceptEvent();
            }
        }

        public override void _Draw()
        {
            var size = Size;
            DrawRect(new Rect2(Vector2.Zero, size), new Color(0, 0, 0, 0.25f));
            var c = size / 2;
            float r = Mathf.Min(size.X, size.Y) * 0.46f;
            // the split meter round it: amber, red past 0.7 (and its words beside, never colour alone)
            DrawArc(c, r, 0, Mathf.Tau, 48, new Color(1, 1, 1, 0.12f), 4f);
            var meter = Splitted || Split > 0.7f ? UiTheme.Bad : UiTheme.Amber;
            if (Split > 0.001f) DrawArc(c, r, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * Mathf.Clamp(Split, 0, 1), 48, meter, 4f);
            float sx = size.X * 0.42f, sy = size.Y * 0.42f;
            if (Guide)
                for (int i = 0; i < 64; i++)
                    DrawLine(c + new Vector2(_guide[i].X * sx, _guide[i].Y * sy), c + new Vector2(_guide[i + 1].X * sx, _guide[i + 1].Y * sy), new Color(1, 1, 1, 0.18f), 2f);
            for (int k = 1; k < _count; k++)
            {
                var a = _trace[(_head - k + 1 + _trace.Length) % _trace.Length];
                var b = _trace[(_head - k + _trace.Length) % _trace.Length];
                float fade = 1f - k / (float)_count;
                DrawLine(new Vector2(a.X * size.X, a.Y * size.Y), new Vector2(b.X * size.X, b.Y * size.Y), new Color(UiTheme.Amber, 0.25f + 0.75f * fade), 3f);
            }
        }
    }
}
