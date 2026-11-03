namespace UnitSport.Audio.Tracker;

/// <summary>
/// Plays a <see cref="TrackerModule"/> into stereo frames (#1244): a small FastTracker 2 replayer, tick by
/// tick. Covers what the venues' modules use: effects 0-F and the E commands, G/H global volume, K key off,
/// L envelope position, P panning slide, R multi-retrig, X extra-fine portamento, the volume column,
/// volume and panning envelopes, fadeout; linear and Amiga frequencies; linear interpolation. Leaves out
/// tremor, auto-vibrato and waveform choices (sine only). Pure C#: no Godot, no allocation per frame.
/// Periods are kept in FastTracker units: linear 7680 − 64·note, Amiga four times ProTracker's.
/// </summary>
public sealed class TrackerPlayer
{
    private readonly TrackerModule _m;
    private readonly int _rate;
    private readonly Channel[] _ch;
    private readonly float _gain;
    private int _order, _row, _tick, _speed, _bpm, _global = 64;
    private int _jumpOrder = -1, _jumpRow = -1, _loopTo = -1, _delayRows, _delayLeft;
    private double _tickFrames;
    private readonly HashSet<int> _visited = new();

    /// <summary>The song has come round to a row it already played (one pass done); it plays on, looping.</summary>
    public bool Ended { get; private set; }

    /// <summary>Seconds played (or skipped) since the start.</summary>
    public double Time { get; private set; }

    public TrackerModule Module => _m;

    private sealed class Channel
    {
        public TrackerModule.Instrument? Inst;
        public TrackerModule.Sample? Smp;
        public bool Active, KeyOn;
        public double Pos;
        public int Dir = 1;
        public int Period, Target, PortaSpeed, Volume, Pan = 128, DefaultPan = 128;
        public int Fx, Param, VolCol;
        public int VibPos, VibSpeed, VibDepth, VibDelta, TremPos, TremSpeed, TremDepth, TremDelta, Arp;
        public int MemUp, MemDown, MemVol, MemFineUp, MemFineDown, MemOffset, MemGlobal, MemPan, MemRetrig, MemXFine;
        public int LoopRow, LoopCount, CutAt = -1, DelayAt = -1, KeyOffAt = -1;
        public TrackerModule.Cell Delayed;
        public int VolEnvTick, PanEnvTick, Fade = 32768;
        // what the mixer uses for this tick
        public double Step;
        public float Left, Right;
    }

    public TrackerPlayer(TrackerModule m, int rate)
    {
        _m = m;
        _rate = rate;
        _speed = m.Speed;
        _bpm = m.Bpm;
        _ch = new Channel[m.Channels];
        for (int i = 0; i < _ch.Length; i++)
        {
            // a ProTracker song's channels left, right, right, left, softened toward the middle
            int pan = m.IsMod ? ((i & 3) is 0 or 3 ? 64 : 192) : 128;
            _ch[i] = new Channel { Pan = pan, DefaultPan = pan };
        }
        _gain = 0.9f / MathF.Sqrt(Math.Max(4, m.Channels));
    }

    // ---- frames ----------------------------------------------------------------------------------

    /// <summary>Mixes <paramref name="frames"/> stereo frames, interleaved, into <paramref name="stereo"/>.</summary>
    public void Render(float[] stereo, int frames) => Run(stereo, frames);

    /// <summary>Plays on <paramref name="seconds"/> without mixing: a client joining a venue mid-song starts where everyone else is.</summary>
    public void Skip(double seconds) => Run(null, (int)(seconds * _rate));

    private void Run(float[]? stereo, int frames)
    {
        int done = 0;
        while (done < frames)
        {
            if (_tickFrames <= 0)
            {
                Tick();
                _tickFrames += _rate * 2.5 / _bpm;
            }
            int n = Math.Min(frames - done, Math.Max(1, (int)Math.Ceiling(_tickFrames)));
            if (stereo != null) Array.Clear(stereo, done * 2, n * 2);
            foreach (var c in _ch)
                if (c.Active && c.Smp != null) Mix(c, stereo, done, n);
            if (stereo != null)
                for (int k = done * 2; k < (done + n) * 2; k++)
                {
                    // a soft knee over the top, so a loud chord is round, not torn
                    float v = stereo[k];
                    stereo[k] = v > 0.9f ? 0.9f + 0.1f * MathF.Tanh((v - 0.9f) * 10) : v < -0.9f ? -0.9f - 0.1f * MathF.Tanh((-v - 0.9f) * 10) : v;
                }
            _tickFrames -= n;
            done += n;
            Time += (double)n / _rate;
        }
    }

