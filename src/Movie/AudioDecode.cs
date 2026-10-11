using Godot;

namespace UnitSport.Movie;

/// <summary>
/// Sound files for the movie studio (#656), read with Godot's own decoders: .wav, .ogg and .mp3
/// load at run time from any path, and <see cref="AudioStreamPlayback.MixAudio"/> plays a stream
/// into samples without a sound card, so beats are found without ffmpeg.
/// </summary>
public static class AudioDecode
{
    public static readonly string[] Extensions = { "wav", "ogg", "mp3" };

    public static bool Supported(string path) =>
        Array.IndexOf(Extensions, System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant()) >= 0;

    /// <summary>The sound in file <paramref name="path"/> (an OS path), or null when it cannot be read.</summary>
    public static AudioStream? Load(string path)
    {
        try
        {
            return System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
            {
                "wav" => AudioStreamWav.LoadFromFile(path, null),
                "ogg" => AudioStreamOggVorbis.LoadFromFile(path),
                "mp3" => new AudioStreamMP3 { Data = System.IO.File.ReadAllBytes(path) },
                _ => null,
            };
        }
        catch (Exception e)
        {
            GD.PushWarning($"[movie] could not read {path}: {e.Message}");
            return null;
        }
    }
}

/// <summary>
/// A stream played into mono samples a piece at a time (#656), so a four-minute song decodes over
/// a few frames behind a progress bar instead of freezing the studio.
/// </summary>
public sealed class AudioDecodeJob
{
    private readonly AudioStreamPlayback _playback;
    private readonly float[] _mono;
    private int _filled;
    private bool _ended;

    public AudioDecodeJob(AudioStream stream)
    {
        Rate = (int)AudioServer.GetMixRate();
        Duration = stream.GetLength();
        _mono = new float[Math.Max(0, (int)(Duration * Rate))];
        _playback = stream.InstantiatePlayback();
        _playback.Start(0);
    }

    public int Rate { get; }
    public double Duration { get; }
    public float Progress => _mono.Length == 0 ? 1 : (float)_filled / _mono.Length;
    public bool Done => _ended || _filled >= _mono.Length;

    /// <summary>Decodes up to <paramref name="seconds"/> more; true once all of it is.</summary>
    public bool Step(double seconds)
    {
        int want = Math.Min(_mono.Length - _filled, (int)(seconds * Rate));
        while (want > 0 && !_ended)
        {
            var mixed = _playback.MixAudio(1f, Math.Min(want, 16384));
            // a stream shorter than it said: what came is all there is
            if (mixed.Length == 0) { _ended = true; break; }
            for (int i = 0; i < mixed.Length && _filled < _mono.Length; i++) _mono[_filled++] = (mixed[i].X + mixed[i].Y) * 0.5f;
            want -= mixed.Length;
        }
        return Done;
    }

    /// <summary>The samples, at <see cref="Rate"/>.</summary>
    public float[] Samples => _filled < _mono.Length ? _mono[.._filled] : _mono;
}
