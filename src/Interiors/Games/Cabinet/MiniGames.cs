using Godot;

namespace UnitSport.Interiors.Games.Cabinet;

/// <summary>Where a cabinet's game draws (#1326): a small screen of <see cref="MiniGame.Width"/> x <see cref="MiniGame.Height"/> pixels.</summary>
public interface IScreen
{
    void Clear(Color c);
    void Rect(int x, int y, int w, int h, Color c);
    /// <summary>Text in the 5 x 7 pixel font, <paramref name="scale"/> times, from its top-left corner.</summary>
    void Text(string text, int x, int y, int scale, Color c);
}

/// <summary>The cabinet's controls this frame: the stick, the button, the four arrows (a rhythm game's lanes).</summary>
public readonly record struct MiniInput(float X, float Y, bool Button, bool Left, bool Down, bool Up, bool Right);

/// <summary>
/// One of the cabinets' original small games (#1326), pure: stepped by the player's client while a Batzen's game lasts,
/// drawn into the cabinet's screen. A game ends on its own (lives gone, the song over) with a score.
/// </summary>
public abstract class MiniGame
{
    public const int Width = 160, Height = 120;
    public int Score { get; protected set; }
    public bool Over { get; protected set; }
    public abstract string Title { get; }
    public abstract void Step(float dt, MiniInput input);
    public abstract void Draw(IScreen s);

    /// <summary>The cabinet's kinds: what each machine plays.</summary>
    public enum Kind : byte { Shooter, Racer, Rhythm }

    public static MiniGame Make(Kind kind, int seed) => kind switch
    {
        Kind.Racer => new ColRush(seed),
        Kind.Rhythm => new YodelBeat(seed),
        _ => new SnowballPatrol(seed),
    };

    public static string TitleOf(Kind kind) => kind switch { Kind.Racer => "COL RUSH", Kind.Rhythm => "YODEL BEAT", _ => "SNOWBALL PATROL" };

    protected static void Hud(IScreen s, int score, string right)
    {
        s.Text($"{score:000000}", 2, 2, 1, new Color(1f, 1f, 1f));
        s.Text(right, Width - 2 - (right.Length * 6 - 1), 2, 1, new Color(1f, 0.85f, 0.2f));
    }
}

/// <summary>
/// The shooter (#1326): a snowcat along the bottom fires snowballs up at rows of marmot-shaped invaders that sweep side to
/// side and step down, dropping acorns. Three snowcats; a cleared wave comes back faster.
/// </summary>
public sealed class SnowballPatrol : MiniGame
{
    private const int Cols = 8, Rows = 4;
    private readonly Random _rng;
    private float _x = Width / 2f, _cool, _sweep = 1, _speed = 14, _drop;
    private readonly bool[,] _alive = new bool[Cols, Rows];
    private Vector2 _grid = new(16, 18);
    private readonly List<Vector2> _shots = new(), _acorns = new();
    private int _lives = 3, _wave = 1;
    private float _hurt;

    public override string Title => TitleOf(Kind.Shooter);

    public SnowballPatrol(int seed)
    {
        _rng = new Random(seed);
        Wave();
    }

    private void Wave()
    {
        for (int c = 0; c < Cols; c++) for (int r = 0; r < Rows; r++) _alive[c, r] = true;
        _grid = new Vector2(16, 18);
        _speed = 12 + 4 * _wave;
    }

