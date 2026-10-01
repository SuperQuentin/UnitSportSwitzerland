using Godot;

namespace UnitSport.Core;

/// <summary>
/// Viewport-level settings — window mode and size, 3D render scale, vsync — and the F11 /
/// Alt+Enter fullscreen toggle. Owned by <see cref="GameShell"/>, so they hold on the title
/// screen as well as in a world, and outlive every world that comes and goes.
/// </summary>
public partial class DisplaySettings : Node
{
    private (WindowMode Mode, int W, int H)? _appliedWindow;
    private WindowMode _lastFullscreenMode = WindowMode.Borderless;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Apply();
        GameSettings.Changed += Apply;
    }

    public override void _ExitTree() => GameSettings.Changed -= Apply;

    private void Apply()
    {
        var s = GameSettings.Current;
        // in VR the headset picks its own resolution and paces its own frames (XrSession)
        if (!XR.XrSession.Active)
        {
            GetViewport().Scaling3DScale = s.RenderScale;
            DisplayServer.WindowSetVsyncMode(s.VSync
                ? DisplayServer.VSyncMode.Enabled
                : DisplayServer.VSyncMode.Disabled);
        }
        ApplyWindow(s);
    }

    /// <summary>
    /// F11 / Alt+Enter toggle fullscreen from anywhere, menus and chat box included. From _Input,
    /// not _UnhandledInput: ChatUi takes Enter in _UnhandledKeyInput and would open on Alt+Enter.
    /// Goes through the saved setting so the Settings screen agrees; windowed comes back to the
    /// fullscreen kind (borderless or exclusive) that was last used.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        bool altEnter = key.AltPressed && key.PhysicalKeycode is Key.Enter or Key.KpEnter;
        if (key.PhysicalKeycode != Key.F11 && !altEnter) return;
        if (DisplayServer.GetName() == "headless") return;

        var s = GameSettings.Current;
        if (s.WindowMode == WindowMode.Windowed)
            s.WindowMode = _lastFullscreenMode;
        else
        {
            _lastFullscreenMode = s.WindowMode;
            s.WindowMode = WindowMode.Windowed;
        }
        s.Commit();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>
    /// Window mode and size, touched only when those settings themselves changed: every other
    /// setting also raises <see cref="GameSettings.Changed"/>, and re-applying the saved size then
    /// would snap back a window the player had just dragged to a new size.
    /// </summary>
    private void ApplyWindow(GameSettings s)
    {
        if (DisplayServer.GetName() == "headless") return;
        var wanted = (s.WindowMode, s.WindowWidth, s.WindowHeight);
        if (_appliedWindow == wanted) return;
        _appliedWindow = wanted;

        switch (s.WindowMode)
        {
            case WindowMode.Fullscreen:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
                return;
            case WindowMode.Borderless:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                return;
        }

        if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        if (s.WindowWidth <= 0 || s.WindowHeight <= 0) return;

        int screen = DisplayServer.WindowGetCurrentScreen();
        var usable = DisplayServer.ScreenGetUsableRect(screen);
        var size = new Vector2I(Math.Min(s.WindowWidth, usable.Size.X), Math.Min(s.WindowHeight, usable.Size.Y));
        DisplayServer.WindowSetSize(size);
        DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
    }
}
