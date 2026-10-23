using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RideLink.Model;
using RideLink.Threading;

namespace RideLink.Capabilities;

/// <summary>Reports that a quantity started being served by a different device.</summary>
/// <param name="Quantity">"Power", "Cadence", "HeartRate", "Speed" or "Distance".</param>
/// <param name="Previous">Source that was winning, null if there was none.</param>
/// <param name="Current">Source now winning.</param>
/// <param name="DeviceName">Display name of the new source's device.</param>
public readonly record struct ActiveSourceChanged(
    string Quantity, SourceId? Previous, SourceId Current, string DeviceName);

/// <summary>
/// Merges every registered capability into one stream per quantity, choosing the best live
/// source for each.
/// <para>
/// This is what lets the consumer subscribe to "power" rather than to "the Assioma pedals":
/// plug in a power meter mid-ride and watts switch to it; unplug it and they fall back to the
/// trainer, with no consumer code involved.
/// </para>
/// <para>
/// Selection is re-evaluated on every arriving sample rather than on a timer. A source that
/// falls silent cannot itself trigger anything, but the moment a lower-ranked source emits,
/// the silent one is found to be stale and the lower-ranked source takes over — which is
/// exactly when the handover matters.
/// </para>
/// </summary>
public sealed class CapabilityRouter : IDisposable
{
    private readonly IEventDispatcher _dispatcher;
    private readonly SourcePriorityPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    private readonly Subject<ActiveSourceChanged> _sourceChanges = new();
    private readonly List<IDisposable> _registrations = [];
    private readonly object _gate = new();

    private readonly PrioritizedMerge<PowerSample> _power;
    private readonly PrioritizedMerge<CadenceSample> _cadence;
    private readonly PrioritizedMerge<HeartRateSample> _heartRate;
    private readonly PrioritizedMerge<SpeedSample> _speed;
    private readonly PrioritizedMerge<DistanceSample> _distance;
    private readonly Subject<BatterySample> _battery = new();

    private readonly List<IControllableResistance> _trainers = [];

    public CapabilityRouter(
        IEventDispatcher? dispatcher = null,
        SourcePriorityPolicy? policy = null,
        TimeProvider? clock = null,
        ILogger<CapabilityRouter>? logger = null)
    {
        _dispatcher = dispatcher ?? ImmediateDispatcher.Instance;
        _policy = policy ?? SourcePriorityPolicy.Default;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CapabilityRouter>.Instance;

        _power = Merge<PowerSample>("Power");
        _cadence = Merge<CadenceSample>("Cadence");
        _heartRate = Merge<HeartRateSample>("HeartRate");
        _speed = Merge<SpeedSample>("Speed");
        _distance = Merge<DistanceSample>("Distance");

        PrioritizedMerge<T> Merge<T>(string quantity) where T : IMeasurement =>
            new(quantity, _policy.StaleAfter, _clock, _dispatcher, OnActiveSourceChanged);
    }

    /// <summary>Best available power, watts.</summary>
    public IObservable<PowerSample> Power => _power.Output;

    /// <summary>Best available cadence, rpm.</summary>
    public IObservable<CadenceSample> Cadence => _cadence.Output;

    /// <summary>Best available heart rate, bpm.</summary>
    public IObservable<HeartRateSample> HeartRate => _heartRate.Output;

    /// <summary>Best available speed, m/s.</summary>
    public IObservable<SpeedSample> Speed => _speed.Output;

    /// <summary>Best available cumulative distance, m.</summary>
    public IObservable<DistanceSample> Distance => _distance.Output;

    /// <summary>Battery levels from every device that reports one; not prioritized.</summary>
    public IObservable<BatterySample> Battery => _battery;

    /// <summary>Fires when a quantity changes which device serves it.</summary>
    public IObservable<ActiveSourceChanged> ActiveSourceChanges => _sourceChanges;

    /// <summary>Every registered controllable trainer.</summary>
    public IReadOnlyList<IControllableResistance> Trainers
    {
        get { lock (_gate) return _trainers.ToArray(); }
    }

    /// <summary>
    /// The trainer control commands go to. First registered wins; with two trainers connected
    /// the consumer should pick explicitly rather than let the engine guess.
    /// </summary>
    public IControllableResistance? PrimaryTrainer
    {
        get { lock (_gate) return _trainers.FirstOrDefault(); }
    }