    public override void Step(float dt, MiniInput input)
    {
        if (Over) return;
        _x = Mathf.Clamp(_x + input.X * 90 * dt, 8, Width - 8);
        _cool -= dt;
        _hurt -= dt;
        if (input.Button && _cool <= 0 && _shots.Count < 3) { _shots.Add(new Vector2(_x, Height - 14)); _cool = 0.3f; }
        for (int i = _shots.Count - 1; i >= 0; i--)
        {
            _shots[i] += new Vector2(0, -150 * dt);
            if (_shots[i].Y < 10) { _shots.RemoveAt(i); continue; }
            if (Hit(_shots[i])) _shots.RemoveAt(i);
        }
        // the grid sweeps; at an edge it turns and steps down
        _grid.X += _sweep * _speed * dt;
        float left = float.MaxValue, right = float.MinValue, bottom = 0;
        for (int c = 0; c < Cols; c++) for (int r = 0; r < Rows; r++)
            if (_alive[c, r]) { left = Mathf.Min(left, _grid.X + c * 16); right = Mathf.Max(right, _grid.X + c * 16 + 10); bottom = Mathf.Max(bottom, _grid.Y + r * 12 + 8); }
        if (left == float.MaxValue) { _wave++; Score += 500; Wave(); return; }
        if (right > Width - 4 && _sweep > 0 || left < 4 && _sweep < 0) { _sweep = -_sweep; _grid.Y += 5; }
        if (bottom > Height - 18) { _lives = 0; Over = true; return; }
        _drop -= dt;
        if (_drop <= 0)
        {
            _drop = Mathf.Max(0.35f, 1.3f - 0.15f * _wave) * (0.6f + (float)_rng.NextDouble() * 0.8f);
            int c = _rng.Next(Cols);
            for (int r = Rows - 1; r >= 0; r--)
                if (_alive[c, r]) { _acorns.Add(new Vector2(_grid.X + c * 16 + 5, _grid.Y + r * 12 + 8)); break; }
        }
        for (int i = _acorns.Count - 1; i >= 0; i--)
        {
            _acorns[i] += new Vector2(0, 60 * dt);
            if (_acorns[i].Y > Height) { _acorns.RemoveAt(i); continue; }
            if (_hurt <= 0 && Mathf.Abs(_acorns[i].X - _x) < 7 && _acorns[i].Y > Height - 14)
            {
                _acorns.RemoveAt(i);
                _hurt = 1.5f;
                if (--_lives <= 0) Over = true;
            }
        }
    }

    private bool Hit(Vector2 shot)
    {
        for (int c = 0; c < Cols; c++) for (int r = 0; r < Rows; r++)
        {
            if (!_alive[c, r]) continue;
            float x = _grid.X + c * 16, y = _grid.Y + r * 12;
            if (shot.X >= x && shot.X <= x + 10 && shot.Y >= y && shot.Y <= y + 8)
            {
                _alive[c, r] = false;
                Score += 10 * (Rows - r);
                return true;
            }
        }
        return false;
    }

    public override void Draw(IScreen s)
    {
        s.Clear(new Color(0.02f, 0.03f, 0.10f));
        // the far peaks
        for (int i = 0; i < 6; i++) s.Rect(i * 30, Height - 26 + (i % 2) * 4, 22, 26, new Color(0.10f, 0.12f, 0.25f));
        s.Rect(0, Height - 6, Width, 6, new Color(0.85f, 0.88f, 0.95f));
        var fur = new Color(0.70f, 0.45f, 0.20f);
        for (int c = 0; c < Cols; c++) for (int r = 0; r < Rows; r++)
        {
            if (!_alive[c, r]) continue;
            int x = (int)(_grid.X + c * 16), y = (int)(_grid.Y + r * 12);
            s.Rect(x + 1, y + 2, 8, 6, fur);
            s.Rect(x, y, 3, 3, fur);
            s.Rect(x + 7, y, 3, 3, fur);
            s.Rect(x + 3, y + 3, 1, 1, Colors.Black);
            s.Rect(x + 6, y + 3, 1, 1, Colors.Black);
        }
        foreach (var a in _acorns) s.Rect((int)a.X - 1, (int)a.Y, 3, 4, new Color(0.55f, 0.35f, 0.15f));
        foreach (var b in _shots) s.Rect((int)b.X - 1, (int)b.Y, 3, 3, Colors.White);
        if (_hurt <= 0 || (int)(_hurt * 10) % 2 == 0)
        {
            int x = (int)_x;
            s.Rect(x - 7, Height - 12, 14, 5, new Color(0.95f, 0.20f, 0.15f));
            s.Rect(x - 2, Height - 15, 4, 3, new Color(0.95f, 0.20f, 0.15f));
            s.Rect(x - 7, Height - 7, 14, 2, new Color(0.15f, 0.15f, 0.15f));
        }
        Hud(s, Score, $"x{Math.Max(0, _lives)} W{_wave}");
    }
}

