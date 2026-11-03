using Godot;
using UnitSport.Core;

namespace UnitSport.Audio.Tracker;

/// <summary>
/// <c>godot --headless --path . -- --trackercheck</c> (#1244): every module of the venues' playlists loads,
/// lasts a song's length, plays sound (neither silent nor clipped), lands at the same place seeked as
/// played, and the schedule runs on with the clock. Writes 20 s of each mood's first song to
/// <c>test_output/tracker/</c>, to listen to. <c>--trackerwav &lt;file&gt;</c> (#1306) writes one module whole there
/// instead (a film's soundtrack) and quits.
/// </summary>
public partial class TrackerProbe : Node
{
    private bool _ok = true;

    public static bool ParseArgs() => CmdArgs.All.Contains("--trackercheck") || CmdArgs.Value("--trackerwav") != null;

    private void Check(bool condition, string what)
    {
        GD.Print($"[tracker] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    public override void _Ready()
    {
        string dir = ProjectSettings.GlobalizePath("res://test_output/tracker");
        Directory.CreateDirectory(dir);
        if (CmdArgs.Value("--trackerwav") is { } one)
        {
            var (song, seconds) = TrackerLibrary.Get(one);
            if (song != null) Wav(Path.Combine(dir, Path.ChangeExtension(one, ".wav")), song, (int)Math.Ceiling(seconds));
            GD.Print(song != null ? $"[tracker] {one}: {seconds:F0} s in {dir}" : $"[tracker] FAILED: no module {one}");
            GetTree().Quit(song != null ? 0 : 1);
            return;
        }
        foreach (var mood in Enum.GetValues<VenueMood>())
            if (mood != VenueMood.None) Check(TrackerLibrary.Playlists.ContainsKey(mood), $"{mood} has a playlist");

        foreach (var (mood, files) in TrackerLibrary.Playlists)
            foreach (var file in files)
            {
                var (m, length) = TrackerLibrary.Get(file);
                if (m == null) { Check(false, $"{file} loads"); continue; }
                Check(m.Channels > 0 && m.Order.Length > 0 && m.Patterns.Length > 0 && m.Instruments.Length > 0,
                    $"{file}: {(m.IsMod ? "MOD" : "XM")}, {m.Channels} ch, {m.Patterns.Length} patterns, {m.Instruments.Length} instruments, {length:F0} s");
                Check(length >= 20, $"{file}: a song's length, not a jingle ({length:F0} s)");

                // ten seconds from a third in: sound, finite, under full scale
                var p = new TrackerPlayer(m, Dsp.Rate);
                p.Skip(length / 3);
                var buf = new float[Dsp.Rate * 10 * 2];
                p.Render(buf, Dsp.Rate * 10);
                float peak = 0, sum = 0;
                bool finite = true;
                foreach (float v in buf) { finite &= float.IsFinite(v); peak = Math.Max(peak, Math.Abs(v)); sum += v * v; }
                float rms = MathF.Sqrt(sum / buf.Length);
                Check(finite && rms > 0.01f && peak <= 1f, $"{file}: plays (rms {rms:F3}, peak {peak:F2})");

                // seeked or played to the same moment: the same sound (a client joining mid-song)
                var a = new TrackerPlayer(m, Dsp.Rate);
                a.Skip(30);
                var b = new TrackerPlayer(m, Dsp.Rate);
                var scratch = new float[Dsp.Rate * 30 * 2];
                b.Render(scratch, Dsp.Rate * 30);
                var ra = new float[Dsp.Rate * 2];
                var rb = new float[Dsp.Rate * 2];
                a.Render(ra, Dsp.Rate);
                b.Render(rb, Dsp.Rate);
                double dot = 0, na = 0, nb = 0;
                for (int k = 0; k < ra.Length; k++) { dot += ra[k] * rb[k]; na += ra[k] * ra[k]; nb += rb[k] * rb[k]; }
                double corr = na > 1e-9 && nb > 1e-9 ? dot / Math.Sqrt(na * nb) : 1;
                Check(corr > 0.6 || rms < 0.02f, $"{file}: seeking to 30 s sounds as playing to it (correlation {corr:F2})");
            }

        // the schedule: a moment later is further into the same song, or the next one
        var t0 = TrackerLibrary.At(VenueMood.Cafe, 1000, 7);
        var t1 = TrackerLibrary.At(VenueMood.Cafe, 1002, 7);
        Check(t0 != null && t1 != null && (t0.Value.File != t1.Value.File || Math.Abs(t1.Value.Into - t0.Value.Into - 2) < 1e-6),
            "the playlist runs on with the clock");
        Check(TrackerLibrary.At(VenueMood.Cafe, 1000, 7) == TrackerLibrary.At(VenueMood.Cafe, 1000, 7), "every client finds the same song at the same time");

        foreach (var (mood, files) in TrackerLibrary.Playlists)
            if (TrackerLibrary.Get(files[0]).Module is { } m) Wav(Path.Combine(dir, $"{mood}.wav"), m, 20);
        GD.Print($"[tracker] 20 s of each mood in {dir}");
        GD.Print($"[tracker] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private static void Wav(string path, TrackerModule m, int seconds)
    {
        var p = new TrackerPlayer(m, Dsp.Rate);
        var buf = new float[Dsp.Rate * seconds * 2];
        p.Render(buf, Dsp.Rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        int bytes = buf.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8); w.Write("fmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Dsp.Rate); w.Write(Dsp.Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (float v in buf) w.Write((short)Math.Clamp(v * 32767, -32768, 32767));
    }
}