    /// <summary>
    /// Registers everything a capability implements. One object implementing
    /// <see cref="IPowerSource"/>, <see cref="ICadenceSource"/> and <see cref="ISpeedSource"/>
    /// is wired into all three.
    /// </summary>
    /// <returns>Disposing removes the capability from the router again.</returns>
    public IDisposable Register(ICapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        var parts = new List<IDisposable>();

        if (capability is IPowerSource p)
            parts.Add(_power.Add(p, p.Power, _policy.RankPower(p)));

        if (capability is ICadenceSource c)
            parts.Add(_cadence.Add(c, c.Cadence, _policy.RankCadence(c)));

        if (capability is IHeartRateSource h)
            parts.Add(_heartRate.Add(h, h.HeartRate, _policy.RankHeartRate(h)));

        if (capability is ISpeedSource s)
            parts.Add(_speed.Add(s, s.Speed, _policy.RankSpeed(s)));

        if (capability is IDistanceSource d)
            parts.Add(_distance.Add(d, d.Distance, _policy.RankDistance(d)));

        if (capability is IBatterySource b)
            parts.Add(b.Battery.Subscribe(sample => _dispatcher.Post(() => _battery.OnNext(sample))));

        if (capability is IControllableResistance trainer)
        {
            lock (_gate) _trainers.Add(trainer);
            parts.Add(new ActionDisposable(() => { lock (_gate) _trainers.Remove(trainer); }));
        }

        if (parts.Count == 0)
            _logger.LogWarning(
                "Capability {Source} from {Device} implements no known capability interface",
                capability.Source, capability.DeviceName);

        var registration = new CompositeDisposable(parts);
        lock (_gate) _registrations.Add(registration);

        _logger.LogInformation(
            "Registered {Device} ({Source}) providing {Count} capability stream(s)",
            capability.DeviceName, capability.Source, parts.Count);

        return registration;
    }

    private void OnActiveSourceChanged(ActiveSourceChanged change)
    {
        _logger.LogInformation(
            "{Quantity} now served by {Device} ({Source}), was {Previous}",
            change.Quantity, change.DeviceName, change.Current,
            change.Previous?.ToString() ?? "nothing");

        _dispatcher.Post(() => _sourceChanges.OnNext(change));
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (_gate)
        {
            registrations = _registrations.ToArray();
            _registrations.Clear();
            _trainers.Clear();
        }

        foreach (var registration in registrations) registration.Dispose();

        _power.Dispose();
        _cadence.Dispose();
        _heartRate.Dispose();
        _speed.Dispose();
        _distance.Dispose();
        _battery.Dispose();
        _sourceChanges.Dispose();
    }

    /// <summary>
    /// Holds the candidate sources for one quantity and forwards only the winner's samples.
    /// </summary>
    private sealed class PrioritizedMerge<TSample> : IDisposable where TSample : IMeasurement
    {
        private sealed record Entry(SourceId Source, string DeviceName, int Rank)
        {
            public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.MinValue;
        }

        private readonly string _quantity;
        private readonly TimeSpan _staleAfter;
        private readonly TimeProvider _clock;
        private readonly IEventDispatcher _dispatcher;
        private readonly Action<ActiveSourceChanged> _onActiveSourceChanged;

        private readonly Dictionary<SourceId, Entry> _entries = [];
        private readonly Subject<TSample> _output = new();
        private readonly object _gate = new();
        private SourceId? _active;

        public PrioritizedMerge(
            string quantity,
            TimeSpan staleAfter,
            TimeProvider clock,
            IEventDispatcher dispatcher,
            Action<ActiveSourceChanged> onActiveSourceChanged)
        {
            _quantity = quantity;
            _staleAfter = staleAfter;
            _clock = clock;
            _dispatcher = dispatcher;
            _onActiveSourceChanged = onActiveSourceChanged;
        }

        public IObservable<TSample> Output => _output;

        public IDisposable Add(ICapability capability, IObservable<TSample> stream, int rank)
        {
            var entry = new Entry(capability.Source, capability.DeviceName, rank);
            lock (_gate) _entries[capability.Source] = entry;

            var subscription = stream.Subscribe(sample => OnSample(entry, sample));

            return new ActionDisposable(() =>
            {
                subscription.Dispose();
                lock (_gate)
                {
                    _entries.Remove(entry.Source);
                    if (_active == entry.Source) _active = null;
                }
            });
        }

        private void OnSample(Entry entry, TSample sample)
        {
            ActiveSourceChanged? change = null;
            bool publish;

            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                entry.LastSeen = now;

                // The winner is the highest-ranked source that has spoken recently. Ties are
                // broken by whoever is already active, so two identical sensors do not
                // ping-pong the stream between them.
                Entry? best = null;
                foreach (var candidate in _entries.Values)
                {
                    if (now - candidate.LastSeen > _staleAfter) continue;
                    if (best is null || candidate.Rank > best.Rank) best = candidate;
                    else if (candidate.Rank == best.Rank && _active == candidate.Source) best = candidate;
                }

                best ??= entry;
                publish = best.Source == entry.Source;

                if (publish && _active != best.Source)
                {
                    change = new ActiveSourceChanged(_quantity, _active, best.Source, best.DeviceName);
                    _active = best.Source;
                }
            }

            if (change is { } c) _onActiveSourceChanged(c);
            if (publish) _dispatcher.Post(() => _output.OnNext(sample));
        }

        public void Dispose()
        {
            _output.Dispose();
            lock (_gate) _entries.Clear();
        }
    }

    private sealed class ActionDisposable(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    private sealed class CompositeDisposable(IEnumerable<IDisposable> parts) : IDisposable
    {
        private readonly IDisposable[] _parts = parts.ToArray();

        public void Dispose()
        {
            foreach (var part in _parts) part.Dispose();
        }
    }
}
