using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Audio.Live;

/// <summary>
/// Server side of one station: ffmpeg pulls the live stream and decodes it to mono 16 kHz PCM on
/// a worker thread; the main thread hands the samples out <b>on the shared clock</b>, sample n
/// belonging to <c>T0 + n / Rate</c>. That stamping is what makes every listener hear the same
/// sample at the same moment: the stream's own timing (an Icecast burst, a stall) never reaches
/// a client. Too little audio is padded with silence, too much (the connect burst) is dropped, so
/// the index never leaves the clock.
/// </summary>
public sealed class StationTap : IDisposable
{
    /// <summary>Most audio kept waiting ahead of the clock; the rest of a burst is dropped.</summary>
    private const int MaxBacklog = WebRadio.Rate;
    private const double RestartAfter = 10;

    public Station Station { get; }

    /// <summary>Server clock of sample 0.</summary>
    public double T0 { get; private set; } = double.NaN;

    /// <summary>The next sample index to hand out.</summary>
    public long Emitted { get; private set; }

    /// <summary>Seconds since anyone asked for this station; the owner closes it after a while.</summary>
    public double Unwanted { get; set; }

    private readonly ConcurrentQueue<short[]> _incoming = new();
    private readonly Queue<short> _backlog = new();
    private readonly List<short> _pending = new();
    private Process? _ffmpeg;
    private double _restartAt;
    private bool _disposed;

    public StationTap(Station station) => Station = station;

    /// <summary>
    /// Main thread: hands out the samples due by now as whole chunks of
    /// <see cref="WebRadio.ChunkSamples"/>, each starting at a multiple of it. Nothing before the
    /// first audio arrives (T0 is the moment it does).
    /// </summary>
    public void Pump(List<(long Start, short[] Samples)> chunks)
    {
        double now = ClockSync.ServerNow;
        KeepRunning(now);
        while (_incoming.TryDequeue(out var block))
        {
            if (double.IsNaN(T0))
            {
                T0 = now;
                GD.Print($"[webradio] {Station.Name} on air");
            }
            foreach (var s in block) _backlog.Enqueue(s);
        }
        if (double.IsNaN(T0)) return;
        while (_backlog.Count > MaxBacklog) _backlog.Dequeue();

        long due = (long)((now - T0) * WebRadio.Rate);
        // a long stall (the server hiccuped): jump, not seconds of silence sent in one go
        if (due - Emitted > WebRadio.Rate * 2)
        {
            Emitted = (due / WebRadio.ChunkSamples - 1) * WebRadio.ChunkSamples;
            _pending.Clear();
        }
        for (; Emitted < due; Emitted++)
        {
            _pending.Add(_backlog.Count > 0 ? _backlog.Dequeue() : (short)0);
            if (_pending.Count < WebRadio.ChunkSamples) continue;
            chunks.Add((Emitted + 1 - WebRadio.ChunkSamples, _pending.ToArray()));
            _pending.Clear();
        }
    }

    private void KeepRunning(double now)
    {
        if (_ffmpeg is { HasExited: false } || now < _restartAt) return;
        _ffmpeg?.Dispose();
        _ffmpeg = null;
        _restartAt = now + RestartAfter;
        try
        {
            var info = new ProcessStartInfo(BundledTools.Resolve("ffmpeg"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // an argument list, never a shell string; the URL is from our own table anyway
            foreach (var a in new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5",
                "-i", Station.Url, "-vn", "-ac", "1", "-ar", WebRadio.Rate.ToString(), "-f", "s16le", "-",
            }) info.ArgumentList.Add(a);
            var p = Process.Start(info)!;
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) GD.Print($"[webradio] {Station.Name}: {e.Data}"); };
            p.BeginErrorReadLine();
            _ffmpeg = p;
            var stdout = p.StandardOutput.BaseStream;
            new Thread(() => Read(stdout)) { IsBackground = true, Name = $"webradio-{Station.Id}" }.Start();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[webradio] cannot start ffmpeg for {Station.Name}: {e.Message}");
        }
    }

    private void Read(Stream stdout)
    {
        var buf = new byte[3200];
        int carry = 0;
        try
        {
            while (!_disposed)
            {
                int n = stdout.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                n += carry;
                int samples = n / 2;
                var block = new short[samples];
                Buffer.BlockCopy(buf, 0, block, 0, samples * 2);
                _incoming.Enqueue(block);
                carry = n - samples * 2;
                if (carry > 0) buf[0] = buf[n - 1];
            }
        }
        catch (Exception) { /* the process went away: KeepRunning restarts it */ }
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_ffmpeg is { HasExited: false } p) p.Kill(); } catch (Exception) { }
        _ffmpeg?.Dispose();
        _ffmpeg = null;
    }
}