    private static void Mix(Channel c, float[]? stereo, int at, int n)
    {
        var s = c.Smp!;
        var d = s.Data;
        int len = d.Length;
        int loopEnd = s.LoopStart + s.LoopLength;
        if (stereo == null)
        {
            // skipping: the voice moves on in one step, its loop folded back in
            c.Pos += c.Step * c.Dir * n;
            if (s.LoopType == 0) { if (c.Pos >= len || c.Pos < 0) c.Active = false; }
            else if (c.Pos >= loopEnd || c.Pos < s.LoopStart)
            {
                double into = c.Pos - s.LoopStart;
                c.Pos = s.LoopStart + ((into % s.LoopLength) + s.LoopLength) % s.LoopLength;
                c.Dir = 1;
            }
            return;
        }
        for (int k = 0; k < n; k++)
        {
            int i = (int)c.Pos;
            if (i < 0 || i >= len) { c.Active = false; return; }
            if (stereo != null)
            {
                float frac = (float)(c.Pos - i);
                int j = i + 1 < len ? i + 1 : i;
                float v = d[i] + (d[j] - d[i]) * frac;
                stereo[(at + k) * 2] += v * c.Left;
                stereo[(at + k) * 2 + 1] += v * c.Right;
            }
            c.Pos += c.Step * c.Dir;
            if (s.LoopType == 0)
            {
                if (c.Pos >= len) { c.Active = false; return; }
            }
            else if (s.LoopType == 1)
            {
                while (c.Pos >= loopEnd) c.Pos -= s.LoopLength;
            }
            else
            {
                if (c.Dir > 0 && c.Pos >= loopEnd) { c.Pos = Math.Max(s.LoopStart, 2 * loopEnd - c.Pos - 1); c.Dir = -1; }
                else if (c.Dir < 0 && c.Pos < s.LoopStart) { c.Pos = Math.Min(loopEnd - 1, 2 * s.LoopStart - c.Pos); c.Dir = 1; }
            }
        }
    }

    // ---- the song ------------------------------------------------------------------------------

    private void Tick()
    {
        if (_tick == 0 && _delayLeft == 0) Row();
        else foreach (var c in _ch) TickEffects(c);
        foreach (var c in _ch) Update(c);
        if (++_tick < _speed) return;
        _tick = 0;
        if (_delayLeft == 0 && _delayRows > 0) { _delayLeft = _delayRows; _delayRows = 0; }
        if (_delayLeft > 0) { _delayLeft--; if (_delayLeft > 0) return; }
        Advance();
    }

    private void Advance()
    {
        if (_loopTo >= 0) { _row = _loopTo; _loopTo = -1; }
        else if (_jumpOrder >= 0 || _jumpRow >= 0)
        {
            _order = _jumpOrder >= 0 ? _jumpOrder : _order + 1;
            _row = Math.Max(0, _jumpRow);
            _jumpOrder = _jumpRow = -1;
        }
        else _row++;
        if (_order < _m.Order.Length && _row >= Pattern().Rows) { _row = 0; _order++; }
        if (_order >= _m.Order.Length) { _order = _m.Restart; _row = 0; Ended = true; }
        if (_row >= Pattern().Rows) _row = 0;
        if (!_visited.Add(_order * 1024 + _row) && !_ch.Any(c => c.LoopCount > 0)) Ended = true;
    }

    private TrackerModule.Pattern Pattern() => _m.Patterns[Math.Clamp(_m.Order[Math.Min(_order, _m.Order.Length - 1)], 0, _m.Patterns.Length - 1)];

