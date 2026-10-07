using System.Collections.Generic;
using Godot;

namespace UnitSport.Trailer;

/// <summary>
/// The trailer's own layer over the picture: captions low in the frame, title cards in the middle,
/// and a black veil for fades. Sizes follow the window's height, so a 720p preview and a 1080p film
/// frame alike. The only layer left visible while the director hides the game's HUD.
/// </summary>
public partial class Captions : CanvasLayer
{
    private readonly Label _text, _sub, _title, _titleSub;
    private readonly ColorRect _veil;
    private readonly VBoxContainer _low, _middle;
    private static readonly SystemFont Heavy = new()
    {
        FontNames = ["Montserrat Black", "Arial Black", "Segoe UI Black", "Helvetica Neue", "Arial"],
        FontWeight = 900,
    };
    private static readonly SystemFont Light = new()
    {
        FontNames = ["Montserrat", "Segoe UI", "Helvetica Neue", "Arial"],
        FontWeight = 500,
    };

    public Captions()
    {
        Name = "TrailerCaptions";
        Layer = 120;
        _veil = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        _veil.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_veil);

        _low = Column(Control.LayoutPreset.CenterBottom);
        _text = Line(_low, Heavy);
        _sub = Line(_low, Light);
        _middle = Column(Control.LayoutPreset.Center);
        _title = Line(_middle, Heavy);
        _titleSub = Line(_middle, Light);
    }

    private VBoxContainer Column(Control.LayoutPreset where)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(box);
        return box;
    }

    private static Label Line(Container box, Font font)
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = new LabelSettings
            {
                Font = font,
                FontColor = Colors.White,
                ShadowColor = new Color(0, 0, 0, 0.55f),
                ShadowSize = 6,
                ShadowOffset = new Vector2(0, 3),
            },
        };
        box.AddChild(label);
        return label;
    }

    /// <summary>
    /// The layer at shot time <paramref name="t"/> of a shot <paramref name="length"/> s long:
    /// which caption shows and how faded in it is, and how black the frame is.
    /// </summary>
    public void Show(IReadOnlyList<Caption> captions, double t, double length, double fadeIn, double fadeOut)
    {
        float h = GetViewport().GetVisibleRect().Size.Y;
        _text.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.062f);
        _sub.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.026f);
        _title.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.105f);
        _titleSub.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.03f);
        _low.OffsetBottom = -h * 0.09f;
        _low.Alignment = BoxContainer.AlignmentMode.End;

        Caption? low = null, mid = null;
        foreach (var c in captions)
            if (t >= c.T && t < c.T + c.Seconds)
            {
                if (c.Title) mid = c;
                else low = c;
            }
        Put(low, _text, _sub, t);
        Put(mid, _title, _titleSub, t);

        float black = 0f;
        if (fadeIn > 0 && t < fadeIn) black = 1f - (float)(t / fadeIn);
        if (fadeOut > 0 && t > length - fadeOut) black = Mathf.Max(black, (float)((t - (length - fadeOut)) / fadeOut));
        _veil.Color = new Color(0, 0, 0, Mathf.Clamp(black, 0f, 1f));
    }

    private static void Put(Caption? c, Label text, Label sub, double t)
    {
        if (c == null)
        {
            text.Visible = sub.Visible = false;
            return;
        }
        // in and out over a quarter of a second, the line rising a little as it comes
        double age = t - c.T, left = c.T + c.Seconds - t;
        float alpha = Mathf.Clamp((float)Mathf.Min(age / 0.25, left / 0.25), 0f, 1f);
        text.Visible = true;
        text.Text = c.Text;
        text.Modulate = new Color(1, 1, 1, alpha);
        sub.Visible = c.Sub != null;
        sub.Text = c.Sub ?? "";
        sub.Modulate = new Color(1, 1, 1, alpha * 0.9f);
    }

    /// <summary>Nothing on the picture (between shots, while the next one stages).</summary>
    public void Clear(bool black)
    {
        _text.Visible = _sub.Visible = _title.Visible = _titleSub.Visible = false;
        _veil.Color = new Color(0, 0, 0, black ? 1f : 0f);
    }
}
