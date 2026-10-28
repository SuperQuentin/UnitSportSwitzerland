namespace UnitSport.Adventure.Together;

/// <summary>
/// Stirring the figure-eight, la huit (#1266 part 5.3), pure. <see cref="StirTracker"/> reads 2D samples (the
/// mouse's motion while held, the right stick's position, a VR hand over the pot) and reports each eight it
/// finishes with its quality; the split meter's rules are the server's. Tier 0 (<c>StirRulesTests</c>).
/// </summary>
public static class StirRules
{
    /// <summary>An eight takes this long at best, s; it counts within <see cref="MinSeconds"/>..<see cref="MaxSeconds"/>.</summary>
    public const double TargetSeconds = 2.4, MinSeconds = 1.4, MaxSeconds = 3.6;
    /// <summary>A lobe turns this much at least (a lemniscate's lobe turns its tangent about 270°), radians.</summary>
    public const double LobeMin = 200 * Math.PI / 180;
    /// <summary>A lobe that turns past this is a circle, not an eight's half: a bad stir.</summary>
    public const double LobeMax = 540 * Math.PI / 180;
    /// <summary>The turn has to come back this far from its peak before a lobe counts as ended, radians.</summary>
    public const double Hysteresis = 40 * Math.PI / 180;

    /// <summary>The meter rises this much a second with nobody stirring.</summary>
    public const float IdleRise = 0.02f;
    /// <summary>And this much more a second when the heat is high (no wine in it).</summary>
    public const float HotRise = 0.04f;
    /// <summary>A poor eight (q under <see cref="PoorQ"/>) adds this.</summary>
    public const float PoorRise = 0.08f;
    /// <summary>A good eight (q from <see cref="GoodQ"/>) takes this off.</summary>
    public const float GoodFall = 0.05f;
    public const float PoorQ = 0.3f, GoodQ = 0.7f;
    /// <summary>"Nobody stirring": no eight from anyone for this long, s.</summary>
    public const double IdleAfter = 3;
    /// <summary>One eight a peer per this many seconds at most (the server drops faster ones).</summary>
    public const double PerPeerEvery = 1.2;
    /// <summary>The third eight at the table within this window counts nothing (a crowd of spoons does not help).</summary>
    public const double CrowdWindow = 3;
    public const int CrowdMax = 2;

    /// <summary>The meter after <paramref name="dt"/> seconds: rising when nobody stirs, faster on a high heat. Split stays split.</summary>
    public static float Step(float split, float dt, bool stirred, bool hot)
    {
        if (split >= 1f) return 1f;
        float rise = (stirred ? 0f : IdleRise) + (hot ? HotRise : 0f);
        return Math.Min(1f, split + rise * dt);
    }

    /// <summary>The meter after an eight of quality <paramref name="q"/>.</summary>
    public static float AfterEight(float split, float q)
    {
        if (split >= 1f) return 1f;
        if (q < PoorQ) return Math.Min(1f, split + PoorRise);
        if (q >= GoodQ) return Math.Max(0f, split - GoodFall);
        return split;
    }

    /// <summary>
    /// An eight's quality from its two lobes' turns (radians, signed), its duration and the coefficient of variation of
    /// the trace's speed: the balance of the lobes, their shape (270° each), the smoothness and the tempo.
    /// </summary>
    public static float Quality(double lobeA, double lobeB, double seconds, double speedCv)
    {
        double a = Math.Abs(lobeA), b = Math.Abs(lobeB);
        double balance = 1 - Math.Abs(a - b) / Math.Max(Math.Max(a, b), 1e-6);
        double ideal = 270 * Math.PI / 180;
        double shape = 1 - Math.Min(1, (Math.Abs(a - ideal) + Math.Abs(b - ideal)) / (2 * ideal));
        double smooth = Math.Clamp(1 - (speedCv - 0.15) / 0.6, 0, 1);
        double tempo = Math.Clamp(1 - Math.Abs(seconds - TargetSeconds) / 1.2, 0, 1);
        return (float)Math.Clamp(0.3 * balance + 0.2 * shape + 0.2 * smooth + 0.3 * tempo, 0, 1);
    }

    /// <summary>The 64-point guide the panel draws: a lemniscate of Bernoulli in -1..1.</summary>
    public static (float X, float Y) Guide(int i, int n = 64)
    {
        double t = 2 * Math.PI * i / n, s = Math.Sin(t), c = Math.Cos(t), d = 1 + s * s;
        return ((float)(c / d), (float)(s * c / d * 1.6));
    }
}