    private void Row()
    {
        if (_visited.Count == 0) _visited.Add(_order * 1024 + _row);
        var pat = Pattern();
        int row = Math.Min(_row, pat.Rows - 1);
        for (int i = 0; i < _ch.Length; i++)
        {
            var c = _ch[i];
            var cell = pat.Cells[row * _m.Channels + i];
            c.Fx = cell.Effect;
            c.Param = cell.Param;
            c.VolCol = cell.Volume;
            c.Arp = 0;
            c.VibDelta = 0;
            c.TremDelta = 0;
            c.CutAt = c.DelayAt = c.KeyOffAt = -1;
            if (c.Fx == 0xE && c.Param >> 4 == 0xD && (c.Param & 15) > 0)
            {
                c.DelayAt = c.Param & 15;
                c.Delayed = cell;
                continue;
            }
            Trigger(c, cell);
            RowEffects(c, cell);
        }
    }

    private int Period(int note, TrackerModule.Sample s)
    {
        int n = Math.Clamp(note - 1 + s.RelativeNote, 0, 118);
        return _m.Linear
            ? 7680 - n * 64 - s.FineTune / 2
            : (int)Math.Round(1712 * Math.Pow(2, -(n - 48) / 12.0 - s.FineTune / (128.0 * 12)));
    }

    private void Trigger(Channel c, TrackerModule.Cell cell)
    {
        bool porta = c.Fx is 3 or 5 || c.VolCol >> 4 == 0xF;
        if (cell.Instrument > 0 && cell.Instrument <= _m.Instruments.Length)
        {
            c.Inst = _m.Instruments[cell.Instrument - 1];
            var smp = SampleOf(c.Inst, cell.Note is > 0 and <= 96 ? cell.Note : 49);
            if (smp != null)
            {
                c.Volume = smp.Volume;
                if (!_m.IsMod) c.Pan = smp.Panning;
            }
            c.KeyOn = true;
            c.Fade = 32768;
            c.VolEnvTick = c.PanEnvTick = 0;
        }
        if (cell.Note == 97) { KeyOff(c); return; }
        if (cell.Note is 0 or > 96 || c.Inst == null) return;
        var s = SampleOf(c.Inst, cell.Note);
        if (s == null) return;
        int period = Period(cell.Note, s);
        if (porta && c.Active)
        {
            c.Target = period;
            return;
        }
        c.Smp = s;
        c.Period = c.Target = period;
        c.Pos = 0;
        c.Dir = 1;
        c.Active = s.Data.Length > 0;
        c.VibPos = c.TremPos = 0;
        if (c.Fx == 9)
        {
            if (c.Param > 0) c.MemOffset = c.Param;
            c.Pos = c.MemOffset * 256;
            if (c.Pos >= s.Data.Length) c.Active = false;
        }
        if (cell.Instrument == 0) return;
        c.KeyOn = true;
        c.Fade = 32768;
        c.VolEnvTick = c.PanEnvTick = 0;
    }

    private static TrackerModule.Sample? SampleOf(TrackerModule.Instrument inst, int note)
    {
        if (inst.Samples.Length == 0) return null;
        int k = inst.Samples.Length == 1 ? 0 : inst.NoteSample[Math.Clamp(note - 1, 0, 95)];
        return k < inst.Samples.Length ? inst.Samples[k] : null;
    }

    private static void KeyOff(Channel c)
    {
        c.KeyOn = false;
        if (c.Inst == null || !c.Inst.Volume.On) c.Volume = 0;
    }

