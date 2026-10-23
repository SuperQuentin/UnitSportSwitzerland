using Microsoft.Extensions.Logging;
using RideLink;
using RideLink.Bluetooth.Windows;
using RideLink.Ble;
using RideLink.Capabilities;
using RideLink.Model;
using RideLink.Simulation;
using RideLink.Transport;

namespace RideLink.Demo;

/// <summary>
/// Standalone console harness for the engine.
/// <para>
/// It uses nothing but the public API a game would use: build an engine over some transports,
/// scan, connect, subscribe to the router, send control commands. Running it against
/// <c>--sim</c> proves the whole stack with no hardware; running it against <c>--ble</c>
/// proves the same code against a real trainer.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = DemoOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.ProbeAdvertisements)
        {
            await AdvertisementProbe.RunAsync(
                TimeSpan.FromSeconds(options.ScanSeconds), Console.WriteLine);
            return 0;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning)
            .AddSimpleConsole(c => { c.SingleLine = true; c.TimestampFormat = "HH:mm:ss "; }));

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

        var transports = BuildTransports(options, loggerFactory);

        await using var engine = new RideLinkEngine(
            transports,
            new RideLinkOptions
            {
                // Inline delivery is right for a console: no UI thread to protect, and it
                // keeps output ordering obvious. A Godot host would pass a QueuedDispatcher.
                Dispatcher = RideLink.Threading.ImmediateDispatcher.Instance,
                WheelCircumferenceMeters = options.WheelCircumferenceMeters,
            },
            loggerFactory);

        Console.WriteLine($"RideLink demo — transports: {string.Join(", ", transports.Select(t => t.Name))}");
        Console.WriteLine();

        var chosen = await PickDevicesAsync(engine, options, shutdown.Token);
        if (chosen.Count == 0)
        {
            Console.WriteLine("No devices selected. Nothing to do.");
            return 1;
        }

        foreach (var device in chosen)
        {
            Console.Write($"Connecting to {device.Name}... ");
            try
            {
                var managed = await engine.ConnectAsync(device, shutdown.Token);
                Console.WriteLine($"ok — {managed.Capabilities.Count} capability stream(s)");
                DescribeCapabilities(managed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"failed: {ex.Message}");
            }
        }

        Console.WriteLine();
        var display = new LiveDisplay(engine.Router);
        using var subscriptions = display.Subscribe();

        PrintCommands();
        await RunCommandLoopAsync(engine, display, shutdown);

        Console.WriteLine("\nShutting down...");
        return 0;
    }

    private static IReadOnlyList<IDeviceTransport> BuildTransports(
        DemoOptions options, ILoggerFactory loggerFactory)
    {
        var transports = new List<IDeviceTransport>();

        if (options.UseSimulator)
        {
            IRideProfile profile = options.CsvPath is { } path
                ? CsvRideProfile.Load(path)
                : options.Ramp
                    ? new RampRideProfile(100, 320, TimeSpan.FromMinutes(4))
                    : new SteadyRideProfile();

            var simulator = SimulatedTransport.CreateDefaultRig(profile);

            // A trainer with SIM but no ERG, so the "report the gap" path is visible in the demo.
            if (options.IncludeErgLessTrainer)
                simulator.Add(new SimulatedTrainer(
                    "sim-trainer-noerg", "RideLink Sim Trainer (no ERG)", profile)
                { SupportsErg = false });

            transports.Add(simulator);
        }

        if (options.UseBluetooth)
            transports.Add(new WindowsBleTransport(loggerFactory.CreateLogger<WindowsBleTransport>()));

        return transports;
    }

    private static async Task<List<DiscoveredDevice>> PickDevicesAsync(
        RideLinkEngine engine, DemoOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Scanning for {options.ScanSeconds}s... (Ctrl+C to stop early)");

        var seen = new Dictionary<string, DiscoveredDevice>();
        using var scanTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scanTimeout.CancelAfter(TimeSpan.FromSeconds(options.ScanSeconds));

        try
        {
            await foreach (var device in engine.ScanAsync(ScanFilter.All, scanTimeout.Token))
            {
                // Windows only learns a BLE local name after connecting or pairing, so most
                // advertisements arrive nameless. The advertised service UUIDs are what say
                // what the thing actually is, and skipping anything with none of them is what
                // stops the list offering you your headphones.
                string roles = BleUuids.DescribeServices(device.AdvertisedServices);
                if (roles.Length == 0 && !options.ShowAllDevices) continue;

                if (seen.TryAdd(device.Id, device))
                    Console.WriteLine($"  [{seen.Count,2}] {Label(device, roles)}");
                else
                    seen[device.Id] = device;   // keep the freshest advertisement
            }
        }
        catch (OperationCanceledException) { }

        if (seen.Count == 0)
        {
            Console.WriteLine(options.ShowAllDevices
                ? "  nothing advertising at all — is Bluetooth on?"
                : "  no fitness devices found. Wake the sensor (a heart rate strap needs damp"
                  + " contacts and a moment on-body), and check it is not already connected"
                  + " to a watch or phone. --all lists every BLE device regardless.");
            return [];
        }

        var ordered = seen.Values.ToList();
        Console.WriteLine();
        Console.WriteLine("Pick devices by number, space separated (e.g. \"1 2\"), or Enter for all:");
        Console.Write("> ");

        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) return ordered;

        var picked = new List<DiscoveredDevice>();
        foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out int index) && index >= 1 && index <= ordered.Count)
                picked.Add(ordered[index - 1]);
            else
                Console.WriteLine($"  ignoring '{token}'");
        }

        return picked;
    }

    /// <summary>Name if the device gave one, otherwise what its services say it is.</summary>
    private static string Label(DiscoveredDevice device, string roles)
    {
        string name = string.IsNullOrWhiteSpace(device.Name) ? "(unnamed)" : device.Name;
        string rssi = device.Rssi is { } r ? $"{r,4} dBm" : "";
        // raw UUIDs when we recognise nothing, so an unexpected sensor is still diagnosable
        string detail = roles.Length > 0
            ? roles
            : string.Join(",", device.AdvertisedServices
                .Select(u => Guid.TryParse(u, out var g) && BleUuids.TryGetShort(g, out ushort sh)
                    ? $"0x{sh:X4}" : u)
                .Take(4));

        return $"{name,-22} {detail,-24} {rssi}  [{device.Id}]";
    }

    private static void DescribeCapabilities(ManagedDevice managed)
    {
        foreach (var capability in managed.Capabilities)
        {
            var kinds = new List<string>();
            if (capability is IPowerSource) kinds.Add("power");
            if (capability is ICadenceSource) kinds.Add("cadence");
            if (capability is IHeartRateSource) kinds.Add("hr");
            if (capability is ISpeedSource) kinds.Add("speed");
            if (capability is IDistanceSource) kinds.Add("distance");
            if (capability is IBatterySource) kinds.Add("battery");

            if (capability is IControllableResistance trainer)
            {
                var modes = new List<string>();
                if (trainer.Capabilities.SupportsSimulation) modes.Add("SIM");
                if (trainer.Capabilities.SupportsErg) modes.Add("ERG");
                if (trainer.Capabilities.SupportsResistance) modes.Add("Resistance");

                kinds.Add(modes.Count > 0
                    ? $"control[{string.Join('+', modes)}]"
                    : "control[none advertised]");

                if (!trainer.Capabilities.SupportsErg)
                    Console.WriteLine(
                        $"      note: {trainer.DeviceName} does not advertise ERG (Set Target Power)");

                if (trainer.Capabilities.PowerRange is { } range)
                    Console.WriteLine(
                        $"      ERG range {range.MinimumWatts}-{range.MaximumWatts} W "
                        + $"(step {range.MinimumIncrementWatts} W)");
            }

            Console.WriteLine($"      {capability.Source.Channel}: {string.Join(", ", kinds)}");
        }
    }

    private static void PrintCommands()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  g <percent>   SIM mode — set grade, e.g. 'g 4.5' or 'g -2'");
        Console.WriteLine("  e <watts>     ERG mode — hold a target, e.g. 'e 250'");
        Console.WriteLine("  r <level>     Resistance mode — raw level, e.g. 'r 20'");
        Console.WriteLine("  reset         return the trainer to its default state");
        Console.WriteLine("  hex           toggle raw packet logging");
        Console.WriteLine("  dump          print the last 20 raw packets");
        Console.WriteLine("  s             one-shot status line");
        Console.WriteLine("  q             quit");
        Console.WriteLine();
    }

    private static async Task RunCommandLoopAsync(
        RideLinkEngine engine, LiveDisplay display, CancellationTokenSource shutdown)
    {
        var reader = Task.Run(() =>
        {
            while (!shutdown.IsCancellationRequested)
            {
                string? line = Console.ReadLine();
                if (line is null) { shutdown.Cancel(); return; }
                _ = HandleAsync(line.Trim());
            }
        });

        try
        {
            // The display prints on its own timer; this just parks until Ctrl+C or 'q'.
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException) { }

        await Task.WhenAny(reader, Task.Delay(200));

        async Task HandleAsync(string command)
        {
            if (command.Length == 0) return;

            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();

            var trainer = engine.Router.PrimaryTrainer;

            try
            {
                switch (verb)
                {
                    case "q" or "quit" or "exit":
                        shutdown.Cancel();
                        return;

                    case "s":
                        Console.WriteLine(display.StatusLine());
                        Console.WriteLine(display.SourceLine());
                        return;

                    case "hex":
                        bool enable = !engine.PacketLog.IsEnabled;
                        engine.PacketLog.SetEnabled(null, enable);
                        Console.WriteLine($"raw packet logging {(enable ? "on" : "off")}");
                        return;

                    case "dump":
                        foreach (var record in engine.PacketLog.Recent(20))
                            Console.WriteLine("  " + record);
                        return;
                }

                if (trainer is null)
                {
                    Console.WriteLine("No controllable trainer connected.");
                    return;
                }

                ControlResult result = verb switch
                {
                    "g" when TryNumber(parts, out double percent) =>
                        await trainer.SetSimulationAsync(
                            new SimulationParameters(percent / 100.0)),

                    "e" when TryNumber(parts, out double watts) =>
                        await trainer.SetTargetPowerAsync((int)Math.Round(watts)),

                    "r" when TryNumber(parts, out double level) =>
                        await trainer.SetResistanceLevelAsync(level),

                    "reset" => await trainer.ResetAsync(),

                    _ => ControlResult.Fail(ControlFailure.None, $"unknown command '{command}'"),
                };

                Console.WriteLine(result.Success
                    ? $"  -> {trainer.Mode}: {result}" + (result.Message is { } m ? $" — {m}" : "")
                    : $"  -> refused: {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> error: {ex.Message}");
            }
        }

        static bool TryNumber(string[] parts, out double value)
        {
            value = 0;
            return parts.Length >= 2
                && double.TryParse(
                    parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            RideLink console demo

              ridelink [--sim] [--ble] [--csv <file>] [--ramp] [--no-erg]
                       [--scan <seconds>] [--wheel <metres>] [--all] [-v]

              --sim            use the simulated transport (default when nothing is given)
              --ble            use the Windows BLE transport
              --csv <file>     drive the simulator from a recorded session
              --ramp           drive the simulator with a 100 -> 320 W ramp
              --no-erg         also advertise a SIM-only trainer, to see the ERG gap reported
              --scan <s>       scan duration, default 5
              --wheel <m>      wheel circumference for wheel-based speed, default 2.096
              --all            list every BLE device, not just fitness sensors
              --probe          dump raw advertisement contents and exit
              -v               verbose logging

            Both --sim and --ble may be given at once; the engine scans them in parallel.
            """);
    }
}

/// <summary>Command-line options for the demo.</summary>
internal sealed record DemoOptions
{
    public bool UseSimulator { get; init; }
    public bool UseBluetooth { get; init; }
    public bool Ramp { get; init; }
    public bool IncludeErgLessTrainer { get; init; }
    public bool Verbose { get; init; }

    /// <summary>List every BLE device, not just those advertising a fitness service.</summary>
    public bool ShowAllDevices { get; init; }

    /// <summary>Dump raw BLE advertisement contents and exit.</summary>
    public bool ProbeAdvertisements { get; init; }
    public bool ShowHelp { get; init; }
    public string? CsvPath { get; init; }
    public int ScanSeconds { get; init; } = 5;
    public double WheelCircumferenceMeters { get; init; } = 2.096;

    public static DemoOptions Parse(string[] args)
    {
        bool sim = false, ble = false, ramp = false, noErg = false, verbose = false, help = false;
        string? csv = null;
        int scan = 5;
        double wheel = 2.096;
        bool showAll = false;
        bool probe = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--sim": sim = true; break;
                case "--ble": ble = true; break;
                case "--ramp": ramp = true; break;
                case "--no-erg": noErg = true; break;
                case "--all": showAll = true; break;
                case "--probe": probe = true; break;
                case "-v" or "--verbose": verbose = true; break;
                case "-h" or "--help": help = true; break;
                case "--csv" when i + 1 < args.Length: csv = args[++i]; break;
                case "--scan" when i + 1 < args.Length: int.TryParse(args[++i], out scan); break;
                case "--wheel" when i + 1 < args.Length:
                    double.TryParse(
                        args[++i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out wheel);
                    break;
            }
        }

        // Default to the simulator: the demo must do something useful with no hardware.
        if (!sim && !ble) sim = true;

        return new DemoOptions
        {
            UseSimulator = sim,
            UseBluetooth = ble,
            Ramp = ramp,
            IncludeErgLessTrainer = noErg,
            Verbose = verbose,
            ShowAllDevices = showAll,
            ProbeAdvertisements = probe,
            ShowHelp = help,
            CsvPath = csv,
            ScanSeconds = Math.Clamp(scan, 1, 120),
            WheelCircumferenceMeters = wheel <= 0 ? 2.096 : wheel,
        };
    }
}

/// <summary>
/// Subscribes to the router and prints a live line. Deliberately reads only from the router,
/// never from a device, which is the whole point of the capability layer.
/// </summary>
internal sealed class LiveDisplay(CapabilityRouter router)
{
    private int _watts;
    private double _cadence;
    private int _heartRate;
    private double _speed;
    private double _distance;
    private DateTimeOffset _lastPrint = DateTimeOffset.MinValue;

    public IDisposable Subscribe()
    {
        var subscriptions = new List<IDisposable>
        {
            router.Power.Subscribe(s => { _watts = s.Watts; MaybePrint(); }),
            router.Cadence.Subscribe(s => { _cadence = s.Rpm; }),
            router.HeartRate.Subscribe(s => { _heartRate = s.Bpm; }),
            router.Speed.Subscribe(s => { _speed = s.MetersPerSecond; }),
            router.Distance.Subscribe(s => { _distance = s.Meters; }),

            // The handover message is the visible proof that the router is doing its job.
            // The handover message is only useful if it says WHICH capability won. A trainer
            // routinely offers the same quantity twice — the Direto XR publishes power over
            // both FTMS and the Cycling Power Service — and naming just the device makes two
            // different sources print an identical line.
            router.ActiveSourceChanges.Subscribe(change =>
            {
                _active[change.Quantity] = change.Current.Channel;
                Console.WriteLine($"  [router] {change.Quantity,-9} <- {change.DeviceName} "
                                + $"({change.Current.Channel})");
            }),
        };

        return new Composite(subscriptions);
    }

    private readonly Dictionary<string, string> _active = new();

    public string StatusLine() =>
        $"{_watts,4} W | {_cadence,5:0} rpm | {_heartRate,3} bpm | "
        + $"{_speed * 3.6,5:0.0} km/h | {_distance / 1000,6:0.00} km";

    /// <summary>Which capability currently feeds each quantity.</summary>
    public string SourceLine() => _active.Count == 0
        ? "  no active sources yet"
        : "  " + string.Join("   ", _active.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={kv.Value}"));

    private void MaybePrint()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPrint < TimeSpan.FromSeconds(1)) return;
        _lastPrint = now;
        Console.WriteLine("  " + StatusLine());
    }

    private sealed class Composite(List<IDisposable> parts) : IDisposable
    {
        public void Dispose()
        {
            foreach (var part in parts) part.Dispose();
        }
    }
}
