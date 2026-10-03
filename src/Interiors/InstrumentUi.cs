using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// Sat at a house instrument (#433): the letter keys play it. A piano or keyboard has an octave
/// on the tracker layout (<see cref="PlayerInput.InstrumentKeys"/>, white keys A S D F G H J K,
/// black W E T Y U), Z / X an octave down / up; a drum kit has its pieces on the same keys. Shift
/// accents a note. Esc or Space stands up. A strip of keys at the bottom of the screen lights up
/// with what is played. Keys are taken in <c>_Input</c>, before anything else sees them, and
/// <see cref="UiFocus"/> keeps the player from walking off while the keys are notes.
/// </summary>
public partial class InstrumentUi : CanvasLayer
{
    private sealed partial class View : Control
    {
        public Action<View>? Drawer;
        public override void _Draw() => Drawer?.Invoke(this);
    }

    /// <summary>Which drum each key strikes (<see cref="InstrumentSynth.DrumNames"/>): the home row in order, the row above doubling them.</summary>
    private static readonly int[] DrumOfKey = { 0, 0, 1, 1, 2, 3, 2, 4, 4, 5, 6, 6, 7 };
    private static readonly bool[] Black = { false, true, false, true, false, false, true, false, true, false, true, false, false };

    private readonly View _view = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly float[] _lit = new float[13];
    private string _plan = "";
    private int _index = -1;
    private InstrumentKind _kind;
    private int _octave;

    public bool IsOpen => _view.Visible;
    public InstrumentKind Kind => _kind;

    /// <summary>The note key <paramref name="k"/> plays now: a MIDI note, or a drum.</summary>
    public int NoteOf(int k) => _kind == InstrumentKind.Drums ? DrumOfKey[k] : Math.Min(InstrumentSynth.HighNote, 60 + 12 * _octave + k);

    public override void _Ready()
    {
        Layer = 11;
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _view.Drawer = Draw;
        _view.Visible = false;
        AddChild(_view);
    }

    public override void _ExitTree() => UiFocus.Set(this, false);

    public void Open(FootPlayer me, string plan, int index, InstrumentKind kind)
    {
        _me = me;
        _plan = plan;
        _index = index;
        _kind = kind;
        _octave = 0;
        Array.Clear(_lit);
        _view.Visible = true;
        UiFocus.Set(this, true);
    }