    private void RowEffects(Channel c, TrackerModule.Cell cell)
    {
        // the volume column first, as FastTracker does
        int v = c.VolCol;
        switch (v >> 4)
        {
            case >= 1 and <= 4: c.Volume = v - 0x10; break;
            case 5 when v == 0x50: c.Volume = 64; break;
            case 8: c.Volume = Math.Max(0, c.Volume - (v & 15)); break;
            case 9: c.Volume = Math.Min(64, c.Volume + (v & 15)); break;
            case 0xA: c.VibSpeed = v & 15; break;
            case 0xB: if ((v & 15) > 0) c.VibDepth = v & 15; break;
            case 0xC: c.Pan = (v & 15) * 17; break;
            case 0xF: if ((v & 15) > 0) c.PortaSpeed = (v & 15) * 16; break;
        }
        int p = c.Param, x = p >> 4, y = p & 15;
        switch (c.Fx)
        {
            case 1: if (p > 0) c.MemUp = p; break;
            case 2: if (p > 0) c.MemDown = p; break;
            case 3: if (p > 0) c.PortaSpeed = p; break;
            case 4: if (x > 0) c.VibSpeed = x; if (y > 0) c.VibDepth = y; break;
            case 5: case 6: case 0xA: if (p > 0) c.MemVol = p; break;
            case 7: if (x > 0) c.TremSpeed = x; if (y > 0) c.TremDepth = y; break;
            case 8: c.Pan = p; break;
            case 0xB: _jumpOrder = Math.Min(p, _m.Order.Length); _jumpRow = Math.Max(_jumpRow, 0); break;
            case 0xC: c.Volume = Math.Min(64, p); break;
            case 0xD: if (_jumpOrder < 0) _jumpOrder = _order + 1; _jumpRow = Math.Min(63, x * 10 + y); break;
            case 0xE:
                switch (x)
                {
                    case 1: if (y > 0) c.MemFineUp = y; c.Period -= c.MemFineUp * 4; break;
                    case 2: if (y > 0) c.MemFineDown = y; c.Period += c.MemFineDown * 4; break;
                    case 6:
                        if (y == 0) c.LoopRow = _row;
                        else if (c.LoopCount == 0) { c.LoopCount = y; _loopTo = c.LoopRow; }
                        else if (--c.LoopCount > 0) _loopTo = c.LoopRow;
                        break;
                    case 8: c.Pan = y * 17; break;
                    case 0xA: c.Volume = Math.Min(64, c.Volume + y); break;
                    case 0xB: c.Volume = Math.Max(0, c.Volume - y); break;
                    case 0xC: c.CutAt = y; if (y == 0) c.Volume = 0; break;
                    case 0xE: if (_delayLeft == 0) _delayRows = y + 1; break;
                }
                break;
            case 0xF:
                if (p == 0) break;
                if (p < 32) _speed = p; else _bpm = p;
                break;
            case 16: _global = Math.Min(64, p); break;
            case 17: if (p > 0) c.MemGlobal = p; break;
            case 20: c.KeyOffAt = p; if (p == 0) KeyOff(c); break;
            case 21: c.VolEnvTick = c.PanEnvTick = p; break;
            case 25: if (p > 0) c.MemPan = p; break;
            case 27: if (p > 0) c.MemRetrig = p; break;
            case 33:
                if (y > 0) c.MemXFine = y;
                if (x == 1) c.Period -= c.MemXFine; else if (x == 2) c.Period += c.MemXFine;
                break;
        }
        c.Period = Math.Max(1, c.Period);
    }

    private void TickEffects(Channel c)
    {
        if (c.DelayAt == _tick)
        {
            Trigger(c, c.Delayed);
            RowEffects(c, c.Delayed);
            c.DelayAt = -1;
            return;
        }
        if (c.CutAt == _tick) c.Volume = 0;
        if (c.KeyOffAt == _tick) KeyOff(c);
        int v = c.VolCol;
        switch (v >> 4)
        {
            case 6: c.Volume = Math.Max(0, c.Volume - (v & 15)); break;
            case 7: c.Volume = Math.Min(64, c.Volume + (v & 15)); break;
            case 0xB: Vibrato(c); break;
            case 0xD: c.Pan = Math.Max(0, c.Pan - (v & 15)); break;
            case 0xE: c.Pan = Math.Min(255, c.Pan + (v & 15)); break;
            case 0xF: Porta(c); break;
        }
        int p = c.Param, x = p >> 4, y = p & 15;
        switch (c.Fx)
        {
            case 0 when p > 0: c.Arp = (_tick % 3) switch { 1 => x, 2 => y, _ => 0 }; break;
            case 1: c.Period = Math.Max(1, c.Period - c.MemUp * 4); break;
            case 2: c.Period += c.MemDown * 4; break;
            case 3: Porta(c); break;
            case 4: Vibrato(c); break;
            case 5: Porta(c); VolSlide(c); break;
            case 6: Vibrato(c); VolSlide(c); break;
            case 7:
                c.TremPos += c.TremSpeed;
                c.TremDelta = Sine(c.TremPos) * c.TremDepth >> 6;
                break;
            case 0xA: VolSlide(c); break;
            case 0xE when x == 9 && y > 0 && _tick % y == 0: c.Pos = 0; c.Active = c.Smp != null; break;
            case 17:
                int gx = c.MemGlobal >> 4, gy = c.MemGlobal & 15;
                _global = Math.Clamp(_global + (gx > 0 ? gx : -gy), 0, 64);
                break;
            case 25:
                int px = c.MemPan >> 4, py = c.MemPan & 15;
                c.Pan = Math.Clamp(c.Pan + (px > 0 ? px : -py), 0, 255);
                break;
            case 27:
                int ry = c.MemRetrig & 15;
                if (ry > 0 && _tick % ry == 0) { c.Pos = 0; c.Active = c.Smp != null; }
                break;
        }
    }

