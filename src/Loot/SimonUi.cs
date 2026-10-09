using Godot;
using UnitSport.Audio;
using UnitSport.Core;

namespace UnitSport.Loot;

/// <summary>
/// The second lock of a bank vault's safe (#213): a Simon panel. Four lit pads play a sequence,
/// one more each round, and the player plays it back; the last round is the whole sequence
/// (<see cref="LootTables.SimonSequence"/>), as long as the safe is rich. A wrong pad buzzes and
/// starts again from the first round, so the sequence is learnt, not guessed. Pads: 1-4, the
/// arrows or the d-pad (up, right, down, left = pads 0-3) or a click; E, B or Esc walks away.
/// Finished, the dial's numbers and the sequence go to the server together
/// (<see cref="LootService.SubmitSimon"/>), which checks both.
/// </summary>
public partial class SimonUi : CanvasLayer
{
    private const float Lit = 0.42f, Gap = 0.16f, RoundPause = 0.7f;

    private static readonly Color[] PadColors =
    {
        new(0.20f, 0.78f, 0.32f), new(0.88f, 0.22f, 0.20f), new(0.95f, 0.80f, 0.20f), new(0.22f, 0.45f, 0.92f),
    };
    private static readonly float[] PadPitch = { 0.84f, 1.0f, 1.19f, 1.41f };

    private sealed partial class View : Control
    {
        public Action<View>? Drawer;
        public override void _Draw() => Drawer?.Invoke(this);
    }

    private enum Phase { Showing, Input, Failed, Waiting }

    private readonly LootService _service;
    private readonly View _view = new() { MouseFilter = Control.MouseFilterEnum.Stop };
    private int[] _seq = Array.Empty<int>();
    private string _what = "";
    private Phase _phase;
    private int _round;      // pads shown this round
    private int _entered;    // pads played back this round
    private float _clock;
    private int _lit = -1;
    private float _litFor;

    public SimonUi(LootService service) => _service = service;
    public SimonUi() : this(null!) { }

    public bool IsOpen => _view.Visible;
    /// <summary>For probes: the round (pads shown), and whether it waits for the player.</summary>
    public int Round => _round;
    public bool AwaitingInput => IsOpen && _phase == Phase.Input;
    public int Length => _seq.Length;

    public override void _Ready()
    {
        Layer = 12;
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _view.Drawer = Draw;
        _view.Visible = false;
        _view.GuiInput += OnGuiInput;
        AddChild(_view);
    }

    public override void _ExitTree() => UiFocus.Set(this, false);

    public void Open(string what, int[] sequence)
    {
        _what = what;
        _seq = sequence;
        _view.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        StartRound(1);
    }

