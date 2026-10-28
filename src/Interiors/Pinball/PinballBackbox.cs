using Godot;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// A backbox's score display in its table's era (#1325), seen by everyone around: score reels behind the
/// lit backglass (Clockwork), 7-segment and alphanumeric glass (Space Mission), the orange 128 x 32 dot
/// matrix (Haunted Castle), a race-timing LCD (Alpine Ski Race). The picture is drawn on the CPU into one
/// small image only when what it shows changes (<see cref="Show"/>), through one shader.
/// </summary>
public partial class PinballBackbox : MeshInstance3D
{
    private static Shader? _shader;
    private PinballTable _table = null!;
    private Image _image = null!;
    private ImageTexture _texture = null!;
    private int _w, _h;
    private string _shown = "";
    private double _blinkAt;

    /// <summary>The display's size on the backglass [m].</summary>
    public Vector2 Size { get; private set; }

    public static PinballBackbox Create(PinballTable t)
    {
        var (w, h, mode, size) = t.Era switch
        {
            PinballEra.Reels => (160, 64, 0, new Vector2(0.44f, 0.176f)),
            PinballEra.Segments => (192, 48, 2, new Vector2(0.46f, 0.115f)),
            PinballEra.DotMatrix => (128, 32, 1, new Vector2(0.40f, 0.10f)),
            _ => (256, 96, 0, new Vector2(0.44f, 0.165f)),
        };
        _shader ??= GD.Load<Shader>("res://shaders/pinball_display.gdshader");
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        var texture = ImageTexture.CreateFromImage(image);
        var mat = new ShaderMaterial { Shader = _shader };
        mat.SetShaderParameter("screen", texture);
        mat.SetShaderParameter("res", new Vector2(w, h));
        mat.SetShaderParameter("mode", mode);
        var box = new PinballBackbox
        {
            Name = "Display", _table = t, _image = image, _texture = texture, _w = w, _h = h, Size = size,
            Mesh = new QuadMesh { Size = size }, MaterialOverride = mat, CastShadow = ShadowCastingSetting.Off,
        };
        box.Show(PinballScreen.Idle(t));
        return box;
    }

    /// <summary>Draws <paramref name="s"/> if the backbox would look any different; cheap to call every frame.</summary>
    public void Show(PinballScreen s, double now = 0)
    {
        // the attract mode's text alternates every few seconds
        bool blink = s.Phase != PinballRules.Phase.Playing && (int)(now / 3.0) % 2 == 1;
        string key = s.DisplayKey() + blink;
        if (key == _shown) return;
        _shown = key;
        _image.Fill(Colors.Black);
        switch (_table.Era)
        {
            case PinballEra.Reels: Reels(s, blink); break;
            case PinballEra.Segments: Segments(s, blink); break;
            case PinballEra.DotMatrix: Dots(s, blink); break;
            default: Lcd(s, blink); break;
        }
        _texture.Update(_image);
    }

    private void Px(int x, int y, Color c)
    {
        if (x >= 0 && y >= 0 && x < _w && y < _h) _image.SetPixel(x, y, c);
    }

