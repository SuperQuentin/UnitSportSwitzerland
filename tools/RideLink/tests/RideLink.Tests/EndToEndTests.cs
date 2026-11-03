using RideLink.Capabilities;
using RideLink.Simulation;
using RideLink.Threading;
using RideLink.Transport;
using Xunit;

namespace RideLink.Tests;

/// <summary>
/// The whole stack over the simulated transport: scan, connect, discover, subscribe, parse,
/// route, control.
/// <para>
/// The simulated devices encode real GATT payloads and the engine decodes them with the same
/// parsers it uses on hardware, so these exercise the actual pipeline rather than a shortcut
/// around it — which is what makes "prove it works with no hardware" meaningful.
/// </para>
/// </summary>
public class SimulatedRigTests
{
    private static async Task<RideLinkEngine> StartAsync(
        SimulatedTransport transport, RideLinkOptions? options = null)
    {
        var engine = new RideLinkEngine([transport], options ?? FastOptions());

        var devices = new List<DiscoveredDevice>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var device in engine.ScanAsync(ScanFilter.All, cts.Token))
            devices.Add(device);

        foreach (var device in devices) await engine.ConnectAsync(device, cts.Token);
        return engine;
    }

    private static RideLinkOptions FastOptions() => new()
    {
        Dispatcher = ImmediateDispatcher.Instance,
        AutoReconnect = false,
    };

    /// <summary>Waits for a predicate, so tests do not depend on notification timing.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task Scan_finds_the_default_rig()
    {
        await using var transport = SimulatedTransport.CreateDefaultRig();
        await using var engine = new RideLinkEngine([transport], FastOptions());

        var found = new List<DiscoveredDevice>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var device in engine.ScanAsync(ScanFilter.All, cts.Token))
            found.Add(device);

        Assert.Equal(3, found.Count);
        Assert.Contains(found, d => d.Name.Contains("Trainer"));
        Assert.Contains(found, d => d.Name.Contains("HRM"));
        Assert.Contains(found, d => d.Name.Contains("Power Meter"));
    }

    [Fact]
    public async Task Scan_filter_narrows_by_advertised_service()
    {
        await using var transport = SimulatedTransport.CreateDefaultRig();
        await using var engine = new RideLinkEngine([transport], FastOptions());

        var heartRateService = EndpointId.Ble(RideLink.Ble.BleUuids.HeartRateService, 0).Service;

        var found = new List<DiscoveredDevice>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var device in engine.ScanAsync(
            new ScanFilter { Services = [heartRateService] }, cts.Token))
        {
            found.Add(device);
        }

        Assert.Single(found);
        Assert.Contains("HRM", found[0].Name);
    }

    [Fact]
    public async Task Measurements_flow_through_the_router()
    {
        await using var transport = SimulatedTransport.CreateDefaultRig(new SteadyRideProfile(220, 90, 150));
        await using var engine = await StartAsync(transport);

        int watts = 0, bpm = 0;
        double rpm = 0, speed = 0;

        using var subs = new Subscriptions(
            engine.Router.Power.Subscribe(s => watts = s.Watts),
            engine.Router.Cadence.Subscribe(s => rpm = s.Rpm),
            engine.Router.HeartRate.Subscribe(s => bpm = s.Bpm),
            engine.Router.Speed.Subscribe(s => speed = s.MetersPerSecond));

        Assert.True(await WaitForAsync(() => watts > 0 && bpm > 0 && rpm > 0 && speed > 0),
            $"watts={watts} bpm={bpm} rpm={rpm} speed={speed}");

        Assert.InRange(watts, 200, 240);
        Assert.InRange(bpm, 140, 160);
        Assert.InRange(rpm, 80, 100);
    }

    [Fact]
    public async Task Power_meter_outranks_the_trainer_end_to_end()
    {
        await using var transport = SimulatedTransport.CreateDefaultRig();
        await using var engine = await StartAsync(transport);

        var sources = new HashSet<string>();
        using var sub = engine.Router.Power.Subscribe(s => sources.Add(s.Source.Channel));

        // Let both devices notify several times, then check who is actually being forwarded.
        await Task.Delay(1500);
        sources.Clear();
        await Task.Delay(1500);

        Assert.Contains(Channels.CyclingPower, sources);
        Assert.DoesNotContain(Channels.FtmsIndoorBike, sources);
    }

    [Fact]
    public async Task Erg_command_makes_the_trainer_hold_the_target()
    {
        await using var transport = SimulatedTransport.CreateDefaultRig(new SteadyRideProfile(150, 85, 130));
        await using var engine = await StartAsync(transport);

        var trainer = engine.Router.PrimaryTrainer;
        Assert.NotNull(trainer);
        Assert.True(trainer!.Capabilities.SupportsErg);

        var result = await trainer.SetTargetPowerAsync(300);
        Assert.True(result.Success, result.ToString());
        Assert.Equal(ControlMode.Erg, trainer.Mode);

        // The trainer's own power stream is what has to move; the router prefers the power
        // meter, so subscribe to the trainer directly for this assertion.
        var machine = (RideLink.Ble.FtmsMachineCapability)engine.Devices
            .SelectMany(d => d.Capabilities)
            .First(c => c is RideLink.Ble.FtmsMachineCapability);

        int watts = 0;
        using var sub = machine.Power.Subscribe(s => watts = s.Watts);

        Assert.True(await WaitForAsync(() => watts >= 295), $"watts settled at {watts}");
    }

    [Fact]
    public async Task Sim_grade_changes_the_speed_the_trainer_reports()
    {
        await using var transport = SimulatedTransport.CreateDefaultRig(new SteadyRideProfile(200, 90, 140));
        await using var engine = await StartAsync(transport);

        var trainer = engine.Router.PrimaryTrainer!;
        Assert.True(trainer.Capabilities.SupportsSimulation);

        double speed = 0;
        using var sub = engine.Router.Speed.Subscribe(s => speed = s.MetersPerSecond);

        Assert.True(await WaitForAsync(() => speed > 0));
        await Task.Delay(600);
        double flatSpeed = speed;

        // Same rider effort, now up a 6% hill: the physics must slow them down.
        var result = await trainer.SetSimulationAsync(new SimulationParameters(0.06));
        Assert.True(result.Success, result.ToString());
        Assert.Equal(ControlMode.Simulation, trainer.Mode);

        Assert.True(await WaitForAsync(() => speed < flatSpeed * 0.7),
            $"flat {flatSpeed:0.00} m/s, climbing {speed:0.00} m/s");
    }

    [Fact]
    public async Task A_trainer_without_erg_reports_the_gap_instead_of_failing_silently()
    {
        await using var transport = new SimulatedTransport()
            .Add(new SimulatedTrainer("no-erg", "SIM only trainer") { SupportsErg = false });

        await using var engine = await StartAsync(transport);

        var trainer = engine.Router.PrimaryTrainer!;
        Assert.False(trainer.Capabilities.SupportsErg);
        Assert.True(trainer.Capabilities.SupportsSimulation);

        var result = await trainer.SetTargetPowerAsync(250);

        Assert.False(result.Success);
        Assert.Equal(ControlFailure.NotSupported, result.Failure);
        Assert.Contains("ERG", result.Message);

        // SIM still works on the same device.
        Assert.True((await trainer.SetSimulationAsync(new SimulationParameters(0.03))).Success);
    }

    [Fact]
    public async Task Erg_targets_are_clamped_to_the_advertised_range()
    {
        await using var transport = new SimulatedTransport()
            .Add(new SimulatedTrainer("clamped", "Narrow range trainer")
            {
                PowerRange = new RideLink.Ble.Parsers.PowerRange(50, 400, 1),
            });

        await using var engine = await StartAsync(transport);
        var trainer = engine.Router.PrimaryTrainer!;

        // Without clamping this would be written to hardware and rejected as an invalid
        // parameter; with clamping it becomes a successful 400 W with an explanation.
        var result = await trainer.SetTargetPowerAsync(2000);

        Assert.True(result.Success, result.ToString());
        Assert.Equal(400, result.AppliedValue);
        Assert.Contains("clamped", result.Message);
    }

    [Fact]
    public async Task Control_commands_are_refused_until_the_handshake_has_run()
    {
        // The simulated trainer enforces Request Control exactly as the spec requires, so a
        // capability that skipped the handshake would fail here.
        await using var transport = new SimulatedTransport().Add(new SimulatedTrainer());
        await using var engine = await StartAsync(transport);

        var trainer = engine.Router.PrimaryTrainer!;

        // SetTargetPowerAsync runs the handshake itself; this proves it actually does.
        var result = await trainer.SetTargetPowerAsync(200);
        Assert.True(result.Success, result.ToString());
    }

    [Fact]
    public async Task Battery_and_device_information_are_surfaced()
    {
        await using var transport = new SimulatedTransport().Add(new SimulatedTrainer());
        await using var engine = await StartAsync(transport);

        var device = Assert.Single(engine.Devices);
        Assert.Equal("RideLink", device.Metadata.Manufacturer);
        Assert.Equal("Sim Trainer 1", device.Metadata.Model);

        var battery = device.Capabilities.OfType<IBatterySource>().Single();
        Assert.Equal(78, ((RideLink.Ble.BatteryCapability)battery).LastPercent);
    }

    [Fact]
    public async Task Raw_packet_logging_captures_hex_when_enabled()
    {
        await using var transport = new SimulatedTransport().Add(new SimulatedTrainer());
        await using var engine = new RideLinkEngine([transport], FastOptions());

        engine.PacketLog.SetEnabled(null, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var device in engine.ScanAsync(ScanFilter.All, cts.Token))
            await engine.ConnectAsync(device, cts.Token);

        Assert.True(await WaitForAsync(() => engine.PacketLog.Recent().Count > 0));

        var record = engine.PacketLog.Recent().Last();
        Assert.NotEmpty(record.Hex);
    }

    [Fact]
    public async Task Queued_dispatcher_defers_delivery_until_the_host_pumps()
    {
        var dispatcher = new QueuedDispatcher();

        await using var transport = new SimulatedTransport().Add(new SimulatedTrainer());
        await using var engine = await StartAsync(transport, new RideLinkOptions
        {
            Dispatcher = dispatcher,
            AutoReconnect = false,
        });

        int delivered = 0;
        using var sub = engine.Router.Power.Subscribe(_ => delivered++);

        // The trainer notifies on a background timer. Let several notifications land: with a
        // queued dispatcher not one of them may reach the subscriber, because nothing has
        // pumped the queue. This is the property a game engine relies on to stay on its
        // main thread.
        await Task.Delay(1200);

        Assert.True(dispatcher.PendingCount > 0, "nothing was queued at all");
        Assert.Equal(0, delivered);

        // Now pump, exactly as a host would from its frame callback.
        int ran = dispatcher.Drain();

        Assert.True(ran > 0);
        Assert.True(delivered > 0, $"drained {ran} callback(s) but no power sample arrived");
    }

    [Fact]
    public async Task Csv_replay_drives_the_rig()
    {
        var profile = CsvRideProfile.Parse(
        [
            "seconds,power,cadence,hr",
            "0,100,80,120",
            "10,200,90,140",
            "20,300,100,160",
        ]);

        Assert.Equal(3, profile.RowCount);

        // Interpolated between rows, so a 1 Hz recording drives a 4 Hz notification rate.
        Assert.Equal(150, profile.Sample(TimeSpan.FromSeconds(5)).PowerWatts);
        Assert.Equal(85, profile.Sample(TimeSpan.FromSeconds(5)).CadenceRpm, 3);

        // Before the first and after the last row it holds, rather than extrapolating.
        Assert.Equal(100, profile.Sample(TimeSpan.Zero).PowerWatts);
        Assert.Equal(300, profile.Sample(TimeSpan.FromMinutes(5)).PowerWatts);

        await using var transport = SimulatedTransport.CreateDefaultRig(profile);
        await using var engine = await StartAsync(transport);

        int watts = 0;
        using var sub = engine.Router.Power.Subscribe(s => watts = s.Watts);
        Assert.True(await WaitForAsync(() => watts > 0));
    }

    private sealed class Subscriptions(params IDisposable[] parts) : IDisposable
    {
        public void Dispose()
        {
            foreach (var part in parts) part.Dispose();
        }
    }
}
