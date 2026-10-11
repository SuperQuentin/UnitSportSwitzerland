namespace UnitSport.Movie;

/// <summary>
/// The last minutes of one player (#638): a fixed ring of frames that overwrites the oldest, so it
/// costs the same memory after an hour as after a minute and allocates nothing while it records.
/// The change-only properties are kept as events plus the values they had before the oldest frame
/// still held. <see cref="Slice"/> copies a stretch out as an <see cref="ActorTrack"/>.
/// </summary>
public sealed class ReplayRing
{
    private readonly double[] _times, _pos;
    private readonly float[] _f;
    private readonly long[] _baseNum, _lastNum;
    private readonly string[] _baseStr, _lastStr;
    private readonly List<DiscreteEvent> _events = new();
    private int _head, _count;   // _head: where the next frame goes
    private bool _any;

    public ReplayRing(int capacity, int discrete)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _times = new double[capacity];
        _pos = new double[capacity * 3];
        _f = new float[capacity * Channels.Stride];
        _baseNum = new long[discrete];
        _lastNum = new long[discrete];
        _baseStr = new string[discrete];
        _lastStr = new string[discrete];
        Array.Fill(_baseStr, "");
        Array.Fill(_lastStr, "");
    }

    public int Capacity => _times.Length;
    public int Count => _count;
    public double Oldest => _count == 0 ? double.NaN : _times[Index(0)];
    public double Newest => _count == 0 ? double.NaN : _times[Index(_count - 1)];
    /// <summary>Change-only events still held (for the checks).</summary>
    public int EventCount => _events.Count;

    private int Index(int k) => (_head - _count + k + _times.Length) % _times.Length;

    /// <summary>Records <paramref name="s"/> at <paramref name="t"/> (seconds, increasing).</summary>
    public void Append(double t, ActorState s)
    {
        if (_count > 0 && t <= Newest) return;   // two frames at one instant: the first one stands
        bool full = _count == _times.Length;
        _times[_head] = t;
        _pos[_head * 3] = s.E; _pos[_head * 3 + 1] = s.N; _pos[_head * 3 + 2] = s.Alt;
        Array.Copy(s.F, 0, _f, _head * Channels.Stride, Channels.Stride);
        _head = (_head + 1) % _times.Length;
        if (!full) _count++;

        for (int p = 0; p < _lastNum.Length; p++)
        {
            string str = s.Str[p] ?? "";
            if (!_any) { _baseNum[p] = _lastNum[p] = s.Num[p]; _baseStr[p] = _lastStr[p] = str; continue; }
            if (s.Num[p] == _lastNum[p] && str == _lastStr[p]) continue;
            _lastNum[p] = s.Num[p];
            _lastStr[p] = str;
            _events.Add(new DiscreteEvent(t, p, s.Num[p], str));
        }
        _any = true;

        // events from before the oldest frame still held fold into the values it starts with
        if (full)
        {
            double oldest = Oldest;
            int drop = 0;
            while (drop < _events.Count && _events[drop].T <= oldest)
            {
                var e = _events[drop++];
                _baseNum[e.Prop] = e.Num;
                _baseStr[e.Prop] = e.Str;
            }
            if (drop > 0) _events.RemoveRange(0, drop);
        }
    }

    /// <summary>
    /// The frames recorded after <paramref name="after"/> (absolute time), or null when there are
    /// none. The track's local time 0 is its first frame, whose absolute time comes out as
    /// <paramref name="start"/>.
    /// </summary>
    public ActorTrack? Slice(double after, out double start)
    {
        start = double.NaN;
        int first = 0;
        while (first < _count && _times[Index(first)] <= after) first++;
        int n = _count - first;
        if (n < 2) return null;
        start = _times[Index(first)];
        var times = new double[n];
        var pos = new double[n * 3];
        var f = new float[n * Channels.Stride];
        for (int k = 0; k < n; k++)
        {
            int at = Index(first + k);
            times[k] = _times[at] - start;
            Array.Copy(_pos, at * 3, pos, k * 3, 3);
            Array.Copy(_f, at * Channels.Stride, f, k * Channels.Stride, Channels.Stride);
        }
        var num = (long[])_baseNum.Clone();
        var str = (string[])_baseStr.Clone();
        var events = new List<DiscreteEvent>();
        foreach (var e in _events)
        {
            if (e.T <= start) { num[e.Prop] = e.Num; str[e.Prop] = e.Str; }
            else events.Add(e with { T = e.T - start });
        }
        return new ActorTrack(times, pos, f, num, str, events.ToArray());
    }
}
