using Godot;
using UnitSport.Ui;

namespace UnitSport.Movie;

/// <summary>
/// "● REC 3:12" in the top corner while a Record / Stop session runs (#775), so nobody forgets it is
/// on. The text changes once a second, never per frame.
/// </summary>
public partial class RecordingBadge : CanvasLayer
{
    private readonly ReplayRecorder _recorder;
    private readonly Label _label;
    private int _shown = -1;

    public RecordingBadge(ReplayRecorder recorder)
    {
        _recorder = recorder;
        Name = "RecordingBadge";
        Layer = 30;
        _label = UiKit.Text("", UiTheme.FontBody, new Color(1f, 0.35f, 0.3f), bold: true);
        _label.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _label.Position = new Vector2(20, 14);
        _label.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_label);
    }

    public override void _Process(double delta)
    {
        int seconds = (int)_recorder.SessionSeconds;
        if (seconds == _shown) return;
        _shown = seconds;
        // hidden while the studio is open: nothing is recorded then
        _label.Visible = !_recorder.Paused;
        _label.Text = $"● REC {MovieSession.Clock(seconds)}";
    }
}
