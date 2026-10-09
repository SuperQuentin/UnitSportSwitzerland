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
        // before the first Apply: it sizes the window, and the PS1 scale depends on that size
        GetWindow().SizeChanged += ApplyScale;
        Styles.StyleKit.ChoiceChanged += ApplyView;
        GameSettings.Changed += Apply;
        Apply();
    }

    public override void _ExitTree()
    {
        GameSettings.Changed -= Apply;
        Styles.StyleKit.ChoiceChanged -= ApplyView;
        GetWindow().SizeChanged -= ApplyScale;
    }

    /// <summary>
    /// The viewport's <c>Scaling3DScale</c> for a <see cref="GameSettings.RenderScale"/> in a
    /// window of <paramref name="window"/> pixels. Of the window's pixels (100% = native), except
    /// in PS1, whose look is its low resolution: there it is of the UI canvas, 1152x648 widened or
    /// heightened to the window's aspect (stretch aspect "expand"), so PS1 at 75% is 864x486 on
    /// any screen, as it was before the 3D followed the window (#306).
    /// </summary>
    public static float EffectiveScale(float scale, Vector2I window)
    {
        if (Styles.StyleKit.Style != Styles.VisualStyle.Ps1 || window.X <= 0 || window.Y <= 0) return scale;
        float canvasToWindow = Math.Min(window.X / (float)GameSettings.BaseWidth, window.Y / (float)GameSettings.BaseHeight);
        return scale / canvasToWindow;
    }

    private void ApplyScale() =>
        // In VR the window is the monitor view: its render scale still applies
        GetViewport().Scaling3DScale = EffectiveScale(GameSettings.Current.RenderScale, GetWindow().Size);

    /// <summary>What a style change touches: the 3D scale and the anti-aliasing.</summary>
    private void ApplyView()
    {
        ApplyScale();
        ApplyAntiAliasing();
    }

    /// <summary>
    /// The main view's MSAA or FXAA (#768), off in PS1, whose jagged low-resolution edges are its
    /// look. Door portals and photos copy the main viewport's, the headset has its own (VrMsaa).
    /// </summary>
    private void ApplyAntiAliasing()
    {
        var view = GetViewport();
        var aa = Styles.StyleKit.Style == Styles.VisualStyle.Ps1 ? AntiAliasing.Off : GameSettings.Current.AntiAliasing;
        view.Msaa3D = aa switch
        {
            AntiAliasing.Msaa2x => Viewport.Msaa.Msaa2X,
            AntiAliasing.Msaa4x => Viewport.Msaa.Msaa4X,
            AntiAliasing.Msaa8x => Viewport.Msaa.Msaa8X,
            _ => Viewport.Msaa.Disabled,
        };
        view.ScreenSpaceAA = aa == AntiAliasing.Fxaa ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;
    }

    private void Apply()
    {
        var s = GameSettings.Current;
        ApplyView();
        // In VR the headset paces the frames, and a desktop vsync on top would hold it to the
        // monitor's rate.
        if (!XR.XrSession.Active)
            DisplayServer.WindowSetVsyncMode(s.VSync
                ? DisplayServer.VSyncMode.Enabled
                : DisplayServer.VSyncMode.Disabled);
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
