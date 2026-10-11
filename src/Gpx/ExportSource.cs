using Godot;
using UnitSport.Terrain;

namespace UnitSport.Gpx;

/// <summary>
/// What <see cref="VideoExporter"/> films (#786): something with a clock it can step by exactly one frame
/// and a camera it can wait on the terrain around. The GPX replay is one (<see cref="RaceExportSource"/>),
/// the movie studio's movie another (<c>Movie.MovieExportSource</c>).
/// </summary>
public interface IExportSource
{
    /// <summary>Anything to film at all.</summary>
    bool HasContent { get; }
    /// <summary>Length in the source's own seconds.</summary>
    double Duration { get; }
    /// <summary>Source seconds per second of video (a replay at 2× makes a video half as long).</summary>
    double Speed { get; }
    /// <summary>Where the camera is: the terrain must have settled round it before a frame is taken.</summary>
    Vector3 CameraPosition { get; }
    /// <summary>A sound track to put under the video (a .wav path), or null.</summary>
    string? AudioPath { get; }

    /// <summary>The exporter takes over: the source stops running on its own and shows its camera.</summary>
    void Take();
    /// <summary>
    /// The source at time <paramref name="t"/>, moved on by <paramref name="step"/> source seconds and
    /// <paramref name="screenStep"/> seconds of video (the camera's own easing runs on the second).
    /// </summary>
    void StepTo(double t, double step, double screenStep);
    /// <summary>The export is over (done or cancelled): everything back as it was.</summary>
    void Give();
    /// <summary>A loader reading ahead along the path the camera will take, or null.</summary>
    ExportPrefetcher? Prefetch(ChunkManager chunks);
}

/// <summary>The GPX replay as an export source: what <see cref="VideoExporter"/> always filmed.</summary>
public sealed class RaceExportSource : IExportSource
{
    private readonly RacePlayback _race;
    private readonly PlaybackCamera _camera;

    public RaceExportSource(RacePlayback race, PlaybackCamera camera)
    {
        _race = race;
        _camera = camera;
    }

    public bool HasContent => _race.Runners.Count > 0;
    public double Duration => _race.Duration;
    public double Speed => _race.Speed;
    public Vector3 CameraPosition => _camera.GlobalPosition;
    public string? AudioPath => null;

    public void Take()
    {
        _race.Playing = false;
        _race.SetProcess(false);
        _camera.SetProcess(false);
        _camera.Current = true;
    }

    public void StepTo(double t, double step, double screenStep)
    {
        _race.StepTo(t, step);
        _camera.Step(screenStep);
    }

    public void Give()
    {
        _race.SetProcess(true);
        _camera.SetProcess(true);
    }

    public ExportPrefetcher? Prefetch(ChunkManager chunks) =>
        _race.Focused is { } focused && chunks.Source != null ? ExportPrefetcher.Create(focused.Active, chunks) : null;
}
