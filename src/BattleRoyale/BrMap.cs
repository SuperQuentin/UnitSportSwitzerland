using Godot;
using UnitSport.Core;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The full map of the match region (#190), on M during a match instead of the place search. It shows:
/// <list type="bullet">
/// <item>the map image;</item>
/// <item>a lettered grid every kilometre (A1 at the north-west);</item>
/// <item>the towns' names;</item>
/// <item>the circles, the line to safety, your waypoint and your arrow (<see cref="BrMapDraw"/>).</item>
/// </list>
/// The wheel zooms around the pointer, a drag pans, a right click sets or clears the waypoint,
/// and M or Esc closes it. The game goes on underneath. A row of buttons at the bottom right sets the
/// <see cref="BrPrefs"/> (minimap size and turning, compass, stings), where their effect is in view.
/// </summary>
public partial class BrMap : CanvasLayer
{
    private readonly BrManager _br;
    private View _view = null!;
    private float _zoom = 1f;
    private Vector2 _pan;         // zone metres at the screen centre
    private bool _dragging;

    public bool IsOpen => _view.Visible;

    public BrMap(BrManager br)
    {
        _br = br;
        Name = "BrMap";
        Layer = 20;
    }

    public override void _Ready()
    {
        _view = new View { Map = this, Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
        BuildOptions();
    }

    private readonly List<(Button Button, Func<string> Label)> _options = new();

    /// <summary>The display choices (#231), each a button that steps through its values and saves.</summary>
    private void BuildOptions()
    {
        var row = new HBoxContainer { Name = "Options" };
        // bottom right, clear of the title and of the help line
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.KeepSize, 12);
        row.GrowHorizontal = Control.GrowDirection.Begin;
        row.GrowVertical = Control.GrowDirection.Begin;
        row.AddThemeConstantOverride("separation", 6);
        _view.AddChild(row);
        var p = BrPrefs.Current;
        void Option(Func<string> label, Action step)
        {
            var b = new Button { Text = label(), FocusMode = Control.FocusModeEnum.None, MouseFilter = Control.MouseFilterEnum.Stop };
            b.AddThemeFontSizeOverride("font_size", 13);
            b.Pressed += () =>
            {
                step();
                p.Save();
                foreach (var (button, text) in _options) button.Text = text();
            };
            row.AddChild(b);
            _options.Add((b, label));
        }
        Option(() => $"Minimap: {p.Minimap}", () => p.Minimap = (BrPrefs.MapSize)(((int)p.Minimap + 1) % 3));
        Option(() => "Minimap turns: " + (p.MinimapTurns ? "on" : "off"), () => p.MinimapTurns = !p.MinimapTurns);
        Option(() => "Compass: " + (p.Compass ? "on" : "off"), () => p.Compass = !p.Compass);
        Option(() => "Stings: " + (p.Stings ? "on" : "off"), () => p.Stings = !p.Stings);
    }

    public void Toggle() => SetOpen(!IsOpen);

    public void SetOpen(bool open)
    {
        if (open == IsOpen || (open && _br.MapTexture == null)) return;
        _view.Visible = open;
        UiFocus.Set(this, open);
        if (open)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _zoom = 1f;
            _pan = Vector2.Zero;
        }
        else MouseCapture.Capture();
    }

    public override void _Process(double delta)
    {
        // the match ended or this player was released: nothing left to show
        if (IsOpen && (!_br.State.Running || !_br.InMatch)) SetOpen(false);
        if (IsOpen) _view.QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed(PlayerInput.Teleport) || e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu))
        {
            SetOpen(false);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Pixels per metre at the current zoom.</summary>
    private float Ppm => Mathf.Min(_view.Size.X, _view.Size.Y - 2 * TitleBand) * 0.92f / _br.State.Side * _zoom;

    /// <summary>Room kept free above and below the map for the title, the grid letters and the help line.</summary>
    private const float TitleBand = 56f;

    private Vector2 Middle => _view.Size * 0.5f;
    private Vector2 ToScreen(Vector2 zone) => Middle + BrMapDraw.Screen(zone - _pan) * Ppm;
    private Vector2 ToZone(Vector2 screen) => _pan + BrMapDraw.Screen((screen - Middle) / Ppm);

    private void OnInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
            {
                // zoom about the pointer: the point under it stays under it
                var under = ToZone(wheel.Position);
                _zoom = Mathf.Clamp(_zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1.25f : 0.8f), 1f, 8f);
                _pan += under - ToZone(wheel.Position);
                Clamp();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                _dragging = left.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } right:
            {
                var at = ToZone(right.Position);
                // a click on the waypoint takes it away; anywhere else moves it there
                _br.Waypoint = _br.Waypoint is { } wp && ToScreen(wp).DistanceTo(right.Position) < 16f ? null : at;
                break;
            }
            case InputEventMouseMotion motion when _dragging:
                _pan -= BrMapDraw.Screen(motion.Relative / Ppm);
                Clamp();
                break;
        }
        _view.AcceptEvent();
    }

    private void Clamp()
    {
        float half = _br.State.Side * 0.5f;
        _pan = new Vector2(Mathf.Clamp(_pan.X, -half, half), Mathf.Clamp(_pan.Y, -half, half));
    }

    private partial class View : Control
    {
        public BrMap Map = null!;

        public override void _GuiInput(InputEvent e) => Map.OnInput(e);

        public override void _Draw()
        {
            var br = Map._br;
            if (br.MapTexture is not { } tex) return;
            var font = ThemeDB.FallbackFont;
            float side = br.State.Side, half = side * 0.5f, ppm = Map.Ppm;

            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.04f, 0.05f, 0.07f, 0.92f));
            var nw = Map.ToScreen(new Vector2(-half, half));
            DrawTextureRect(tex, new Rect2(nw, new Vector2(side, side) * ppm), false);

            // the grid: a kilometre a square, letters west to east, numbers north to south
            int cells = (int)Mathf.Ceil(side / 1000f);
            float step = side / cells;
            for (int i = 0; i <= cells; i++)
            {
                var a = Map.ToScreen(new Vector2(-half + i * step, half));
                var b = Map.ToScreen(new Vector2(-half + i * step, -half));
                DrawLine(a, b, new Color(0, 0, 0, 0.35f), 1f);
                var c = Map.ToScreen(new Vector2(-half, half - i * step));
                var d = Map.ToScreen(new Vector2(half, half - i * step));
                DrawLine(c, d, new Color(0, 0, 0, 0.35f), 1f);
                if (i < cells)
                {
                    DrawString(font, Map.ToScreen(new Vector2(-half + (i + 0.5f) * step, half)) + new Vector2(-5, -8),
                        ((char)('A' + i)).ToString(), HorizontalAlignment.Left, -1, 16, Colors.White);
                    DrawString(font, Map.ToScreen(new Vector2(-half, half - (i + 0.5f) * step)) + new Vector2(-22, 6),
                        (i + 1).ToString(), HorizontalAlignment.Left, -1, 16, Colors.White);
                }
            }

            // towns, biggest in larger type
            foreach (var (name, at, size) in br.Towns)
            {
                var p = Map.ToScreen(at);
                int fs = size > 1500 ? 17 : size > 300 ? 14 : 12;
                if (size < 60 && Map._zoom < 2f) continue;
                float w = font.GetStringSize(name, HorizontalAlignment.Left, -1, fs).X;
                DrawString(font, p + new Vector2(-w * 0.5f + 1, 1), name, HorizontalAlignment.Left, -1, fs, new Color(0, 0, 0, 0.8f));
                DrawString(font, p + new Vector2(-w * 0.5f, 0), name, HorizontalAlignment.Left, -1, fs, Colors.White);
            }

            BrMapDraw.Overlays(this, br, Map.ToScreen, ppm, Size.Length() * 2f);
            DrawRect(new Rect2(nw, new Vector2(side, side) * ppm), new Color(0, 0, 0, 0.9f), false, 2f);

            // the title and the help line
            string title = $"{br.State.AreaName} · {side / 1000:F0} × {side / 1000:F0} km";
            if (br.ViewPoint() is { } v) title += $" · you are in {Cell(v.Position, half, step)}";
            DrawString(font, new Vector2(24, 34), title, HorizontalAlignment.Left, -1, 20, Colors.White);
            DrawString(font, new Vector2(24, Size.Y - 20),
                InputHints.Format("Wheel: zoom · drag: move · right click: waypoint · {teleport} / Esc: close"),
                HorizontalAlignment.Left, -1, 14, new Color(1, 1, 1, 0.8f));
        }

        private static string Cell(Vector2 p, float half, float step)
        {
            int col = (int)Mathf.Floor((p.X + half) / step), row = (int)Mathf.Floor((half - p.Y) / step);
            return col < 0 || row < 0 || col > 25 ? "the storm" : $"{(char)('A' + col)}{row + 1}";
        }
    }
}