/// <summary>
/// Follows a stirring trace and finds its eights (#1266 part 5.3): the tangent's turning, cut into lobes where it
/// changes sense; two opposite lobes within the tempo window are an eight. A lobe turning past
/// <see cref="StirRules.LobeMax"/> (a circle) ends as a bad eight. No allocation per sample.
/// </summary>
public sealed class StirTracker
{
    /// <summary>A move shorter than this (in the sample's own units) carries no direction.</summary>
    public float MinStep = 0.004f;

    private bool _has, _hasHeading;
    private float _x, _y;
    private double _t, _heading;
    private double _turn, _peak;
    private int _lobes;
    private double _lobeA, _start, _lobeStart;
    // speed statistics over the eight
    private double _sum, _sumSq;
    private int _n;

    /// <summary>The quality of the last eight it reported.</summary>
    public float LastQ { get; private set; }
    /// <summary>Eights reported so far.</summary>
    public int Eights { get; private set; }

    public void Reset()
    {
        _has = _hasHeading = false;
        _turn = _peak = 0;
        _lobes = 0;
        _sum = _sumSq = 0;
        _n = 0;
    }

    /// <summary>A sample at time <paramref name="t"/> (s). True when it finished an eight; <paramref name="q"/> is its quality.</summary>
    public bool Add(float x, float y, double t, out float q)
    {
        q = 0;
        if (!_has)
        {
            (_x, _y, _t, _has) = (x, y, t, true);
            _start = _lobeStart = t;
            return false;
        }
        float dx = x - _x, dy = y - _y;
        double dt = t - _t;
        float step = MathF.Sqrt(dx * dx + dy * dy);
        if (step < MinStep) return false;
        (_x, _y, _t) = (x, y, t);
        if (dt > 0)
        {
            double v = step / dt;
            _sum += v;
            _sumSq += v * v;
            _n++;
        }
        double h = Math.Atan2(dy, dx);
        if (!_hasHeading)
        {
            _heading = h;
            _hasHeading = true;
            return false;
        }
        double d = h - _heading;
        while (d > Math.PI) d -= 2 * Math.PI;
        while (d < -Math.PI) d += 2 * Math.PI;
        _heading = h;
        _turn += d;
        // a circle: one lobe turning on and on is a poor stir
        if (Math.Abs(_turn) > StirRules.LobeMax)
        {
            q = 0.1f;
            Finish(q, t);
            return true;
        }
        if (Math.Abs(_turn) > Math.Abs(_peak)) _peak = _turn;
        // the sense changed for good: the lobe ended at its peak, the rest starts the next
        if (Math.Abs(_peak) - Math.Abs(_turn) > StirRules.Hysteresis && Math.Sign(_peak) != 0 && Math.Abs(_peak) >= StirRules.LobeMin)
        {
            double lobe = _peak, rest = _turn - _peak;
            if (_lobes == 1 && Math.Sign(lobe) != Math.Sign(_lobeA))
            {
                double seconds = t - _start;
                if (seconds is >= StirRules.MinSeconds and <= StirRules.MaxSeconds)
                {
                    double mean = _n > 0 ? _sum / _n : 0, var = _n > 0 ? _sumSq / _n - mean * mean : 0;
                    double cv = mean > 1e-9 ? Math.Sqrt(Math.Max(0, var)) / mean : 1;
                    q = StirRules.Quality(_lobeA, lobe, seconds, cv);
                }
                else q = 0.1f;   // a hurried or a dawdling eight
                Finish(q, t);
                // the next eight starts with what turned past this one's end
                _turn = _peak = rest;
                return true;
            }
            _lobeA = lobe;
            _start = _lobeStart;
            _lobeStart = t;
            _lobes = 1;
            _turn = _peak = rest;
        }
        else if (Math.Abs(_peak) - Math.Abs(_turn) > StirRules.Hysteresis && Math.Abs(_peak) < StirRules.LobeMin)
        {
            // a wobble too small to be a lobe: start the turn over
            _turn = _peak = _turn - _peak;
            _lobeStart = t;
            if (_lobes == 0) { _sum = _sumSq = 0; _n = 0; }
        }
        return false;
    }

    private void Finish(float q, double t)
    {
        LastQ = q;
        Eights++;
        _lobes = 0;
        _turn = _peak = 0;
        _start = _lobeStart = t;
        _sum = _sumSq = 0;
        _n = 0;
    }
}