    private static void VolSlide(Channel c)
    {
        int x = c.MemVol >> 4, y = c.MemVol & 15;
        c.Volume = Math.Clamp(c.Volume + (x > 0 ? x : -y), 0, 64);
    }

    private static void Porta(Channel c)
    {
        int speed = c.PortaSpeed * 4;
        if (c.Period < c.Target) c.Period = Math.Min(c.Target, c.Period + speed);
        else if (c.Period > c.Target) c.Period = Math.Max(c.Target, c.Period - speed);
    }

    private static void Vibrato(Channel c)
    {
        c.VibPos += c.VibSpeed;
        c.VibDelta = Sine(c.VibPos) * c.VibDepth >> 5;
    }

    /// <summary>A sine of 64 steps a turn, ±255, as the trackers' tables.</summary>
    private static int Sine(int pos) => (int)Math.Round(Math.Sin((pos & 63) * Math.PI / 32) * 255);

    /// <summary>The envelopes, the fadeout, and what the mixer plays this tick: its step and its gains.</summary>
    private void Update(Channel c)
    {
        if (c.Smp == null) return;
        int env = 64, penv = 32;
        if (c.Inst is { } inst)
        {
            if (inst.Volume.On) env = Envelope(inst.Volume, ref c.VolEnvTick, c.KeyOn);
            if (inst.Panning.On) penv = Envelope(inst.Panning, ref c.PanEnvTick, c.KeyOn);
            if (!c.KeyOn)
            {
                c.Fade = Math.Max(0, c.Fade - inst.Fadeout);
                if (c.Fade == 0) c.Active = false;
            }
        }
        double period = c.Period + c.VibDelta;
        double freq = _m.Linear ? 8363 * Math.Pow(2, (4608 - period) / 768) : 8363.0 * 1712 / Math.Max(1, period);
        if (c.Arp > 0) freq *= Math.Pow(2, c.Arp / 12.0);
        c.Step = freq / _rate;
        float vol = Math.Clamp(c.Volume + c.TremDelta, 0, 64) / 64f * env / 64f * c.Fade / 32768f * _global / 64f * _gain;
        int pan = Math.Clamp(c.Pan + (penv - 32) * (128 - Math.Abs(c.Pan - 128)) / 32, 0, 255);
        c.Left = vol * MathF.Sqrt((255 - pan) / 255f);
        c.Right = vol * MathF.Sqrt(pan / 255f);
    }

    private static int Envelope(TrackerModule.Envelope e, ref int tick, bool keyOn)
    {
        var pts = e.Points;
        int value = pts[^1].Value;
        for (int k = 0; k + 1 < pts.Length; k++)
        {
            if (tick < pts[k].Tick || tick > pts[k + 1].Tick) continue;
            int span = Math.Max(1, pts[k + 1].Tick - pts[k].Tick);
            value = pts[k].Value + (pts[k + 1].Value - pts[k].Value) * (tick - pts[k].Tick) / span;
            break;
        }
        if (tick < pts[0].Tick) value = pts[0].Value;
        bool held = e.Sustain && keyOn && e.SustainPoint < pts.Length && tick == pts[e.SustainPoint].Tick;
        if (!held)
        {
            tick++;
            if (e.Loop && e.LoopEnd < pts.Length && e.LoopStart < pts.Length && tick >= pts[e.LoopEnd].Tick) tick = pts[e.LoopStart].Tick;
            tick = Math.Min(tick, pts[^1].Tick);
        }
        return Math.Clamp(value, 0, 64);
    }

    /// <summary>How long one pass of the song lasts, s: played without mixing until it comes round, at most <paramref name="cap"/>.</summary>
    public static double Duration(TrackerModule m, double cap = 600)
    {
        var p = new TrackerPlayer(m, 1000);
        while (!p.Ended && p.Time < cap) p.Skip(0.25);
        return p.Time;
    }
}
