using Godot;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// The MMO free cursor (#654). Tap Alt in game and the mouse is let go while the game keeps going,
/// to point and click what is on screen. Tap Alt again, or click anywhere in the world (anything
/// no control takes), and the mouse looks again.
///
/// <para>
/// It toggles on Alt's release, and only when nothing else was pressed while Alt was down, so
/// Alt+Enter (fullscreen) and Alt+Tab never trigger it. It only takes a captured pointer: one that
/// a menu, the inventory or the chat has let go belongs to them, and they capture it back themselves.
/// </para>
/// </summary>
public partial class CursorToggle : Node
{
    /// <summary>The pointer is free because of Alt (not a menu): the next world click takes it back.</summary>
    public static bool Free { get; private set; }

    private readonly Func<bool> _menuOpen;
    private bool _armed;
    private Label _hint = null!;

    public CursorToggle(Func<bool> menuOpen)
    {
        Name = "CursorToggle";
        _menuOpen = menuOpen;
    }

    public override void _Ready()
    {
        var layer = new CanvasLayer { Name = "CursorHint", Layer = 30 };
        AddChild(layer);
        _hint = UiTheme.Prompt(0);
        _hint.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _hint.Position = new Vector2(-220, 14);
        _hint.Size = new Vector2(440, 30);
        _hint.AddThemeFontSizeOverride("font_size", 15);
        _hint.Text = "Cursor free  ·  Alt or click the world to look again";
        layer.AddChild(_hint);
    }

    public override void _Input(InputEvent e)
    {
        // seen before any control takes it: whether Alt was pressed alone
        if (e is not InputEventKey { Echo: false } k) return;
        if (k.PhysicalKeycode == Key.Alt)
        {
            if (k.Pressed) { _armed = true; return; }
            if (_armed) Toggle();
            _armed = false;
        }
        else if (k.Pressed) _armed = false;   // Alt+Enter, Alt+Tab, Alt+F4: a chord, not a tap
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // a click no control took landed in the world: the mouse looks again, and the click does nothing else
        if (!Free || e is not InputEventMouseButton { Pressed: true } b) return;
        if (b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight) return;
        GetViewport().SetInputAsHandled();
        SetFree(false);
    }

    public override void _Process(double delta)
    {
        // someone else took the pointer meanwhile (a menu opened, a UI captured it back): no longer ours
        if (Free && (Input.MouseMode == Input.MouseModeEnum.Captured || _menuOpen())) { Free = false; _hint.Visible = false; }
    }

    private void Toggle()
    {
        if (UiFocus.TextEntryActive || _menuOpen()) return;
        if (Free) SetFree(false);
        else if (Input.MouseMode == Input.MouseModeEnum.Captured) SetFree(true);
    }

    private void SetFree(bool free)
    {
        Free = free;
        _hint.Visible = free;
        if (free) Input.MouseMode = Input.MouseModeEnum.Visible;
        else MouseCapture.Capture();
    }

    public override void _ExitTree()
    {
        if (Free && Input.MouseMode == Input.MouseModeEnum.Visible) Free = false;
    }
}
