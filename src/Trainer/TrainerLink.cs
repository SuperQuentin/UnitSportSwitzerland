using System.Collections.Concurrent;
using System.Text.Json;
using Godot;
using RideLink;
using RideLink.Ble;
using RideLink.Capabilities;
using RideLink.Model;
using RideLink.Threading;
using RideLink.Transport;
using UnitSport.Core;

namespace UnitSport.Trainer;

/// <summary>A device remembered from an earlier session, to reconnect without a scan.</summary>
public sealed class KnownDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public TransportKind Transport { get; set; }
    public List<string> Services { get; set; } = new();
}

/// <summary>The rider's trainer settings, <c>user://trainer.json</c>. Serialised as it is.</summary>
public sealed class TrainerPrefs
{
    /// <summary>Trainer difficulty, percent of the road's grade the trainer plays (Zwift's default: 50).</summary>
    public int Difficulty { get; set; } = 50;
    public List<KnownDevice> Devices { get; set; } = new();
    /// <summary>GPX files trained on, newest first.</summary>
    public List<string> Recent { get; set; } = new();
}

/// <summary>
/// The game's one door to the rider's hardware (#901): a smart trainer, a power meter, a heart-rate
/// strap, through RideLink (<c>tools/RideLink</c>). Nothing else in the game touches RideLink.
///
/// <para>
/// RideLink calls back on Bluetooth threads; every sample goes through a <see cref="QueuedDispatcher"/>
/// drained in <see cref="_Process"/>, so <see cref="Watts"/> and the rest are only ever written on the
/// main thread. Scans, connects and control writes are tasks whose outcomes come back the same way.
/// </para>
///
/// <para>
/// Transports: Windows Bluetooth (<c>TRAINER_BLE</c>, the Windows build only) and, with
/// <c>--trainer-sim</c>, RideLink's simulated rig (a trainer, a strap and a power meter speaking real
/// GATT), which is how the checks ride with no hardware.
/// </para>
/// </summary>
public sealed partial class TrainerLink : Node
{
    public const string NodeName = "TrainerLink";
    private const string File = "user://trainer.json";
    /// <summary>A reading older than this is a dropped link, not a rider at 0 W.</summary>
    private const double StaleAfter = 3.0;

    public static TrainerLink? Instance { get; private set; }

    private readonly QueuedDispatcher _dispatcher = new();
    private readonly ConcurrentQueue<Action> _main = new();
    private RideLinkEngine? _engine;
    private readonly List<IDisposable> _subscriptions = new();
    private CancellationTokenSource? _scan;
    private IControllableResistance? _controlled;
    private bool _controlAsked;
    private readonly GradeFeed _grade = new();

    public TrainerPrefs Prefs { get; private set; } = new();

    /// <summary>Whether any transport exists on this build (Bluetooth on Windows, or the simulator).</summary>
    public bool Available => HasBle || Simulated;

    /// <summary>This build carries the Windows Bluetooth transport.</summary>
#if TRAINER_BLE
    public const bool HasBle = true;
#else
    public const bool HasBle = false;
#endif
    private static readonly bool Simulated = CmdArgs.Has("--trainer-sim");

    /// <summary>RideLink's simulated rig under <c>--trainer-sim</c>: what the checks read back (the grade its trainer was sent).</summary>
    public RideLink.Simulation.SimulatedTransport? SimRig { get; private set; }

    public bool Scanning => _scan != null;
    /// <summary>Fitness devices seen by the scan in progress or the last one, newest advertisement each.</summary>
    public IReadOnlyList<DiscoveredDevice> Found => _found;
    private readonly List<DiscoveredDevice> _found = new();

    /// <summary>Devices connected or connecting, by id, with their state as a word.</summary>
    public IReadOnlyDictionary<string, (string Name, ConnectionState State)> Devices => _devices;
    private readonly Dictionary<string, (string Name, ConnectionState State)> _devices = new();

    public int Watts => Live(_wattsAt) ? _watts : 0;
    public float? Cadence => Live(_cadenceAt) ? _cadence : null;
    public int? HeartRate => Live(_hrAt) ? _hr : null;
    /// <summary>Measured power is arriving: the bike rides on it, not on the keys.</summary>
    public bool PowerLive => Live(_wattsAt);
    public bool HasTrainer => _engine?.Router.PrimaryTrainer != null;
    /// <summary>The grade last sent to the trainer, after the difficulty; null when none was.</summary>
    public double? GradeSent => _grade.Sent;

