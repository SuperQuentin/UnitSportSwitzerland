namespace RideLink.Threading;

/// <summary>
/// Marshals engine callbacks onto whichever thread the consumer needs.
/// <para>
/// BLE and ANT callbacks arrive on arbitrary background threads — a WinRT event thread, an
/// ANT USB reader thread, a timer pool thread. A game engine will crash or silently corrupt
/// state if scene objects are touched from those. The engine therefore never assumes it may
/// publish inline: every observable pushes through the dispatcher the host installed.
/// </para>
/// </summary>
public interface IEventDispatcher
{
    /// <summary>
    /// True when the calling thread is already the target thread, letting the caller skip
    /// a needless post.
    /// </summary>
    bool IsOnDispatcherThread { get; }

    /// <summary>Queues work for the target thread. Must not block the caller.</summary>
    void Post(Action action);
}

/// <summary>
/// Runs callbacks inline on whatever thread produced them.
/// <para>
/// The right choice for a console host and for unit tests, where there is no UI thread to
/// protect and inline delivery keeps ordering trivially deterministic.
/// </para>
/// </summary>
public sealed class ImmediateDispatcher : IEventDispatcher
{
    public static ImmediateDispatcher Instance { get; } = new();

    public bool IsOnDispatcherThread => true;

    public void Post(Action action) => action();
}

/// <summary>
/// Posts callbacks to a captured <see cref="SynchronizationContext"/>.
/// <para>
/// Construct this on the thread that owns the context — for Godot, the main thread during
/// startup — and every measurement will be raised there.
/// </para>
/// </summary>
public sealed class SynchronizationContextDispatcher : IEventDispatcher
{
    private readonly SynchronizationContext _context;
    private readonly int _threadId;

    /// <param name="context">
    /// Target context. Defaults to the current thread's context; throws when there is none,
    /// because silently falling back to the thread pool would defeat the purpose.
    /// </param>
    public SynchronizationContextDispatcher(SynchronizationContext? context = null)
    {
        _context = context
            ?? SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "No SynchronizationContext on the current thread. Construct this on the host's "
                + "main thread, pass a context explicitly, or use ImmediateDispatcher.");
        _threadId = Environment.CurrentManagedThreadId;
    }

    public bool IsOnDispatcherThread => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action)
    {
        if (IsOnDispatcherThread) action();
        else _context.Post(static state => ((Action)state!)(), action);
    }
}

/// <summary>
/// Queues callbacks for a host that pumps them itself.
/// <para>
/// The shape a game loop usually wants: the engine enqueues from its background threads and
/// the host drains the queue once per frame from <c>_Process</c>, so delivery is both on the
/// main thread and at a point the host chooses.
/// </para>
/// </summary>
public sealed class QueuedDispatcher : IEventDispatcher
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();

    /// <summary>Always false: work is only ever run from <see cref="Drain"/>.</summary>
    public bool IsOnDispatcherThread => false;

    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>Number of callbacks waiting.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Runs queued callbacks on the calling thread. Call once per frame.
    /// </summary>
    /// <param name="maxItems">
    /// Cap on work per call so a burst after a reconnect cannot stall a frame. Negative means
    /// drain everything.
    /// </param>
    /// <returns>How many callbacks ran.</returns>
    public int Drain(int maxItems = -1)
    {
        int ran = 0;
        while ((maxItems < 0 || ran < maxItems) && _queue.TryDequeue(out var action))
        {
            action();
            ran++;
        }
        return ran;
    }
}