    private void Rect(int x0, int y0, int x1, int y1, Color c)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(_h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(_w, x1); x++) _image.SetPixel(x, y, c);
    }

    private void Text(string text, int x, int y, int scale, Color c) => PinballFont.Draw(text, x, y, scale, (px, py) => Px(px, py, c));

    private void Centered(string text, int y, int scale, Color c) => Text(text, (_w - PinballFont.Width(text, scale)) / 2, y, scale, c);

    private static string Score(long v) => PinballRules.Fmt(v);

    private string Headline(PinballScreen s, bool blink) => s.Phase switch
    {
        PinballRules.Phase.Attract => blink ? "PRESS START" : _table.Title.ToUpperInvariant(),
        PinballRules.Phase.GameOver => blink && s.Match >= 0 ? $"MATCH {s.Match:00}" : "GAME OVER",
        _ => s.Message.Length > 0 ? s.Message : s.Mission,
    };

    // ---- electro-mechanical: four score reels, the backglass's lit words -----------------------

    private void Reels(PinballScreen s, bool blink)
    {
        var cream = new Color(0.93f, 0.88f, 0.74f);
        var ink = new Color(0.08f, 0.06f, 0.05f);
        for (int p = 0; p < 4; p++)
        {
            int wx = 6 + p % 2 * 78, wy = 4 + p / 2 * 22;
            long v = p < s.Scores.Length ? s.Scores[p] : 0;
            string digits = (v % 10_000_000).ToString("0000000");
            for (int d = 0; d < 7; d++)
            {
                int cx = wx + d * 10;
                // the drum: brightest in the middle of its window, shading to the edges
                for (int y = 0; y < 17; y++)
                {
                    float shade = 0.45f + 0.55f * Mathf.Sin(Mathf.Pi * (y + 0.5f) / 17f);
                    Rect(cx, wy + y, cx + 9, wy + y + 1, cream * shade);
                }
                Text(digits[d].ToString(), cx + 2, wy + 5, 1, p < s.Scores.Length || p == 0 ? ink : ink * 0.6f + cream * 0.4f);
            }
            // the player up's window is lit round its edge
            if (s.Phase == PinballRules.Phase.Playing && p == s.Up)
            {
                Rect(wx - 2, wy - 2, wx + 70, wy - 1, new Color(1f, 0.7f, 0.2f));
                Rect(wx - 2, wy + 18, wx + 70, wy + 19, new Color(1f, 0.7f, 0.2f));
            }
        }
        // the backglass's lamps: lit or dark words
        Color On(bool on) => on ? new Color(1f, 0.82f, 0.35f) : new Color(0.16f, 0.12f, 0.08f);
        Text($"BALL {Math.Max(1, s.Ball)}", 6, 48, 1, On(s.Phase == PinballRules.Phase.Playing));
        Text("TILT", 52, 48, 1, On(s.Tilt));
        Text("GAME OVER", 90, 48, 1, On(s.Phase != PinballRules.Phase.Playing));
        Text(s.Match >= 0 ? $"MATCH {s.Match:00}" : "MATCH 00", 6, 57, 1, On(s.Match >= 0 && s.Phase == PinballRules.Phase.GameOver));
        Text("SAME PLAYER SHOOTS", 52, 57, 1, On(s.Saving));
    }

    // ---- solid state: an alphanumeric line and four 7-segment scores ----------------------------

    private void Segments(PinballScreen s, bool blink)
    {
        var lit = new Color(1f, 0.42f, 0.10f);
        var ghost = new Color(0.10f, 0.04f, 0.02f);
        // the alphanumeric line: 16 characters with their unlit segments behind
        string line = Headline(s, blink);
        if (line.Length == 0) line = Score(s.ScoreUp);
        line = line.Length > 16 ? line[..16] : line.PadLeft((16 + line.Length) / 2).PadRight(16);
        for (int i = 0; i < 16; i++)
        {
            int x = 4 + i * 11;
            Rect(x, 3, x + 9, 21, ghost);
            PinballFont.Draw(line[i].ToString(), x + 0, 4, 2, (px, py) => Px(px - 1 + (py - 4) / 6, py, lit));
        }
        // four players' scores in 7-segment digits
        for (int p = 0; p < 4; p++)
        {
            string digits = p < s.Scores.Length ? s.Scores[p].ToString().PadLeft(7) : "       ";
            if (digits.Length > 7) digits = digits[^7..];
            int x0 = 4 + p * 47;
            bool up = s.Phase == PinballRules.Phase.Playing && p == s.Up;
            for (int d = 0; d < 7; d++) Seven(x0 + d * 6, 28, digits[d], up ? lit : lit * 0.75f, ghost);
        }
    }

    /// <summary>One 7-segment digit, 5 x 11 pixels.</summary>
    private void Seven(int x, int y, char c, Color on, Color off)
    {
        int seg = PinballFont.Segments(c);
        Color S(int b) => (seg >> b & 1) != 0 ? on : off;
        Rect(x + 1, y, x + 4, y + 1, S(0));
        Rect(x + 4, y + 1, x + 5, y + 5, S(1));
        Rect(x + 4, y + 6, x + 5, y + 10, S(2));
        Rect(x + 1, y + 10, x + 4, y + 11, S(3));
        Rect(x, y + 6, x + 1, y + 10, S(4));
        Rect(x, y + 1, x + 1, y + 5, S(5));
        Rect(x + 1, y + 5, x + 4, y + 6, S(6));
    }

    // ---- the 90s: an orange 128 x 32 dot-matrix --------------------------------------------------

    private void Dots(PinballScreen s, bool blink)
    {
        var orange = new Color(1f, 0.45f, 0.08f);
        var dim = orange * 0.35f;
        string head = Headline(s, blink);
        if (s.Phase == PinballRules.Phase.Playing && s.Message.Length == 0)
        {
            // the score big in the middle, the ball and the mission under it
            Centered(Score(s.ScoreUp), 3, 2, orange);
            string under = s.Mission.Length > 0 ? s.Mission : $"BALL {s.Ball}" + (s.Scores.Length > 1 ? $"   PLAYER {s.Up + 1}" : "");
            Centered(under.Length > 21 ? under[..21] : under, 22, 1, dim);
            return;
        }
        if (head.Length <= 10) Centered(head, 3, 2, orange);
        else Centered(head.Length > 21 ? head[..21] : head, 8, 1, orange);
        if (s.Scores.Length > 0)
        {
            string scores = string.Join("  ", s.Scores.Select(Score));
            Centered(scores.Length > 21 ? scores[..21] : scores, 23, 1, dim);
        }
    }

    // ---- today: a race-timing LCD ----------------------------------------------------------------

    private void Lcd(PinballScreen s, bool blink)
    {
        var navy = new Color(0.02f, 0.08f, 0.25f);
        var red = new Color(0.85f, 0.08f, 0.10f);
        var white = Colors.White;
        var snow = new Color(0.80f, 0.90f, 1f);
        Rect(0, 0, _w, _h, navy);
        // the timing bar: a red band with the run's name, a white board for the score
        Rect(0, 0, _w, 16, red);
        Text(_table.Title.ToUpperInvariant(), 4, 4, 1, white);
        string ball = s.Phase == PinballRules.Phase.Playing ? $"RUN {s.Ball}/3" : "";
        Text(ball, _w - 4 - PinballFont.Width(ball), 4, 1, white);
        // mountains along the bottom
        for (int x = 0; x < _w; x++)
        {
            int peak = 82 - (int)(10 * Mathf.Abs(Mathf.Sin(x * 0.045f)) + 6 * Mathf.Abs(Mathf.Sin(x * 0.11f + 1)));
            Rect(x, peak, x + 1, _h, snow * 0.55f);
            Rect(x, Math.Max(peak, 90), x + 1, _h, snow);
        }
        string head = Headline(s, blink);
        if (s.Phase == PinballRules.Phase.Playing)
        {
            Rect(8, 22, _w - 8, 54, new Color(0.95f, 0.97f, 1f));
            string score = Score(s.ScoreUp);
            Text(score, (_w - PinballFont.Width(score, 3)) / 2, 27, 3, navy);
            string line = s.Message.Length > 0 ? s.Message : s.Mission.Length > 0 ? s.Mission : s.Scores.Length > 1 ? $"PLAYER {s.Up + 1}" : "";
            if (line.Length > 40) line = line[..40];
            Rect(8, 58, _w - 8, 70, red);
            Text(line, (_w - PinballFont.Width(line)) / 2, 60, 1, white);
            return;
        }
        Text(head, (_w - PinballFont.Width(head, 2)) / 2, 26, 2, white);
        for (int p = 0; p < s.Scores.Length; p++)
        {
            string row = $"{p + 1}. {Score(s.Scores[p])}";
            Text(row, 20 + p % 2 * 120, 48 + p / 2 * 10, 1, white);
        }
    }
}
