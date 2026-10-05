using Godot;
using UnitSport.Map;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// A region download, running on a worker thread while the game carries on (#515 phase 4). The map
/// screen starts one and the pause menu shows how far it has got; entering and leaving a world does
/// not touch it, which is the whole point — picking a region should not mean sitting on a menu
/// watching a bar.
///
/// <para>
/// The work itself is <c>MapCore</c>'s <see cref="Planner"/> steps, the same ones the terminal
/// region wizard runs, so there is one definition of what downloading a region means. Progress
/// arrives through <see cref="IStepProgress"/> and is published as an immutable
/// <see cref="Progress"/> snapshot the UI reads at its own pace: no signal per line, and nothing
/// for the main thread to lock against while a step is hammering it.
/// </para>
///
/// <para>
/// <b>Deliberately static.</b> Almost every static in this project is reset when a world is torn
/// down (<c>docs/notes/ui/teardown.md</c>); this one must not be, because surviving that is its
/// reason to exist. It is cancelled when the game quits, and nothing it holds belongs to a world.
/// </para>
/// </summary>
public sealed class DownloadJob
{
    /// <summary>The one running or last-finished job, or null if none has been started.</summary>
    public static DownloadJob? Current { get; private set; }

    /// <summary>What the UI reads. A value type, so a reader never sees half an update.</summary>
    public readonly record struct Progress(
        bool Running,
        bool Finished,
        string? Error,
        string StepTitle,
        int StepIndex,
        int StepCount,
        double StepPercent,
        string Latest,
        int Tiles)
    {
        /// <summary>Whole-job fraction, counting each step as an equal share. 0-1.</summary>
        public double Overall => StepCount == 0 ? 0 : (StepIndex + StepPercent / 100.0) / StepCount;

        /// <summary>One line for a HUD: what it is doing and how far along.</summary>
        public string Line => Error != null ? $"download failed: {Error}"
            : Finished ? $"download finished: {Tiles:N0} km²"
            : !Running ? ""
            : $"{StepTitle} — {Overall * 100:F0}%";
    }

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private Progress _progress;

    /// <summary>The tiles this job is working through, for the map's "being downloaded" tint.</summary>
    public IReadOnlyCollection<TileId> Tiles { get; }

    private DownloadJob(IReadOnlyCollection<TileId> tiles)
    {
        Tiles = tiles;
        _progress = new Progress(Running: true, Finished: false, Error: null, "Starting", 0, 1, 0, "", tiles.Count);
    }

    public Progress Now
    {
        get { lock (_gate) return _progress; }
    }

    public static Progress? CurrentProgress => Current?.Now;

    /// <summary>
    /// Starts a download of <paramref name="selection"/>, replacing any finished job. A job that is
    /// still running is left alone and returned instead: two downloads writing the same folders
    /// would fight over the shared manifest.
    /// </summary>
    public static DownloadJob Start(Paths paths, CountryData country, LocalState local, Selection selection,
        Layers layers, Stats stats, SetupState state)
    {
        if (Current is { Now.Running: true } running) return running;

        var job = new DownloadJob(selection.Tiles.ToList());
        Current = job;
        // Long-running by design: this outlives the menu that started it and every world after it.
        Task.Run(() => job.RunAsync(paths, country, local, selection, layers, stats, state));
        return job;
    }

    /// <summary>Stops the job. Everything already downloaded or built stays, and a later run resumes.</summary>
    public static void CancelCurrent() => Current?._cts.Cancel();

    private async Task RunAsync(Paths paths, CountryData country, LocalState local, Selection selection,
        Layers layers, Stats stats, SetupState state)
    {
        string? error = null;
        try
        {
            var context = new SetupContext
            {
                Paths = paths, Country = country, Local = local, Selection = selection, Layers = layers,
                Stats = stats, State = state,
                // The game has neither, and no longer needs either for terrain: the download is C#
                // and the build is in-process. The two GDAL layers are off in the screen that got here.
                Python = null, Gdal = false,
            };

            var steps = Planner.Build(context).Where(s => s.Skip == null && s.Run != null).ToList();
            Directory.CreateDirectory(paths.LogsDir);

            for (int i = 0; i < steps.Count && !_cts.IsCancellationRequested; i++)
            {
                var step = steps[i];
                Publish(p => p with { StepTitle = step.Title, StepIndex = i, StepCount = steps.Count, StepPercent = 0 });

                string logPath = Path.Combine(paths.LogsDir, $"game_{i:00}_{Slug(step.Title)}.log");
                await using var log = new StreamWriter(logPath) { AutoFlush = true };
                var run = new StepRun(context, new JobProgress(this), step.Title, log, _cts.Token);

                if (!await step.Run!(run))
                {
                    error = run.Error ?? $"{step.Title} failed";
                    break;
                }
                stats.Save(paths);
            }
        }
        catch (OperationCanceledException)
        {
            // cancelling is an ordinary end, not a failure: what finished stays and a later run resumes
        }
        catch (Exception e)
        {
            error = e.Message;
            GD.PushWarning($"[download] {e.GetType().Name}: {e.Message}");
        }

        Publish(p => p with { Running = false, Finished = error == null, Error = error, StepPercent = 100 });
        GD.Print(error == null ? $"[download] finished: {Tiles.Count} tiles" : $"[download] stopped: {error}");
    }

    private void Publish(Func<Progress, Progress> change)
    {
        lock (_gate) _progress = change(_progress);
    }

    private static string Slug(string title) =>
        new(title.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    /// <summary>The job's end of <see cref="IStepProgress"/>: every line a step produces lands here.</summary>
    private sealed class JobProgress : IStepProgress
    {
        private readonly DownloadJob _job;

        public JobProgress(DownloadJob job) => _job = job;

        public double Value { set => _job.Publish(p => p with { StepPercent = value }); }

        public void Show(string text) => _job.Publish(p => p with { Latest = text });

        // The step already writes its own log file; nothing extra to do with the raw lines.
        public void Log(string line) { }
    }
}
