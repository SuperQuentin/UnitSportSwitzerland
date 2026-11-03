using Godot;

namespace UnitSport.Core;

/// <summary>
/// Settings › Controls › Touch layout (#1048): the <see cref="TouchEditor"/> with a mode picker,
/// over a still backdrop, so every mode's controls can be placed without being in it. Over the
/// menus (layer 45); it takes every touch and Back until Done or Cancel.
/// </summary>
public partial class TouchLayoutScreen : CanvasLayer
{
    private TouchEditor _editor = null!;

    /// <summary>One is open: the game's own touch overlay stands aside meanwhile.</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>The editor on this screen (for <see cref="TouchCheck"/>).</summary>
    public TouchEditor Editor => _editor;

    public static TouchLayoutScreen Open(Node from)
    {
        var screen = new TouchLayoutScreen { Name = "TouchLayoutScreen", Layer = 45 };
        from.GetTree().Root.AddChild(screen);
        return screen;
    }

    public override void _EnterTree() => IsOpen = true;
    public override void _ExitTree() => IsOpen = false;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        // a still Alpine evening: the controls read against it as against the game
        var backdrop = new ColorRect { Color = new Color(0.16f, 0.22f, 0.2f), MouseFilter = Control.MouseFilterEnum.Stop };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);
        var sky = new ColorRect { Color = new Color(0.36f, 0.48f, 0.62f), MouseFilter = Control.MouseFilterEnum.Ignore };
        sky.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        sky.AnchorBottom = 0.55f;
        AddChild(sky);
        _editor = new TouchEditor(TouchMode.Foot, TouchLayout.Fullest(TouchMode.Foot), picker: true);
        _editor.Finished = Close;
        AddChild(_editor);
    }

    public override void _Input(InputEvent e)
    {
        if (_editor.Handle(e)) GetViewport().SetInputAsHandled();
    }

    private void Close(Dictionary<string, TouchOverride>? kept)
    {
        if (kept != null)
        {
            GameSettings.Current.TouchOverrides = kept;
            GameSettings.SaveTouchOverrides();
        }
        QueueFree();
    }
}
