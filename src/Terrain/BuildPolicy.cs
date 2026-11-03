using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace UnitSport.Terrain;

/// <summary>
/// How much of the machine tile building may take (#1381): pure rules, no Godot, tier-0 tested.
/// Loading used to run a build per core at Normal priority and starve the OS and the main thread.
/// </summary>
public static class BuildPolicy
{
    /// <summary>Most concurrent builds on a desktop outside video export: two cores stay free for the OS and the game.</summary>
    public static int Ceiling(int processorCount) => Math.Max(2, processorCount - 2);

    /// <summary>A requested cap held to the ceiling; true when it had to be lowered.</summary>
    public static int Clamp(int requested, int ceiling, out bool clamped)
    {
        clamped = requested > ceiling;
        return clamped ? ceiling : requested;
    }
}

/// <summary>
/// A few dedicated BelowNormal threads that run tile builds instead of the shared ThreadPool. An
/// <c>await</c> in a build captures this scheduler, so the CPU stages that follow an IO wait come
/// back here too; a source's reads and decodes called from a build run inline (<see cref="Run{T}"/>).
/// </summary>
public sealed class BuildScheduler : TaskScheduler
{
    /// <summary>The scheduler of the build running this async flow (it follows awaits, even <c>ConfigureAwait(false)</c> ones); null elsewhere.</summary>
    public static readonly AsyncLocal<BuildScheduler?> Ambient = new();

    /// <summary>
    /// <c>Task.Run</c> that, from a build, runs the work inline on the calling (BelowNormal) thread and
    /// returns a finished task: no hop to the pool at Normal priority, and no queue behind the build
    /// threads, which would deadlock a caller that blocks on the result (sync-over-async callers such as
    /// <c>FallbackChunkSource.StageWalls</c> or <c>SitePlans</c>). Cancelled or thrown work gives the same
    /// cancelled or faulted task <c>Task.Run</c> would.
    /// </summary>
    public static Task<T> Run<T>(Func<T> work, CancellationToken ct)
    {
        if (Ambient.Value == null) return Task.Run(work, ct);
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        try { return Task.FromResult(work()); }
        catch (OperationCanceledException e) when (e.CancellationToken == ct) { return Task.FromCanceled<T>(ct); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }

    private readonly BlockingCollection<Task> _queue = new();
    private readonly Thread[] _threads;

    public BuildScheduler(int threads, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        _threads = new Thread[Math.Max(1, threads)];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Priority = priority, Name = $"TileBuild{i}" };
            _threads[i].Start();
        }
    }

    public int ThreadCount => _threads.Length;
    public override int MaximumConcurrencyLevel => _threads.Length;

    private void Work()
    {
        foreach (var task in _queue.GetConsumingEnumerable()) TryExecuteTask(task);
    }

    protected override void QueueTask(Task task) => _queue.Add(task);
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    protected override System.Collections.Generic.IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();
}

/// <summary>
/// When a cache over its cap is trimmed (#1381): a running byte total decides, never a directory
/// scan per write, and a trim is rate-limited and never runs twice at once.
/// </summary>
public static class CacheTrimPolicy
{
    public const double MinIntervalSeconds = 30;

    public static bool ShouldTrim(long bytes, long maxBytes, bool running, double secondsSinceLast)
        => !running && bytes > maxBytes && secondsSinceLast >= MinIntervalSeconds;
}
