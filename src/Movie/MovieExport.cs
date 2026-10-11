using Godot;
using UnitSport.Gpx;
using UnitSport.Terrain;

namespace UnitSport.Movie;

/// <summary>
/// The movie as something <see cref="VideoExporter"/> films (#786): the active sequence from its in to its
/// out, the program with its transitions, frame-exact. While it renders the studio shows only its player,
/// filling the window, so what is grabbed is exactly what the player shows: the cuts, the fades, the
/// crossfades and wipes. The sound clips over the sequence are mixed into one .wav for ffmpeg to put under.
/// </summary>
public sealed class MovieExportSource : IExportSource
{
    private readonly MovieStudio _studio;
    private readonly MovieStage _stage;
    private readonly Camera3D _camera;
    private readonly double _from, _to;
    private readonly string _audio;

    public MovieExportSource(MovieStudio studio, MovieStage stage, Camera3D camera, string directory)
    {
        _studio = studio;
        _stage = stage;
        _camera = camera;
        var p = stage.Project;
        var seq = p.Sequences[p.ActiveSequence];
        _from = Math.Clamp(seq.In, 0, p.Duration);
        _to = Math.Clamp(Math.Min(seq.Out, p.Duration), _from, p.Duration);
        _audio = System.IO.Path.Combine(directory, "sound.wav");
    }

    public bool HasContent => _to - _from >= MovieProject.MinLength;
    public double Duration => _to - _from;
    public double Speed => 1;
    public Vector3 CameraPosition => _camera.GlobalPosition;
    public string? AudioPath => System.IO.File.Exists(_audio) ? _audio : null;

    public void Take()
    {
        _stage.Playing = false;
        WriteSound();
        _studio.EnterExportView();
    }

    public void StepTo(double t, double step, double screenStep) => _stage.Seek(_from + t);

    public void Give() => _studio.ExitExportView();

    public ExportPrefetcher? Prefetch(ChunkManager chunks) => null;

    /// <summary>The sound clips over the sequence, decoded and mixed into <c>sound.wav</c>; nothing when there are none.</summary>
    private void WriteSound()
    {
        var p = _stage.Project;
        var pieces = new List<Soundtrack.Piece>();
        var decoded = new Dictionary<int, (float[] Samples, int Rate)>();
        foreach (var c in p.Clips)
        {
            if (!c.Audio || c.End <= _from || c.Start >= _to) continue;
            if (!decoded.TryGetValue(c.Track, out var pcm))
            {
                var stream = AudioDecode.Load(SoundImport.PathOf(p.Audio[c.Track].File));
                if (stream == null) continue;
                var job = new AudioDecodeJob(stream);
                while (!job.Step(30)) { }
                decoded[c.Track] = pcm = (job.Samples, job.Rate);
            }
            pieces.Add(new Soundtrack.Piece(pcm.Samples, pcm.Rate, c.Start, c.In, c.Out));
        }
        if (System.IO.File.Exists(_audio)) System.IO.File.Delete(_audio);
        if (pieces.Count == 0) return;
        int rate = (int)AudioServer.GetMixRate();
        var mixed = Soundtrack.Mix(pieces, _from, _to, rate);
        using var file = System.IO.File.Create(_audio);
        Pcm.WriteWav(file, mixed, rate);
        GD.Print($"[movie] export sound: {pieces.Count} clips, {mixed.Length / (double)rate:F1} s -> {_audio}");
    }
}