    /// <summary>A sentence for the HUD: a connection made or lost, a control write refused or clamped.</summary>
    public event Action<string>? Said;

    private int _watts, _hr;
    private float _cadence;
    private double _wattsAt = double.NegativeInfinity, _cadenceAt = double.NegativeInfinity, _hrAt = double.NegativeInfinity;

    private static bool Live(double at) => RealClock.Now - at < StaleAfter;

    public override void _EnterTree()
    {
        Instance = this;
        Prefs = Load();
        _grade.Difficulty = Prefs.Difficulty / 100.0;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _scan?.Cancel();
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        // releasing the GATT handles matters: a leaked one keeps Windows holding the link, and the next connect fails
        try { _engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception e) { GD.PushWarning($"[trainer] closing: {e.Message}"); }
        _engine = null;
    }

    public override void _Process(double delta)
    {
        _dispatcher.Drain(maxItems: 64);
        while (_main.TryDequeue(out var a)) a();

        // a trainer appeared: take control once (RideLink re-arms it itself after a lost control)
        var trainer = _engine?.Router.PrimaryTrainer;
        if (trainer != _controlled)
        {
            _controlled = trainer;
            _controlAsked = false;
            _grade.Reset();
        }
        if (trainer != null && !_controlAsked)
        {
            _controlAsked = true;
            Run(() => trainer.RequestControlAsync(), "control");
        }
    }

    private List<IDeviceTransport> Transports()
    {
        var list = new List<IDeviceTransport>();
#if TRAINER_BLE
        list.Add(new RideLink.Bluetooth.Windows.WindowsBleTransport());
#endif
        if (Simulated) list.Add(SimRig = RideLink.Simulation.SimulatedTransport.CreateDefaultRig());
        return list;
    }

    private RideLinkEngine? Engine()
    {
        if (_engine != null) return _engine;
        var transports = Transports();
        if (transports.Count == 0) return null;
        _engine = new RideLinkEngine(transports, new RideLinkOptions { Dispatcher = _dispatcher });
        var r = _engine.Router;
        _subscriptions.Add(r.Power.Subscribe(new Sink<PowerSample>(s => { _watts = s.Watts; _wattsAt = RealClock.Now; })));
        _subscriptions.Add(r.Cadence.Subscribe(new Sink<CadenceSample>(s => { _cadence = (float)s.Rpm; _cadenceAt = RealClock.Now; })));
        _subscriptions.Add(r.HeartRate.Subscribe(new Sink<HeartRateSample>(s => { _hr = s.Bpm; _hrAt = RealClock.Now; })));
        _subscriptions.Add(_engine.ConnectionEvents.Subscribe(new Sink<DeviceConnectionEvent>(e => _main.Enqueue(() => OnConnection(e)))));
        return _engine;
    }

    private void OnConnection(DeviceConnectionEvent e)
    {
        bool was = _devices.TryGetValue(e.DeviceId, out var before) && before.State == ConnectionState.Ready;
        _devices[e.DeviceId] = (e.DeviceName, e.State);
        if (e.State == ConnectionState.Ready && !was) Say($"{e.DeviceName} connected");
        else if (e.State == ConnectionState.Disconnected && was) Say($"{e.DeviceName} lost{(e.Detail is { } d ? $": {d}" : "")}");
        GD.Print($"[trainer] {e.DeviceName}: {e.State}{(e.Detail is { } detail ? $" ({detail})" : "")}");
    }

