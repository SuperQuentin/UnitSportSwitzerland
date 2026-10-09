using Godot;
using UnitSport.Ui;

namespace UnitSport.Items;

/// <summary>
/// The cassette in the radio panel's player view (#725): a tape whose two reels turn while it
/// plays, the tape wound from the left reel to the right as the CD goes on, and a label that
/// thumps on the kicks. Drawn with a handful of primitives, redrawn only while it turns.
/// </summary>
public partial class RadioCassette : Control
{
    /// <summary>Turning (something plays).</summary>
    public bool Playing { get; set; }
    /// <summary>0..1 through the CD: how much tape has gone over to the right reel.</summary>
    public float Progress { get; set; }
    /// <summary>0..1, the music's hit right now: the label lights up on it.</summary>
    public float Kick { get; set; }

    private float _angle;

    public RadioCassette()
    {
        CustomMinimumSize = new Vector2(150, 92);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public override void _Process(double delta)
    {
        if (!Playing || !IsVisibleInTree()) return;
        _angle = Mathf.Wrap(_angle + (float)delta * 3.2f, 0f, Mathf.Tau);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        var shell = new Color(0.14f, 0.15f, 0.18f);
        var label = UiTheme.Amber.Lerp(Colors.White, 0.35f * Kick);
        var window = new Color(0.05f, 0.05f, 0.06f);
        var tape = new Color(0.30f, 0.18f, 0.10f);
        var hub = new Color(0.90f, 0.91f, 0.93f);

        DrawRect(new Rect2(Vector2.Zero, size), shell);
        DrawRect(new Rect2(Vector2.Zero, size), new Color(1, 1, 1, 0.12f), filled: false, width: 2f);
        // the label across the top, the window in its middle
        var lab = new Rect2(8, 8, size.X - 16, size.Y - 30);
        DrawRect(lab, label);
        DrawRect(new Rect2(lab.Position.X, lab.Position.Y + 6, lab.Size.X, 3), new Color(0.80f, 0.12f, 0.10f));
        var win = new Rect2(lab.Position.X + 18, lab.Position.Y + 16, lab.Size.X - 36, lab.Size.Y - 22);
        DrawRect(win, window);

        // the reels: the tape wound round each, left emptying into the right
        float cy = win.Position.Y + win.Size.Y * 0.5f;
        float l = win.Position.X + win.Size.X * 0.24f, r = win.Position.X + win.Size.X * 0.76f;
        float max = win.Size.Y * 0.5f - 2f, min = 7f;
        float p = Mathf.Clamp(Progress, 0f, 1f);
        DrawCircle(new Vector2(l, cy), Mathf.Lerp(max, min, p), tape);
        DrawCircle(new Vector2(r, cy), Mathf.Lerp(min, max, p), tape);
        Reel(new Vector2(l, cy), hub);
        Reel(new Vector2(r, cy), hub);

        // the screw holes and the head opening at the bottom
        DrawRect(new Rect2(size.X * 0.3f, size.Y - 16, size.X * 0.4f, 10), new Color(0.09f, 0.09f, 0.1f));
        DrawCircle(new Vector2(6, size.Y - 6), 2f, window);
        DrawCircle(new Vector2(size.X - 6, size.Y - 6), 2f, window);
    }

    /// <summary>A hub with three spokes, turned by the playing angle.</summary>
    private void Reel(Vector2 at, Color colour)
    {
        DrawArc(at, 6f, 0f, Mathf.Tau, 16, colour, 2f);
        for (int i = 0; i < 3; i++)
        {
            float a = _angle + i * Mathf.Tau / 3f;
            DrawLine(at, at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 6f, colour, 2f);
        }
    }
}
