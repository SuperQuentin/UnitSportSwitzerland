using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Playtest.Recording;

/// <summary>
/// F12 in a recorded playtest (#844): "What's wrong?". The moment is the key press, not the Enter:
/// the screenshot is the frame on screen before the box is drawn, and the time and place are taken
/// then too. Enter saves the note (<see cref="PlaytestRecorder.Note"/>), Esc drops it. Nothing
/// freezes. The text box registers with <see cref="UiFocus"/>, so typing never walks the player.
/// A pad notes from the playtest panel's Note button, as does VR through its panel.
/// </summary>
public partial class NoteTaker : CanvasLayer
{
    private readonly PlaytestRecorder _recorder;
    private PanelContainer _panel = null!;
    private LineEdit _box = null!;
    private Label _saved = null!;
    private Image? _shot;
    private double _t, _savedUntil;
    private Input.MouseModeEnum _mouseBefore;

    public NoteTaker(PlaytestRecorder recorder)
    {
        _recorder = recorder;
        Name = "NoteTaker";
        Layer = 17;   // over the playtest panel
    }

    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        _panel = new PanelContainer
        {
            Theme = UiTheme.Get(),
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = -300, OffsetRight = 300, OffsetTop = -150, OffsetBottom = -90,
            Visible = false,
        };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.9f, 10, 12));
        AddChild(_panel);
        var col = UiKit.VBox(6);
        _panel.AddChild(col);
        col.AddChild(UiKit.Text("What's wrong?  Enter saves the note, Esc drops it", UiTheme.FontSmall, UiTheme.TextDim));
        _box = new LineEdit { PlaceholderText = "the car went through the wall…", CustomMinimumSize = new Vector2(0, 36) };
        _box.FocusEntered += () => UiFocus.Set(this, true);
        _box.FocusExited += () => UiFocus.Set(this, false);
        _box.TextSubmitted += _ => Save();
        col.AddChild(_box);
        _saved = UiKit.Text("", UiTheme.FontSmall, UiTheme.Good);
        _saved.AnchorLeft = _saved.AnchorRight = 0.5f;
        _saved.AnchorTop = _saved.AnchorBottom = 1;
        _saved.OffsetLeft = -200; _saved.OffsetTop = -80;
        _saved.Visible = false;
        AddChild(_saved);
    }

    public override void _Process(double delta)
    {
        if (_saved.Visible && GameClock.Now > _savedUntil) _saved.Visible = false;
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen)
        {
            if (!UiFocus.TextEntryActive && e.IsActionPressed(PlayerInput.PlaytestNote, exactMatch: true) && !e.IsEcho())
            {
                Open();
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>The frame on screen now, unless there is no screen (headless) to take it from.</summary>
    public static Image? Grab(Viewport viewport) =>
        DisplayServer.GetName() == "headless" ? null : viewport.GetTexture()?.GetImage();

    private void Open()
    {
        _shot = Grab(GetViewport());
        _t = _recorder.Now;
        _mouseBefore = MouseCapture.Mode;
        MouseCapture.Mode = Input.MouseModeEnum.Visible;
        _panel.Visible = true;
        _box.Text = "";
        _box.GrabFocus();
    }

    private void Save()
    {
        string text = _box.Text.Trim();
        int id = _recorder.Note(text.Length > 0 ? text : "(no text)", "key", _shot, _t);
        Close();
        _saved.Text = id > 0 ? $"Note {id} saved" : "Not recording: the note was not saved";
        _saved.Visible = true;
        _savedUntil = GameClock.Now + 2;
    }

    private void Close()
    {
        _panel.Visible = false;
        _shot = null;
        _box.ReleaseFocus();
        UiFocus.Set(this, false);
        if (_mouseBefore == Input.MouseModeEnum.Captured) MouseCapture.Capture();
    }
}