    /// <summary>Stands the player up and puts the strip away.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        _view.Visible = false;
        UiFocus.Set(this, false);
        if (Me is { PlayingAt: >= 0 } me) me.StopPlaying();
    }

    private FootPlayer? _me;
    private FootPlayer? Me => _me != null && IsInstanceValid(_me) ? _me : null;

    /// <summary>Plays key <paramref name="k"/> (0..12): input and probes both come through here.</summary>
    public void Press(int k, bool accent = false)
    {
        if (!IsOpen || k < 0 || k >= _lit.Length) return;
        _lit[k] = 1f;
        float velocity = (accent ? 1f : 0.72f) * (float)GD.RandRange(0.93, 1.0);
        HouseProps.Instance?.Strike(_plan, _index, NoteOf(k), velocity);
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen || e.IsEcho() || !e.IsPressed()) return;
        // a chat line being typed keeps its letters
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
        if (e.IsActionPressed(PlayerInput.Menu) || e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Jump))
        {
            Close();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed(PlayerInput.InstrumentOctaveDown) || e.IsActionPressed(PlayerInput.InstrumentOctaveUp))
        {
            if (_kind != InstrumentKind.Drums)
                _octave = Math.Clamp(_octave + (e.IsActionPressed(PlayerInput.InstrumentOctaveUp) ? 1 : -1), -2, 2);
            GetViewport().SetInputAsHandled();
            return;
        }
        for (int k = 0; k < PlayerInput.InstrumentKeys.Length; k++)
        {
            if (!e.IsActionPressed(PlayerInput.InstrumentKeys[k])) continue;
            Press(k, Input.IsKeyPressed(Key.Shift));
            GetViewport().SetInputAsHandled();
            return;
        }
        // E is a note here: nothing else gets the keyboard while playing (the mouse is free)
        if (e is InputEventKey) GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        // stood up some other way: hurt, carried off, out of the building
        if (Me is not { PlayingAt: >= 0 } me || me.PlayingAt != _index)
        {
            _view.Visible = false;
            UiFocus.Set(this, false);
            return;
        }
        for (int k = 0; k < _lit.Length; k++) _lit[k] = Mathf.Max(0f, _lit[k] - (float)delta * 4f);
        _view.QueueRedraw();
    }

    private void Draw(View v)
    {
        var font = ThemeDB.FallbackFont;
        var size = v.Size;
        bool drums = _kind == InstrumentKind.Drums;
        float keyW = drums ? 64f : 46f, gap = 4f;
        int count = drums ? 8 : 13;
        float total = drums ? count * (keyW + gap) : 8 * (keyW + gap);
        var origin = new Vector2((size.X - total) / 2, size.Y - 190);
        var panel = new Rect2(origin - new Vector2(18, 40), new Vector2(total + 36, 170));
        v.DrawRect(panel, new Color(0.05f, 0.06f, 0.07f, 0.85f));
        v.DrawRect(panel, new Color(0.35f, 0.38f, 0.42f), false, 2f);
        string title = _kind switch
        {
            InstrumentKind.Piano => $"Piano · octave {4 + _octave}",
            InstrumentKind.Keyboard => $"Keyboard · octave {4 + _octave}",
            _ => "Drums",
        };
        v.DrawString(font, new Vector2(panel.Position.X, origin.Y - 16), title, HorizontalAlignment.Center, panel.Size.X, 18, Colors.White);

        var lit = new Color(0.98f, 0.78f, 0.25f);
        if (drums)
        {
            // one pad per drum, labelled with the home-row key that hits it
            int[] home = { 0, 2, 4, 5, 7, 9, 11, 12 };
            for (int d = 0; d < 8; d++)
            {
                float glow = 0f;
                for (int k = 0; k < 13; k++) if (DrumOfKey[k] == d) glow = Mathf.Max(glow, _lit[k]);
                var r = new Rect2(origin + new Vector2(d * (keyW + gap), 0), new Vector2(keyW, keyW));
                v.DrawRect(r, new Color(0.22f, 0.23f, 0.26f).Lerp(lit, glow));
                v.DrawString(font, r.Position + new Vector2(0, 28), InputHints.Label(PlayerInput.InstrumentKeys[home[d]]),
                    HorizontalAlignment.Center, keyW, 18, Colors.White);
                v.DrawString(font, r.Position + new Vector2(0, keyW + 16), InstrumentSynth.DrumNames[d], HorizontalAlignment.Center, keyW, 12,
                    new Color(0.8f, 0.82f, 0.86f));
            }
        }
        else
        {
            // the white keys, then the black ones over them
            int white = 0;
            var whiteAt = new float[13];
            for (int k = 0; k < 13; k++)
            {
                if (Black[k]) { whiteAt[k] = white * (keyW + gap) - (keyW + gap) / 2; continue; }
                whiteAt[k] = white * (keyW + gap);
                var r = new Rect2(origin + new Vector2(whiteAt[k], 0), new Vector2(keyW, 100));
                v.DrawRect(r, new Color(0.93f, 0.93f, 0.9f).Lerp(lit, _lit[k]));
                v.DrawString(font, r.Position + new Vector2(0, 90), InputHints.Label(PlayerInput.InstrumentKeys[k]), HorizontalAlignment.Center, keyW, 14,
                    new Color(0.15f, 0.15f, 0.17f));
                white++;
            }
            for (int k = 0; k < 13; k++)
            {
                if (!Black[k]) continue;
                var r = new Rect2(origin + new Vector2(whiteAt[k] + keyW * 0.2f + gap / 2, 0), new Vector2(keyW * 0.7f, 60));
                v.DrawRect(r, new Color(0.08f, 0.08f, 0.09f).Lerp(lit, _lit[k]));
                v.DrawString(font, r.Position + new Vector2(0, 52), InputHints.Label(PlayerInput.InstrumentKeys[k]), HorizontalAlignment.Center, r.Size.X, 12,
                    Colors.White);
            }
        }
        string help = drums
            ? "Shift: accent · {menu} / {jump}: stand up"
            : "{instrument_octave_down} / {instrument_octave_up}: octave · Shift: accent · {menu} / {jump}: stand up";
        v.DrawString(font, new Vector2(panel.Position.X, panel.End.Y - 10), InputHints.Format(help), HorizontalAlignment.Center, panel.Size.X, 12,
            new Color(0.65f, 0.67f, 0.7f));
    }
}