    public void Close()
    {
        if (!IsOpen) return;
        _view.Visible = false;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    private void StartRound(int round)
    {
        _round = Math.Clamp(round, 1, _seq.Length);
        _entered = 0;
        _phase = Phase.Showing;
        _clock = -RoundPause;
        _lit = -1;
    }

    /// <summary>The player pressed a pad (keys, pad, click and probes all come through here).</summary>
    public void Press(int pad)
    {
        if (!IsOpen || _phase != Phase.Input || pad < 0 || pad > 3) return;
        Flash(pad);
        if (_seq[_entered] != pad)
        {
            _phase = Phase.Failed;
            _clock = 0;
            _service?.PlayAt(SfxSynth.Impact, 0.5f, -2);
            return;
        }
        _entered++;
        if (_entered < _round) return;
        if (_round < _seq.Length) { StartRound(_round + 1); return; }
        _phase = Phase.Waiting;
        _service?.PlayAt(SfxSynth.Impact, 0.9f, -2);
        _service?.SubmitSimon((int[])_seq.Clone());
    }

    private void Flash(int pad)
    {
        _lit = pad;
        _litFor = Lit;
        _service?.PlayAt(SfxSynth.Chime, PadPitch[pad], -6);
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        float dt = (float)delta;
        _litFor -= dt;
        if (_phase != Phase.Showing && _litFor <= 0) _lit = -1;
        switch (_phase)
        {
            case Phase.Showing:
            {
                // pad i lights at i * (Lit + Gap)
                float before = _clock;
                _clock += dt;
                int i = (int)MathF.Floor(_clock / (Lit + Gap));
                int was = before < 0 ? -1 : (int)MathF.Floor(before / (Lit + Gap));
                if (_clock >= 0 && i != was && i < _round) Flash(_seq[i]);
                if (_clock >= 0 && _clock - i * (Lit + Gap) > Lit) _lit = -1;
                if (i >= _round) { _phase = Phase.Input; _lit = -1; }
                break;
            }
            case Phase.Failed:
                _clock += dt;
                if (_clock > 1.1f) StartRound(1);
                break;
        }
        _view.QueueRedraw();
    }

    private static readonly Key[] PadKeys = { Key.Key1, Key.Key2, Key.Key3, Key.Key4 };

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)
            || e.IsActionPressed(PlayerInput.InteractMount) || e.IsActionPressed(PlayerInput.Inventory))
        {
            _service.StopPicking();
            GetViewport().SetInputAsHandled();
            return;
        }
        int pad = -1;
        if (e is InputEventKey k) pad = Array.IndexOf(PadKeys, k.PhysicalKeycode);
        if (pad < 0)
        {
            if (e.IsActionPressed("ui_up")) pad = 0;
            else if (e.IsActionPressed("ui_right")) pad = 1;
            else if (e.IsActionPressed("ui_down")) pad = 2;
            else if (e.IsActionPressed("ui_left")) pad = 3;
        }
        if (pad < 0) return;
        Press(pad);
        GetViewport().SetInputAsHandled();
    }

    private Vector2 Centre => _view.Size * 0.5f;

    /// <summary>Pad i's square: up (0), right (1), down (2), left (3) of the centre, a diamond of four.</summary>
    private Rect2 PadRect(int i)
    {
        var off = i switch { 0 => new Vector2(0, -1), 1 => new Vector2(1, 0), 2 => new Vector2(0, 1), _ => new Vector2(-1, 0) };
        const float s = 92f;
        return new Rect2(Centre + off * (s + 8) - new Vector2(s, s) / 2, new Vector2(s, s));
    }

    private void OnGuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb) return;
        for (int i = 0; i < 4; i++)
            if (PadRect(i).HasPoint(mb.Position)) Press(i);
        _view.AcceptEvent();
    }

    private void Draw(View v)
    {
        var font = ThemeDB.FallbackFont;
        var c = Centre;
        var panel = new Rect2(c - new Vector2(220, 230), new Vector2(440, 470));
        v.DrawRect(panel, new Color(0.05f, 0.06f, 0.07f, 0.92f));
        v.DrawRect(panel, new Color(0.35f, 0.38f, 0.42f), false, 2f);
        v.DrawString(font, new Vector2(panel.Position.X, panel.Position.Y + 30), $"Crack the {_what}: the code panel",
            HorizontalAlignment.Center, panel.Size.X, 20, Colors.White);

        for (int i = 0; i < 4; i++)
        {
            var col = PadColors[i] * (_lit == i ? 1.15f : 0.38f);
            col.A = 1;
            if (_phase == Phase.Failed) col = col.Lerp(new Color(0.6f, 0.1f, 0.1f), 0.5f);
            var r = PadRect(i);
            v.DrawRect(r, col);
            v.DrawRect(r, _lit == i ? Colors.White : new Color(0.2f, 0.2f, 0.22f), false, 3f);
            v.DrawString(font, r.Position + new Vector2(0, r.Size.Y / 2 + 8), (i + 1).ToString(),
                HorizontalAlignment.Center, r.Size.X, 22, new Color(1, 1, 1, 0.55f));
        }

        // progress: one dot per pad of the whole sequence, the ones this round brighter
        float y = panel.End.Y - 70;
        float x0 = c.X - (_seq.Length - 1) * 11f;
        for (int i = 0; i < _seq.Length; i++)
        {
            var col = i < _entered && _phase == Phase.Input ? new Color(0.35f, 0.9f, 0.4f)
                : i < _round ? new Color(0.75f, 0.76f, 0.8f) : new Color(0.25f, 0.26f, 0.3f);
            v.DrawCircle(new Vector2(x0 + i * 22f, y), 6, col);
        }
        string status = _phase switch
        {
            Phase.Showing => "Watch…",
            Phase.Input => $"Repeat it ({_entered}/{_round})",
            Phase.Failed => "Wrong — from the start",
            _ => "Opening…",
        };
        v.DrawString(font, new Vector2(panel.Position.X, y + 30), status, HorizontalAlignment.Center, panel.Size.X, 15, new Color(0.85f, 0.87f, 0.9f));
        v.DrawString(font, new Vector2(panel.Position.X, y + 50), (InputHints.Vr ? InputHints.Format("Point and pull · {interact_mount} / {ui_cancel} leave")
            : InputHints.Pad ? InputHints.Format("{ui_up} {ui_right} {ui_down} {ui_left} · {interact_mount} / {ui_cancel} leave")
            : InputHints.Format("1-4, arrows or click · {interact_mount} / {menu} leave")),
            HorizontalAlignment.Center, panel.Size.X, 12, new Color(0.6f, 0.62f, 0.66f));
    }
}