/// <summary>
/// The racer (#1326): a pass road winds down the screen; the stick steers, forward goes faster. Slower cars to pass, the
/// verge slows you; three crashes and it is over. The score is the distance.
/// </summary>
public sealed class ColRush : MiniGame
{
    private readonly Random _rng;
    private float _x = Width / 2f, _speed = 60, _dist, _hurt;
    private int _lives = 3;
    private readonly List<Vector2> _cars = new();
    private float _spawn = 1;

    public override string Title => TitleOf(Kind.Racer);

    public ColRush(int seed) => _rng = new Random(seed);

    /// <summary>The road's middle at screen row <paramref name="y"/>: a winding pass, scrolling with the distance.</summary>
    private float Middle(float y) => Width / 2f + 34 * Mathf.Sin((_dist + (Height - y)) * 0.012f) + 14 * Mathf.Sin((_dist + (Height - y)) * 0.031f);
    private const float Half = 26;

    public override void Step(float dt, MiniInput input)
    {
        if (Over) return;
        float want = 60 + 70 * Mathf.Clamp(input.Y, 0, 1);
        _speed = Mathf.MoveToward(_speed, want, 40 * dt);
        _x = Mathf.Clamp(_x + input.X * 80 * dt, 6, Width - 6);
        float off = Mathf.Abs(_x - Middle(Height - 16)) - Half + 5;
        if (off > 0) _speed = Mathf.MoveToward(_speed, 35, 120 * dt);
        _dist += _speed * dt;
        Score = (int)(_dist / 2);
        _hurt -= dt;
        _spawn -= dt;
        if (_spawn <= 0)
        {
            _spawn = 1.6f + (float)_rng.NextDouble() * 1.5f - Mathf.Min(1.0f, _dist / 6000);
            _cars.Add(new Vector2((float)(_rng.NextDouble() * 2 - 1) * (Half - 6), -10));
        }
        for (int i = _cars.Count - 1; i >= 0; i--)
        {
            // the other cars drive at 45: they come down the screen as fast as you gain on them
            var c = _cars[i] + new Vector2(0, (_speed - 45) * dt);
            _cars[i] = c;
            if (c.Y > Height + 10) { _cars.RemoveAt(i); Score += 20; continue; }
            float cx = Middle(c.Y) + c.X;
            if (_hurt <= 0 && Mathf.Abs(cx - _x) < 8 && Mathf.Abs(c.Y - (Height - 16)) < 10)
            {
                _hurt = 1.5f;
                _speed = 20;
                _cars.RemoveAt(i);
                if (--_lives <= 0) Over = true;
            }
        }
    }

    public override void Draw(IScreen s)
    {
        s.Clear(new Color(0.20f, 0.45f, 0.20f));
        for (int y = 0; y < Height; y += 4)
        {
            int m = (int)Middle(y);
            s.Rect(m - (int)Half - 3, y, 3, 4, ((int)(_dist + y) / 8) % 2 == 0 ? new Color(0.9f, 0.1f, 0.1f) : Colors.White);
            s.Rect(m - (int)Half, y, (int)(2 * Half), 4, new Color(0.30f, 0.30f, 0.33f));
            s.Rect(m + (int)Half, y, 3, 4, ((int)(_dist + y) / 8) % 2 == 0 ? new Color(0.9f, 0.1f, 0.1f) : Colors.White);
            if (((int)(_dist + y) / 10) % 2 == 0) s.Rect(m - 1, y, 2, 4, new Color(0.95f, 0.9f, 0.6f));
        }
        foreach (var c in _cars)
        {
            int x = (int)(Middle(c.Y) + c.X);
            s.Rect(x - 4, (int)c.Y - 7, 8, 14, new Color(0.2f, 0.4f, 0.9f));
            s.Rect(x - 3, (int)c.Y - 4, 6, 4, new Color(0.7f, 0.85f, 1f));
        }
        if (_hurt <= 0 || (int)(_hurt * 10) % 2 == 0)
        {
            int x = (int)_x, y = Height - 16;
            s.Rect(x - 4, y - 7, 8, 14, new Color(0.95f, 0.75f, 0.10f));
            s.Rect(x - 3, y - 5, 6, 4, new Color(0.1f, 0.1f, 0.1f));
        }
        Hud(s, Score, $"x{Math.Max(0, _lives)} {(int)_speed}K");
    }
}

