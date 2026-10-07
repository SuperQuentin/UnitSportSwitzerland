using System.Collections.Generic;
using Godot;

namespace UnitSport.Trailer;

/// <summary>
/// The trailer's own layer over the picture: captions low in the frame, title cards in the middle,
/// the friends' chat bottom left (the story's dialogue, typed in), place and time cards top left,
/// a photo's flash and the polaroid it leaves, and a black veil for fades. Sizes follow the
/// frame's height, so a 720p preview and a 1080p film frame alike. The only layer left visible
/// while the director hides the game's HUD.
/// </summary>
public partial class Captions : CanvasLayer
{
    private readonly Label _text, _sub, _title, _titleSub, _super;
    private readonly ColorRect _veil, _flash, _superBar;
    private readonly VBoxContainer _low, _middle, _chat;
    private readonly PanelContainer _chatPanel;
    private readonly Control _photo;
    private readonly Panel _photoCard;
    private readonly TextureRect _photoImage;
    private readonly List<(Line Line, RichTextLabel Label)> _lines = new();

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
    private static readonly SystemFont Semi = new()
    {
        FontNames = ["Montserrat SemiBold", "Segoe UI Semibold", "Helvetica Neue", "Arial"],
        FontWeight = 600,
    };
    private static readonly FontVariation Spaced = new() { BaseFont = Semi, SpacingGlyph = 3 };

    /// <summary>The system line's colour, as the game's chat writes its own messages.</summary>
    private static readonly Color System = new(1f, 0.82f, 0.35f);