    /// <summary>Looks for fitness devices for <paramref name="seconds"/> (filling <see cref="Found"/>).</summary>
    public void Scan(double seconds = 12)
    {
        if (Engine() is not { } engine || _scan != null) return;
        _found.Clear();
        var cts = _scan = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Task.Run(async () =>
        {
            try
            {
                await foreach (var d in engine.ScanAsync(ScanFilter.All, cts.Token))
                {
                    // Windows learns a name only after a connect, so most come nameless: the services
                    // say what a thing is, and skipping those with none keeps your headphones off the list
                    if (BleUuids.DescribeServices(d.AdvertisedServices).Length == 0 && d.Transport != TransportKind.Simulated) continue;
                    _main.Enqueue(() =>
                    {
                        int i = _found.FindIndex(f => f.Id == d.Id);
                        if (i >= 0) _found[i] = d; else _found.Add(d);
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _main.Enqueue(() => Say($"Scan failed: {e.Message}")); }
            finally { _main.Enqueue(() => { if (_scan == cts) _scan = null; }); }
        });
    }

    /// <summary>"Kickr Core: trainer, power" for a list row.</summary>
    public static string Describe(DiscoveredDevice d)
    {
        string roles = BleUuids.DescribeServices(d.AdvertisedServices);
        string name = string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name;
        return roles.Length > 0 ? $"{name}: {roles}" : name;
    }

    public void Connect(DiscoveredDevice d)
    {
        if (Engine() is not { } engine || _devices.ContainsKey(d.Id)) return;
        _devices[d.Id] = (d.Name, ConnectionState.Connecting);
        Remember(d);
        Task.Run(async () =>
        {
            try { await engine.ConnectAsync(d); }
            catch (Exception e) { _main.Enqueue(() => { _devices.Remove(d.Id); Say($"{d.Name}: {e.Message}"); }); }
        });
    }

    /// <summary>Connects every remembered device not already up (no scan: the transports open them by id).</summary>
    public void Reconnect()
    {
        foreach (var k in Prefs.Devices)
            if (!_devices.ContainsKey(k.Id))
                Connect(new DiscoveredDevice(k.Id, k.Name, k.Transport, null, k.Services, new Dictionary<string, string>()));
    }

    public void Forget()
    {
        Prefs.Devices.Clear();
        Save();
    }

    private void Remember(DiscoveredDevice d)
    {
        Prefs.Devices.RemoveAll(k => k.Id == d.Id);
        Prefs.Devices.Add(new KnownDevice { Id = d.Id, Name = d.Name, Transport = d.Transport, Services = d.AdvertisedServices.ToList() });
        Save();
    }

    /// <summary>Trainer difficulty in percent, 0..100, saved.</summary>
    public int Difficulty
    {
        get => Prefs.Difficulty;
        set
        {
            Prefs.Difficulty = Math.Clamp(value, 0, 100);
            _grade.Difficulty = Prefs.Difficulty / 100.0;
            _grade.Reset();
            Save();
        }
    }

    /// <summary>The road's grade under the rider (a fraction), sent to the trainer as SIM when the feed says so.</summary>
    public void FeedGrade(double roadGrade)
    {
        if (_engine?.Router.PrimaryTrainer is not { } trainer) return;
        if (_grade.Next(roadGrade, RealClock.Now) is not { } grade) return;
        // Crr as the game's bike rolls (Bicycle.RollingResistance), so the trainer and the game agree on the road
        Run(() => trainer.SetSimulationAsync(new SimulationParameters(grade, RollingResistanceCoefficient: 0.005)), "grade");
    }

    /// <summary>The trainer back to flat, at the end of a training.</summary>
    public void Flat()
    {
        _grade.Reset();
        if (_engine?.Router.PrimaryTrainer is { } trainer)
            Run(() => trainer.SetSimulationAsync(new SimulationParameters(0, RollingResistanceCoefficient: 0.005)), "flat");
    }

    public void AddRecent(string path)
    {
        Prefs.Recent.Remove(path);
        Prefs.Recent.Insert(0, path);
        if (Prefs.Recent.Count > 8) Prefs.Recent.RemoveRange(8, Prefs.Recent.Count - 8);
        Save();
    }

    /// <summary>A control write, its failure or clamp told once.</summary>
    private void Run(Func<Task<ControlResult>> write, string what)
    {
        Task.Run(async () =>
        {
            try
            {
                var r = await write();
                if (!r.Success) _main.Enqueue(() => Say($"Trainer refused the {what}: {r.Message}"));
                else if (r.Message is { Length: > 0 } note && what != "grade") _main.Enqueue(() => Say($"Trainer: {note}"));
            }
            catch (Exception e) { _main.Enqueue(() => Say($"Trainer {what}: {e.Message}")); }
        });
    }

    private void Say(string text)
    {
        GD.Print($"[trainer] {text}");
        Said?.Invoke(text);
    }

    private static TrainerPrefs Load()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(File);
            if (System.IO.File.Exists(path)) return JsonSerializer.Deserialize<TrainerPrefs>(System.IO.File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) { GD.PushWarning($"[trainer] could not read {File}: {e.Message}"); }
        return new();
    }

    private void Save()
    {
        try { JsonStore.Save(File, Prefs, JsonStore.Indented); }
        catch (Exception e) { GD.PushWarning($"[trainer] could not save {File}: {e.Message}"); }
    }

    /// <summary>An observer from a lambda, without taking System.Reactive's extension methods into the game.</summary>
    private sealed class Sink<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) => GD.PushWarning($"[trainer] stream: {error.Message}");
        public void OnCompleted() { }
    }
}