/// <summary>
/// The rhythm game (#1326): notes fall down four lanes to a line in time with a beat; the four arrows hit them. On the
/// line within 0.08 s a perfect (100 and the combo), within 0.16 s good (50); a miss breaks the combo. The song is a
/// minute; the score is the hits times the combo's bonus.
/// </summary>
public sealed class YodelBeat : MiniGame
{
    public const float Song = 60f, Bpm = 124f, Fall = 1.4f, Perfect = 0.08f, Good = 0.16f;
    private const int LineY = Height - 18;
    private readonly List<(float At, int Lane)> _notes = new();
    private readonly bool[] _was = new bool[4];
    private float _t = -2f;
    private int _next, _combo, _flash;
    private string _call = "";
    private float _callUntil;
    public int Hits, Misses;

    public override string Title => TitleOf(Kind.Rhythm);

    public YodelBeat(int seed)
    {
        var rng = new Random(seed);
        float beat = 60f / Bpm;
        // a pattern of eighths, denser as the song goes, never two in one lane on the same eighth
        for (float t = 1f; t < Song - 1; t += beat / 2)
        {
            float density = 0.35f + 0.35f * t / Song;
            if (rng.NextDouble() < density) _notes.Add((t, rng.Next(4)));
            if (t > Song * 0.6f && rng.NextDouble() < 0.12) _notes.Add((t, (_notes[^1].Lane + 2) % 4));
        }
    }

    public override void Step(float dt, MiniInput input)
    {
        if (Over) return;
        _t += dt;
        bool[] now = { input.Left, input.Down, input.Up, input.Right };
        for (int lane = 0; lane < 4; lane++)
        {
            bool pressed = now[lane] && !_was[lane];
            _was[lane] = now[lane];
            if (!pressed) continue;
            // the nearest unplayed note in the lane
            int best = -1;
            float gap = float.MaxValue;
            for (int i = _next; i < _notes.Count && _notes[i].At < _t + Good; i++)
                if (_notes[i].Lane == lane && Mathf.Abs(_notes[i].At - _t) < gap) { gap = Mathf.Abs(_notes[i].At - _t); best = i; }
            if (best < 0 || gap > Good) continue;
            _combo++;
            Hits++;
            Score += (gap <= Perfect ? 100 : 50) * (1 + Math.Min(_combo, 40) / 10);
            Say(gap <= Perfect ? "PERFECT" : "GOOD");
            _flash = lane;
            _notes[best] = (-1, lane);
        }
        // notes past the line unplayed: misses
        while (_next < _notes.Count && (_notes[_next].At < 0 || _notes[_next].At < _t - Good))
        {
            if (_notes[_next].At >= 0) { Misses++; _combo = 0; Say("MISS"); }
            _next++;
        }
        if (_t > Song) Over = true;
    }

    private void Say(string text)
    {
        _call = text;
        _callUntil = _t + 0.5f;
    }

    public override void Draw(IScreen s)
    {
        s.Clear(new Color(0.08f, 0.02f, 0.12f));
        Color[] lanes = { new(0.98f, 0.30f, 0.70f), new(0.20f, 0.85f, 0.98f), new(0.40f, 0.95f, 0.30f), new(0.98f, 0.80f, 0.15f) };
        for (int lane = 0; lane < 4; lane++)
        {
            int x = 40 + lane * 22;
            s.Rect(x, 12, 18, Height - 12, new Color(0.12f, 0.06f, 0.18f));
            s.Rect(x, LineY, 18, 3, lane == _flash && _t < _callUntil ? Colors.White : lanes[lane] * 0.7f);
        }
        for (int i = _next; i < _notes.Count; i++)
        {
            var (at, lane) = _notes[i];
            if (at < 0) continue;
            float y = LineY - (at - _t) / Fall * (LineY - 12);
            if (y < 4) break;
            s.Rect(40 + lane * 22 + 2, (int)y - 3, 14, 6, lanes[lane]);
        }
        if (_t < _callUntil) s.Text(_call, (Width - (_call.Length * 6 - 1)) / 2, 50, 1, Colors.White);
        if (_combo > 2) s.Text($"{_combo} COMBO", 2, 60, 1, new Color(1f, 0.85f, 0.2f));
        float left = Math.Max(0, Song - _t);
        Hud(s, Score, $"{(int)left}S");
    }
}
