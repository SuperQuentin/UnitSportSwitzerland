using System;
using Godot;

namespace UnitSport.Ui.Bubbles;

/// <summary>
/// A comic speech bubble (#862): a white body with a black outline round bold lettering, a tail
/// to the speaker (a spiky burst for a shout, a dashed outline for a whisper, a cloud and a trail
/// of circles for a thought). The text types itself in (<see cref="Reveal"/>) and the bubble
/// pops from its tail (<see cref="Pop"/>). Its own size is its body's; the tail is drawn outside
/// it, to the point given to <see cref="Point"/> in its own coordinates. Geometry is rebuilt only
/// when the text or the tail's end moves, into kept arrays: nothing is allocated per frame.
/// Used by the films' captions (<see cref="Trailer.Captions"/>) and the in-game chat
/// (<see cref="ChatBubbles"/>).
/// </summary>
public partial class SpeechBubble : Control
{
    /// <summary>Comic lettering: the comic faces the systems carry, bold; a plain bold face elsewhere.</summary>
    public static readonly SystemFont Lettering = new()
    {
        FontNames = ["Comic Neue", "Comic Sans MS", "Chalkboard SE", "Comic Relief", "Arial Rounded MT Bold", "Arial"],
        FontWeight = 700,
    };

    private static readonly Color Ink = new(0.06f, 0.06f, 0.07f);
    private static readonly Color Shadow = new(0f, 0f, 0f, 0.28f);

    private readonly Label _label;
    private readonly LabelSettings _settings = new() { Font = Lettering, FontColor = Ink };
    private BubbleShape _shape;
    private Color _fill = Colors.White;
    private float _font = 32f;
    private int _seed;
    private string _text = "";

    private readonly Vector2[] _scratch = new Vector2[BubbleGeometry.BodyPoints];
    private Vector2[] _body = [];
    private Vector2[] _bodyLoop = [];
    private Vector2[] _dashes = [];
    private readonly Vector2[] _tail = new Vector2[BubbleGeometry.TailPoints];
    private readonly (Vector2 At, float R)[] _trail = new (Vector2, float)[3];
    private int _trailCount;
    private bool _hasTail;
    private Vector2 _tip = new(float.NaN, float.NaN);
    private bool _off;
    /// <summary>The body's box in this control (the text sits in it; spikes and bumps reach out of it).</summary>
    private Rect2 _box;