    public Captions()
    {
        Name = "TrailerCaptions";
        Layer = 120;

        _photo = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _photo.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_photo);
        _photoCard = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        _photoCard.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.97f, 0.96f, 0.93f),
            ShadowColor = new Color(0, 0, 0, 0.45f),
            ShadowSize = 18,
            ShadowOffset = new Vector2(6, 10),
        });
        _photo.AddChild(_photoCard);
        _photoImage = new TextureRect
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        };
        _photoCard.AddChild(_photoImage);

        _veil = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        _veil.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_veil);

        _low = Column();
        _text = Text(_low, Heavy);
        _sub = Text(_low, Light);
        _middle = Column();
        _title = Text(_middle, Heavy);
        _titleSub = Text(_middle, Light);

        _superBar = new ColorRect { Color = new Color(1f, 0.82f, 0.35f), MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_superBar);
        _super = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, LabelSettings = Settings(Spaced) };
        AddChild(_super);

        _chatPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _chatPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.42f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        AddChild(_chatPanel);
        _chat = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _chatPanel.AddChild(_chat);

        _flash = new ColorRect { Color = new Color(1, 1, 1, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_flash);
    }

    private VBoxContainer Column()
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(box);
        return box;
    }

    private static LabelSettings Settings(Font font) => new()
    {
        Font = font,
        FontColor = Colors.White,
        OutlineColor = new Color(0, 0, 0, 0.45f),
        OutlineSize = 6,
        ShadowColor = new Color(0, 0, 0, 0.5f),
        ShadowSize = 10,
        ShadowOffset = new Vector2(0, 3),
    };

    private static Label Text(Container box, Font font)
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = Settings(font),
        };
        box.AddChild(label);
        return label;
    }

    /// <summary>A shot's chat, built once as it starts: one line each, hidden until it is typed.</summary>
    public void SetChat(IReadOnlyList<Line> lines)
    {
        foreach (var (_, label) in _lines) label.QueueFree();
        _lines.Clear();
        foreach (var line in lines)
        {
            var label = new RichTextLabel
            {
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible = false,
                Text = line.Who is { } who
                    ? $"[b][color=#{who.Colour.ToHtml(false)}]{who.Name}[/color][/b]  {Escape(line.Text)}"
                    : $"[i][color=#{System.ToHtml(false)}]{Escape(line.Text)}[/color][/i]",
            };
            label.AddThemeFontOverride("normal_font", Semi);
            label.AddThemeFontOverride("bold_font", Heavy);
            label.AddThemeFontOverride("italics_font", Semi);
            label.AddThemeColorOverride("default_color", Colors.White);
            label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.5f));
            label.AddThemeConstantOverride("outline_size", 4);
            _chat.AddChild(label);
            _lines.Add((line, label));
        }
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");

    /// <summary>
    /// The layer at shot time <paramref name="t"/> of a shot <paramref name="length"/> s long:
    /// which caption, chat lines and card show, the photo, and how black the frame is.
    /// </summary>
    public void Show(Shot shot, double t, Texture2D? photo)
    {
        float h = GetViewport().GetVisibleRect().Size.Y, w = GetViewport().GetVisibleRect().Size.X;
        _text.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.062f);
        _sub.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.026f);
        _title.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.105f);
        _titleSub.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.03f);
        _low.OffsetTop = 0;
        _low.OffsetBottom = -h * 0.09f;
        _low.Alignment = BoxContainer.AlignmentMode.End;

        Caption? low = null, mid = null;
        foreach (var c in shot.Captions)
            if (t >= c.T && t < c.T + c.Seconds)
            {
                if (c.Title) mid = c;
                else low = c;
            }
        Put(low, _text, _sub, t);
        Put(mid, _title, _titleSub, t);

        ShowSuper(shot, t, w, h);
        ShowChat(t, w, h);
        ShowPhoto(shot, t, photo, w, h);

        float black = 0f;
        double length = shot.Length;
        if (shot.FadeIn > 0 && t < shot.FadeIn) black = 1f - (float)(t / shot.FadeIn);
        if (shot.FadeOut > 0 && t > length - shot.FadeOut) black = Mathf.Max(black, (float)((t - (length - shot.FadeOut)) / shot.FadeOut));
        _veil.Color = new Color(0, 0, 0, Mathf.Clamp(black, 0f, 1f));
    }

    /// <summary>The place and time card: top left, a short amber bar beside it, in and out in a quarter second.</summary>
    private void ShowSuper(Shot shot, double t, float w, float h)
    {
        Super? card = null;
        foreach (var s in shot.Supers)
            if (t >= s.T && t < s.T + s.Seconds) card = s;
        _super.Visible = _superBar.Visible = card != null;
        if (card == null) return;
        float alpha = Mathf.Clamp((float)Mathf.Min((t - card.T) / 0.25, (card.T + card.Seconds - t) / 0.25), 0f, 1f);
        _super.LabelSettings.FontSize = Mathf.RoundToInt(h * 0.03f);
        _super.Text = card.Text;
        _super.Position = new Vector2(w * 0.045f + h * 0.016f, h * 0.06f);
        _super.Modulate = new Color(1, 1, 1, alpha);
        _superBar.Position = new Vector2(w * 0.045f, h * 0.064f);
        _superBar.Size = new Vector2(h * 0.006f, h * 0.034f);
        _superBar.Modulate = new Color(1, 1, 1, alpha);
    }

    /// <summary>The chat: each line types itself in from its time (a character every 35 ms) and stays.</summary>
    private void ShowChat(double t, float w, float h)
    {
        bool any = false;
        foreach (var (line, label) in _lines)
        {
            bool on = t >= line.T;
            label.Visible = on;
            if (!on) continue;
            any = true;
            label.AddThemeFontSizeOverride("normal_font_size", Mathf.RoundToInt(h * 0.027f));
            label.AddThemeFontSizeOverride("bold_font_size", Mathf.RoundToInt(h * 0.027f));
            label.AddThemeFontSizeOverride("italics_font_size", Mathf.RoundToInt(h * 0.027f));
            int letters = label.GetTotalCharacterCount();
            label.VisibleCharacters = Mathf.Min(letters, (int)((t - line.T) / 0.035) + 1);
        }
        _chatPanel.Visible = any;
        if (!any) return;
        _chatPanel.ResetSize();
        _chatPanel.Position = new Vector2(w * 0.035f, h * 0.93f - _chatPanel.Size.Y);
    }

    /// <summary>A photo: a white flash that fades in half a second, then the frame as a polaroid, a little askew.</summary>
    private void ShowPhoto(Shot shot, double t, Texture2D? photo, float w, float h)
    {
        bool taken = shot.Photo is { } at && t >= at;
        float flash = taken ? Mathf.Clamp(1f - (float)((t - shot.Photo!.Value) / 0.5), 0f, 1f) : 0f;
        _flash.Color = new Color(1, 1, 1, flash);
        _photo.Visible = taken && photo != null;
        if (!_photo.Visible) { _middle.Alignment = BoxContainer.AlignmentMode.Center; _middle.OffsetBottom = 0; return; }

        // the polaroid settles in over the first second: from a little larger and straighter
        float settle = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((float)((t - shot.Photo!.Value) / 1.0), 0f, 1f));
        float imageH = h * Mathf.Lerp(0.6f, 0.5f, settle), imageW = imageH * w / h;
        float side = imageH * 0.05f, bottom = imageH * 0.2f;
        _photoImage.Texture = photo;
        _photoImage.Position = new Vector2(side, side);
        _photoImage.Size = new Vector2(imageW, imageH);
        _photoCard.Size = new Vector2(imageW + 2 * side, imageH + side + bottom);
        _photoCard.PivotOffset = _photoCard.Size / 2;
        _photoCard.Position = new Vector2(w * 0.5f, h * 0.44f) - _photoCard.Size / 2;
        _photoCard.Rotation = Mathf.DegToRad(Mathf.Lerp(0f, -3.5f, settle));
        // the title goes under the photo, a low line (the credit) over it
        _middle.Alignment = BoxContainer.AlignmentMode.End;
        _middle.OffsetBottom = -h * 0.035f;
        _low.Alignment = BoxContainer.AlignmentMode.Begin;
        _low.OffsetTop = h * 0.012f;
        _low.OffsetBottom = 0;
    }

    private static void Put(Caption? c, Label text, Label sub, double t)
    {
        if (c == null)
        {
            text.Visible = sub.Visible = false;
            return;
        }
        // in and out over a quarter of a second
        double age = t - c.T, left = c.T + c.Seconds - t;
        float alpha = Mathf.Clamp((float)Mathf.Min(age / 0.25, left / 0.25), 0f, 1f);
        text.Visible = c.Text.Length > 0;
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
        _super.Visible = _superBar.Visible = _chatPanel.Visible = _photo.Visible = false;
        _flash.Color = new Color(1, 1, 1, 0);
        _veil.Color = new Color(0, 0, 0, black ? 1f : 0f);
        SetChat([]);
    }
}
