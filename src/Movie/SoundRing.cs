namespace UnitSport.Movie;

/// <summary>
/// The last minutes of the game's own sound (#656): 16-bit mono in a fixed ring, the oldest
/// overwritten, timed by the clock reading when its newest sample came in. The replay buffer
/// keeps one beside its players' rings; a grab copies a stretch out as a .wav.
/// </summary>
public sealed class SoundRing
{
    private readonly short[] _samples;
    private int _head, _count;

    public SoundRing(int seconds, int rate)
    {
        Rate = rate;
        _samples = new short[Math.Max(1, seconds * rate)];
    }

    public int Rate { get; }
    public int Count => _count;
    /// <summary>The clock when the newest sample was heard (NaN before any).</summary>
    public double Newest { get; private set; } = double.NaN;
    public double Oldest => Newest - (double)_count / Rate;

    /// <summary>Appends <paramref name="mono"/> (-1..1), the last of them heard at <paramref name="now"/>.</summary>
    public void Append(ReadOnlySpan<float> mono, double now)
    {
        foreach (float v in mono)
        {
            _samples[_head] = (short)Math.Clamp(v * 32767f, -32768f, 32767f);
            _head = (_head + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }
        Newest = now;
    }

    /// <summary>A gap in the sound (the studio was open): what came before no longer lines up with the clock.</summary>
    public void Clear()
    {
        _count = 0;
        Newest = double.NaN;
    }

    /// <summary>The samples heard after <paramref name="after"/> (clock time), and when the first of them was.</summary>
    public short[] Slice(double after, out double start)
    {
        int skip = _count == 0 ? 0 : (int)Math.Clamp(Math.Ceiling((after - Oldest) * Rate), 0, _count);
        int n = _count - skip;
        start = Oldest + (double)skip / Rate;
        var o = new short[n];
        int first = (_head - _count + skip + _samples.Length * 2) % _samples.Length;
        for (int i = 0; i < n; i++) o[i] = _samples[(first + i) % _samples.Length];
        return o;
    }
}