    public SpeechBubble()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _label = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            LabelSettings = _settings,
            HorizontalAlignment = HorizontalAlignment.Center,
            // top: a label's height can lag its new width (its minimum size wraps at the old one)
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            // typing must not re-wrap the lines: letters are hidden after the text is shaped
            VisibleCharactersBehavior = TextServer.VisibleCharactersBehavior.CharsAfterShaping,
        };
        AddChild(_label);
    }

    /// <summary>The text's letters, for <see cref="Reveal"/>.</summary>
    public int Letters => _text.Length;

    /// <summary>
    /// Sets what it says and how: <paramref name="fontPx"/> is the lettering's size (a shout's is
    /// larger, a whisper's smaller), <paramref name="tint"/> washes the white a little toward a
    /// speaker's colour, <paramref name="seed"/> varies a shout's spikes. Sizes the bubble; call it
    /// once per line, not per frame.
    /// </summary>
    public void Set(string text, BubbleShape shape, float fontPx, Color? tint = null, int seed = 0)
    {
        // comic lettering is capitals; a whisper keeps its small letters
        _text = shape == BubbleShape.Whisper ? text : text.ToUpperInvariant();
        _shape = shape;
        _seed = seed;
        _font = fontPx * shape switch { BubbleShape.Shout => 1.18f, BubbleShape.Whisper => 0.9f, _ => 1f };
        _fill = tint is { } t ? Colors.White.Lerp(t, 0.16f) : Colors.White;
        _settings.FontSize = Mathf.RoundToInt(_font);
        _settings.FontColor = shape == BubbleShape.Whisper ? new Color(0.22f, 0.22f, 0.25f) : Ink;
        _settings.LineSpacing = -_font * 0.12f;
        _label.Text = _text;
        _label.VisibleCharacters = -1;

        // the text block: about as wide as it is tall in a long line, as a comic letterer would
        float wrap = _font * Mathf.Clamp(Mathf.Sqrt(_text.Length) * 1.9f, 8f, 15f);
        var size = Lettering.GetMultilineStringSize(_text, HorizontalAlignment.Center, wrap, _settings.FontSize,
            -1, TextServer.LineBreakFlag.Mandatory | TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.Adaptive);
        int lines = Math.Max(1, Mathf.RoundToInt(size.Y / Mathf.Max(1f, Lettering.GetHeight(_settings.FontSize))));
        size.Y += _settings.LineSpacing * (lines - 1);
        size.X += 2f;
        var body = BubbleGeometry.BodySize(shape, size, _font * 0.42f);
        int n = BubbleGeometry.Body(shape, new Rect2(Vector2.Zero, body), _font * 0.55f, seed, _scratch);
        // the control is the outline's bounds (spikes and bumps included), the body's box inside it
        var bounds = new Rect2(_scratch[0], Vector2.Zero);
        for (int i = 1; i < n; i++) bounds = bounds.Expand(_scratch[i]);
        _box = new Rect2(-bounds.Position, body);
        Size = bounds.Size;
        _label.Size = size;
        _label.Position = _box.Position + (body - size) * 0.5f;
        if (_body.Length != n)
        {
            _body = new Vector2[n];
            _bodyLoop = new Vector2[n + 1];
        }
        for (int i = 0; i < n; i++) _body[i] = _bodyLoop[i] = _scratch[i] - bounds.Position;
        _bodyLoop[n] = _body[0];
        if (shape == BubbleShape.Whisper)
        {
            Span<Vector2> dashes = stackalloc Vector2[1024];
            int d = BubbleGeometry.Dashes(_body, _font * 0.42f, _font * 0.26f, dashes);
            _dashes = dashes[..(d & ~1)].ToArray();
        }
        _tip = new Vector2(float.NaN, float.NaN);
        QueueRedraw();
    }

    /// <summary>
    /// Where the tail ends, in this control's coordinates (the speaker's head); redrawn only when
    /// it has moved by half a pixel. The bubble pops from the tail's root. A thought whose thinker
    /// is off the frame (<paramref name="off"/>) trails nowhere; a word said still points out of it.
    /// </summary>
    public void Point(Vector2 tip, bool off = false)
    {
        if (tip.DistanceSquaredTo(_tip) < 0.25f && off == _off) return;
        _tip = tip;
        _off = off;
        var box = _box;
        if (_shape == BubbleShape.Think)
        {
            _trailCount = off ? 0 : BubbleGeometry.Trail(box, tip, _font * 0.55f, _trail);
            _hasTail = false;
        }
        else
        {
            _trailCount = 0;
            _hasTail = BubbleGeometry.Tail(_shape, box, tip, _font * 0.9f, _tail);
        }
        PivotOffset = BubbleGeometry.Rim(box, tip, 0f);
        QueueRedraw();
    }

    /// <summary>Shows the first <paramref name="letters"/> letters (all with −1).</summary>
    public void Reveal(int letters)
    {
        int shown = letters >= _text.Length ? -1 : letters;
        if (_label.VisibleCharacters != shown) _label.VisibleCharacters = shown;
    }

    /// <summary>The pop's scale (<see cref="BubbleGeometry.Pop"/>) and how opaque it is.</summary>
    public void Pop(float scale, float alpha)
    {
        var s = new Vector2(scale, scale);
        if (Scale != s) Scale = s;
        var m = new Color(1, 1, 1, alpha);
        if (Modulate != m) Modulate = m;
    }

    public override void _Draw()
    {
        if (_body.Length == 0) return;
        float ink = Mathf.Max(2f, _font * 0.12f);
        // a soft offset shadow under it all, so a white bubble holds on snow and sky
        DrawSetTransform(new Vector2(ink * 0.9f, ink * 1.4f));
        DrawColoredPolygon(_body, Shadow);
        if (_hasTail) DrawColoredPolygon(_tail, Shadow);
        for (int i = 0; i < _trailCount; i++) DrawCircle(_trail[i].At, _trail[i].R, Shadow);
        DrawSetTransform(Vector2.Zero);

        // the outlines first, twice as wide as they show: the fills then cover their inner half,
        // and where the tail meets the body each fill hides the other's line
        if (_shape == BubbleShape.Whisper) DrawMultiline(_dashes, Ink, ink * 1.6f, true);
        else DrawPolyline(_bodyLoop, Ink, ink * 2f, true);
        if (_hasTail) DrawPolyline(_tail, Ink, ink * 2f, true);
        for (int i = 0; i < _trailCount; i++) DrawCircle(_trail[i].At, _trail[i].R + ink, Ink);

        DrawColoredPolygon(_body, _fill);
        if (_hasTail) DrawColoredPolygon(_tail, _fill);
        for (int i = 0; i < _trailCount; i++) DrawCircle(_trail[i].At, _trail[i].R, _fill);
    }
}
