using System.Threading.Tasks;
using Godot;

namespace UnitSport.Movie;

/// <summary>
/// A sound file becoming an <see cref="AudioAsset"/> (#656): copied into the movies' audio folder
/// (a movie keeps working when the original moves), decoded a few seconds per frame, then its beat
/// and waveform worked out on a worker thread. <see cref="Step"/> until it is done.
/// </summary>
public sealed class SoundImport
{
    public const string Folder = MovieSession.Folder + "/audio";
    private const double DecodePerStep = 6;   // seconds of sound decoded per frame

    public static string PathOf(string file) => ProjectSettings.GlobalizePath($"{Folder}/{file}");

    private readonly string _name, _file;
    private readonly AudioDecodeJob _job;
    private Task<(BeatGrid Beat, byte[] Peaks)>? _analysis;

    private SoundImport(string name, string file, AudioDecodeJob job)
    {
        _name = name;
        _file = file;
        _job = job;
    }

    /// <summary>Starts importing <paramref name="source"/> (an OS path); null with <paramref name="error"/> when it cannot be.</summary>
    public static SoundImport? Start(string source, out string? error)
    {
        error = null;
        if (!AudioDecode.Supported(source)) { error = $"Only {string.Join(", ", AudioDecode.Extensions.Select(e => "." + e))} files"; return null; }
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(Folder);
            string ext = System.IO.Path.GetExtension(source).ToLowerInvariant();
            string name = System.IO.Path.GetFileNameWithoutExtension(source);
            string file = $"{MovieSession.Safe(name)}-{Guid.NewGuid().ToString("N")[..6]}{ext}";
            System.IO.File.Copy(source, PathOf(file));
            if (AudioDecode.Load(PathOf(file)) is not { } stream) { error = "The file could not be read as sound"; return null; }
            return new SoundImport(name, file, new AudioDecodeJob(stream));
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    public float Progress => _analysis == null ? 0.85f * _job.Progress : _analysis.IsCompleted ? 1 : 0.9f;
    public string Stage => _analysis == null ? "Reading the sound" : "Finding the beat";

    /// <summary>One frame's work; true once the asset is ready.</summary>
    public bool Step()
    {
        if (_analysis == null)
        {
            if (!_job.Step(DecodePerStep)) return false;
            var samples = _job.Samples;
            int rate = _job.Rate;
            _analysis = Task.Run(() =>
            {
                var low = Pcm.Decimate(samples, rate, 11025, out int lowRate);
                return (BeatDetector.Detect(low, lowRate), Pcm.Peaks(samples, rate, AudioAsset.PeaksPerSecond));
            });
        }
        return _analysis.IsCompleted;
    }

    public AudioAsset Result()
    {
        var (beat, peaks) = _analysis!.Result;
        GD.Print($"[movie] imported {_file}: {_job.Duration:F1} s, {beat.Bpm:F1} BPM, {beat.Beats.Length} beats");
        return new AudioAsset { Name = _name, File = _file, Duration = _job.Duration, Beat = beat, Peaks = peaks };
    }

    /// <summary>
    /// The game's own sound out of the replay buffer as an asset: written as a .wav, its waveform taken
    /// from the samples themselves, no beat looked for. Null when there is under half a second.
    /// </summary>
    public static AudioAsset? FromGame(short[] samples, int rate)
    {
        if (samples.Length < rate / 2) return null;
        DirAccess.MakeDirRecursiveAbsolute(Folder);
        string file = $"game-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.wav";
        using (var f = System.IO.File.Create(PathOf(file))) Pcm.WriteWav(f, samples, rate);
        var mono = new float[samples.Length];
        for (int i = 0; i < mono.Length; i++) mono[i] = samples[i] / 32768f;
        return new AudioAsset
        {
            Name = "Game sound", File = file, Duration = (double)samples.Length / rate, Game = true,
            Peaks = Pcm.Peaks(mono, rate, AudioAsset.PeaksPerSecond),
        };
    }
}
