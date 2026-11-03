# RideLink

A .NET class library that talks to indoor-cycling and fitness hardware over **Bluetooth Low
Energy** and **ANT+**, and hands the consuming application a clean, transport-agnostic,
capability-based API.

The engine has no UI and no game dependencies. It runs and is fully testable as a plain
console app with **no hardware at all**, via a simulated backend that speaks real GATT
payloads.

```bash
dotnet run --project demo/RideLink.Console -- --sim
```

---

## Architecture

Four layers, each unaware of the one below it.

```
                    ┌──────────────────────────────────────────┐
   your game  ────► │ CapabilityRouter                         │
                    │  IObservable<PowerSample> Power          │
                    │  IObservable<CadenceSample> Cadence      │  merges + prioritizes
                    │  IObservable<HeartRateSample> HeartRate  │  several devices per
                    │  IObservable<SpeedSample> Speed          │  quantity
                    │  IControllableResistance PrimaryTrainer  │
                    └──────────────────┬───────────────────────┘
                                       │ capabilities, not brands
                    ┌──────────────────┴───────────────────────┐
                    │ IPowerSource  ICadenceSource             │
                    │ IHeartRateSource  ISpeedSource           │
                    │ IDistanceSource  IBatterySource          │
                    │ IControllableResistance                  │
                    └──────────────────┬───────────────────────┘
                                       │ built by device sessions
                    ┌──────────────────┴───────────────────────┐
                    │ BleDeviceSession   FitnessEquipment-     │
                    │  (discovers GATT,   Capability (FE-C)    │
                    │   parses payloads)                       │
                    └──────────────────┬───────────────────────┘
                                       │ scan / connect / read / write / subscribe
                    ┌──────────────────┴───────────────────────┐
                    │ IDeviceTransport                         │
                    │  WindowsBleTransport   SimulatedTransport│
                    │  IAntChannelHost                         │
                    └──────────────────────────────────────────┘
```

**Nothing above `IDeviceTransport` knows which radio a device uses.** A trainer reached over
BLE FTMS and the same trainer reached over ANT+ FE-C produce the same `IControllableResistance`,
and `SetSimulationAsync(new SimulationParameters(0.05))` means "5% climb" to both.

### Projects

| Project | Target | Contents |
|---|---|---|
| `src/RideLink` | `net8.0` | Model, transports, parsers, capabilities, router, control, engine, diagnostics |
| `src/RideLink.Simulation` | `net8.0` | Virtual trainer / strap / power meter, GATT encoders, ride profiles, CSV replay |
| `src/RideLink.Bluetooth.Windows` | `net8.0-windows10.0.19041.0` | WinRT GATT transport |
| `src/RideLink.Ant` | `net8.0` | ANT+ channel config, data-page parsing, FE-C control pages, simulated host |
| `demo/RideLink.Console` | `net8.0-windows10.0.19041.0` | Standalone console harness |
| `tests/RideLink.Tests` | `net8.0` | 108 tests: parsers, rollover, router, ANT+, end-to-end |

### Stream API: `IObservable<T>`

Chosen over events and `Channel<T>` because the router's job *is* merging and switching between
streams, which Rx expresses directly. `System.Reactive` is a dependency of the core library.

Delivery thread is yours to choose — BLE and ANT callbacks arrive on arbitrary background
threads, so every publish goes through an injected `IEventDispatcher`:

| Dispatcher | Use |
|---|---|
| `ImmediateDispatcher` | console, tests — runs inline on the callback thread |
| `SynchronizationContextDispatcher` | posts to a captured `SynchronizationContext` |
| `QueuedDispatcher` | **game engines** — enqueues; you call `Drain()` once per frame |

```csharp
// Godot host sketch
private readonly QueuedDispatcher _dispatcher = new();

public override void _Ready()
{
    _engine = new RideLinkEngine([new WindowsBleTransport()],
        new RideLinkOptions { Dispatcher = _dispatcher });

    _engine.Router.Power.Subscribe(s => _watts = s.Watts);   // safe: raised on the main thread
}

public override void _Process(double delta) => _dispatcher.Drain(maxItems: 64);
```

### Units

Normalized at the parser boundary, never at the call site. Internally: **watts (int)**,
**rpm**, **bpm**, **m/s**, **metres**, **grade as a fraction** (0.05 = 5%). Every sample carries
a UTC timestamp and a `SourceId` of `(DeviceId, Transport, Channel)`.

---

## Source priority

When two devices both claim to know the watts, `SourcePriorityPolicy` decides. Defaults:

| Quantity | Preference | Why |
|---|---|---|
| Power | power meter → trainer | Trainers infer watts from a resistance curve and drift with temperature; pedals and cranks measure force |
| Cadence | dedicated sensor → power meter crank → trainer | Trainers infer cadence from flywheel ripple and are visibly wrong when soft-pedalling |
| Heart rate | worn strap → machine-relayed | A machine is repeating whatever it happened to pair with |
| Speed | trainer → wheel sensor | Indoors the trainer's speed reflects the simulated road; a wheel sensor on a wheel-off trainer reads zero |

A source that goes quiet for `StaleAfter` (5 s) stops being eligible, and the next-best source
takes over on its next sample. Subclass `SourcePriorityPolicy` to change any of it; the router
takes it as a constructor dependency.

`Router.ActiveSourceChanges` reports every handover.

---

## Control modes

Both BLE FTMS and ANT+ FE-C map onto the same three:

| Mode | Meaning | FTMS | FE-C |
|---|---|---|---|
| **SIM** | feel the hill: grade, wind, Crr, Cw | Set Indoor Bike Simulation Parameters `0x11` | Track Resistance `0x33` + Wind Resistance `0x32` |
| **ERG** | hold N watts regardless of cadence | Set Target Power `0x05` | Target Power `0x31` |
| **Resistance** | raw level, for trainers with no power control | Set Target Resistance Level `0x04` | Basic Resistance `0x30` |

```csharp
var trainer = engine.Router.PrimaryTrainer;

if (!trainer.Capabilities.SupportsErg)
    ShowMessage("This trainer has no ERG mode");     // known before any write

var result = await trainer.SetSimulationAsync(new SimulationParameters(GradeFraction: 0.05));
if (!result.Success) Log(result.Failure, result.Message);
```

Targets are **clamped to the device's own advertised range** before anything is written, and the
result reports what was actually applied:

```
Requested 2000 W clamped to 400 W by the machine's supported range
```

**The FTMS handshake is transactional and is handled for you.** `FtmsControlSession` serializes
writes behind a semaphore, runs Request Control → Reset before the first target, waits for the
indicated Response Code before treating a write as applied, and re-arms the handshake when
Fitness Machine Status reports `ControlPermissionLost` (which is what happens when someone
presses a button on the trainer itself).

### One caveat on Cycling Power Control Point `0x2A66`

The spec's op-codes for this characteristic are **sensor calibration only** — Set Cumulative
Value, Update Sensor Location, Set Crank Length. It has no resistance or ERG op-code. Every
real "legacy" trainer-control path over CPS was *vendor-specific* (Wahoo's unlock sequence,
Tacx, Elite each differ). RideLink therefore implements the standard CPCP transaction and
leaves a documented vendor-extension hook, rather than inventing op-codes no device implements.
FTMS and FE-C carry the real control paths.

---

## Platform / transport support matrix

| Platform | BLE | ANT+ | Status in this repo |
|---|---|---|---|
| Windows desktop | ✅ WinRT `Windows.Devices.Bluetooth` | ✅ USB stick | BLE implemented; ANT+ needs the adapter below |
| Linux / macOS desktop | ⚠️ needs a transport (Plugin.BLE or BlueZ/CoreBluetooth binding) | ✅ USB stick | not implemented |
| Android | ⚠️ needs a transport (Plugin.BLE / Shiny) | ⚠️ some devices only | not implemented |
| iOS | ⚠️ needs a transport | ❌ **never** — Apple permits no ANT+ hardware | not implemented |
| Simulator | ✅ | ✅ (`SimulatedAntChannelHost`) | fully implemented, no hardware needed |

### Why WinRT for the BLE backend

The FTMS Control Point needs explicit **indication** subscription and acknowledged writes, and
WinRT exposes both precisely. Binding it directly costs no third-party dependency and gives the
clearest path to validating against real trainers on Windows.

Cross-platform alternatives — **Plugin.BLE** (dotnet-bluetooth-le) and **Shiny.NET** — both cover
Android/iOS/macOS/Windows, and Shiny adds background operation and the peripheral role. Both
target the MAUI/Xamarin app model, so wiring them into Godot's .NET runtime on mobile is a
separate piece of work. Swapping one in means writing another `IDeviceTransport` and changing
**nothing** above it.

### ANT+ and the managed library

The official **ANT Managed Library** is licence-gated behind a thisisant.com account and is not
on NuGet, so it is not vendored here. Everything that does not need it is implemented and
tested: channel configuration (device types, 2457 MHz / RF channel 57, per-profile channel
periods), all data-page parsing, all FE-C control pages, and the capability mapping.

The seam is `IAntChannelHost` / `IAntChannel`. To go live:

1. Get an ANT USB stick — **ANTUSB-m** (`0fcf:1009`) or the older **ANTUSB2** (`0fcf:1008`);
   the Suunto Movestick Mini is the same silicon.
2. Download the ANT Managed Library from thisisant.com and reference `ANT_Managed_Library.dll`.
3. Implement `IAntChannelHost` over `ANT_Device`: open the device, set the ANT+ network key,
   then per channel assign type `0x00` (bidirectional receive), set the channel ID from
   `AntChannelConfig.DeviceType`, set RF frequency and channel period, and open it.
4. Pass it to `FitnessEquipmentCapability` / `AntHeartRateCapability` exactly as
   `SimulatedAntChannelHost` is passed in the tests.

Pairing by device number and wildcard search are both supported —
`AntProfiles.FitnessEquipment.PairedTo(deviceNumber)`.

---

## Running the console demo

```bash
# no hardware — virtual trainer, HR strap and power meter
dotnet run --project demo/RideLink.Console -- --sim

# real Bluetooth hardware on Windows
dotnet run --project demo/RideLink.Console -- --ble --scan 10

# both radios at once; the engine scans them in parallel
dotnet run --project demo/RideLink.Console -- --sim --ble

# replay a recorded session
dotnet run --project demo/RideLink.Console -- --sim --csv ride.csv

# see the "this trainer has no ERG" path
dotnet run --project demo/RideLink.Console -- --sim --no-erg
```

Pick devices by number, then:

| Command | Effect |
|---|---|
| `g 8` | SIM mode, 8% grade |
| `e 300` | ERG mode, 300 W target |
| `r 20` | Resistance mode, level 20 |
| `reset` | return the trainer to default |
| `hex` / `dump` | toggle raw packet capture / print the last 20 packets |
| `s` | one-shot status line |
| `q` | quit |

A real session against the simulator, showing the router picking sources and SIM changing the
speed for the same rider effort:

```
  [router] HeartRate <- RideLink Sim HRM
   182 W |     0 rpm | 140 bpm |   0.0 km/h |   0.00 km
  [router] Power <- RideLink Sim Power Meter
  [router] Cadence <- RideLink Sim Power Meter
   185 W |    89 rpm | 142 bpm |  33.2 km/h |   0.01 km
  -> Erg: ok (300)
   179 W |    89 rpm | 140 bpm |  39.7 km/h |   0.05 km
  -> Simulation: ok (8)
   181 W |    89 rpm | 140 bpm |   9.2 km/h |   0.09 km
```

### CSV replay format

Header row required, column order free. Recognised names:
`seconds`/`time`/`elapsed`, `power`/`watts`, `cadence`/`rpm`, `heartrate`/`hr`/`bpm`.
Values between rows are linearly interpolated, so a 1 Hz recording drives a 4 Hz notification
rate smoothly. Parsing is invariant-culture throughout.

```csv
seconds,power,cadence,hr
0,100,80,120
10,200,90,140
20,300,100,160
```

---

## Adding a new device profile

**A new BLE service.** Add its UUIDs to `BleUuids` with a spec citation, write a parser in
`Ble/Parsers` that walks a `SpanReader` from the flags (never from a constant offset), add a
capability class in `BleCapabilities.cs` implementing the relevant interfaces, and register it
from `BleDeviceSession.BuildAsync`. Give it a `Channels` constant and, if it should outrank
something, a case in `SourcePriorityPolicy`. Add a fixture to `BleParserTests` and an encoder
to `GattEncoders` so the simulator can produce it.

**A new ANT+ profile.** Add an `AntChannelConfig` to `AntProfiles` with the device type and
channel period from its profile document, add page constants and a parser to `AntPages.cs`, and
write a capability that subscribes to `IAntChannel.Broadcasts`.

**A new transport.** Implement `IDeviceTransport` and `IDeviceConnection`. Nothing else changes.

**A misbehaving device.** Register a `DeviceQuirks.Quirk` rather than special-casing a parser:

```csharp
engine.Quirks.Register(new DeviceQuirks.Quirk(
    Name: "acme-trailing-byte",
    Applies: (name, manufacturer) => name.StartsWith("ACME"),
    RewritePayload: p => p.Length == 15 ? p[..14] : p));
```

---

## Diagnostics

- `IPacketLog` — ring-buffered raw hex, toggleable globally or per endpoint, mirrored to
  `ILogger` at Trace. Off by default. Real devices deviate from spec constantly, and the octets
  are the only way to find out how.
- Structured logging through `Microsoft.Extensions.Logging` — pass an `ILoggerFactory` to
  `RideLinkEngine`.
- A malformed packet is logged with its hex and skipped; it never takes the session down.

## Connection lifecycle

Scan (filterable by advertised service UUID, name substring, RSSI) → connect → discover →
subscribe. Multiple simultaneous connections are the normal case — trainer plus strap plus a
separate cadence sensor is what the router exists for.

Auto-reconnect uses exponential backoff from 1 s to 30 s, tears down the previous session's
subscriptions before retrying so capabilities are never double-registered, and reports progress
through `ConnectionEvents`. Disposal cancels cleanly and releases native GATT handles — leaking
them keeps Windows holding the link open and the next connect fails.

---

## Tests

```bash
dotnet test
```

108 tests, no hardware required. The parser tests are byte-level fixtures written in spec field
order, covering:

- every flag combination that shifts a field, including FTMS's inverted **More Data** bit and
  the Cross Trainer's **24-bit** flags field
- uint16 and uint32 counter rollover on both revolution and event-time counters, and both
  rolling over together
- the Cycling Power / CSC event-time resolution split (1/2048 s vs 1/1024 s — using the wrong
  one gives exactly half the speed)
- "not available" sentinels mapping to null rather than to 65535 kcal
- FE-C's grade offset encoding (0% grade is transmitted as 20000, not 0)
- router handover, staleness fallback, recovery, and tie-break stability

End-to-end tests drive the whole stack over the simulated transport: the virtual devices encode
real GATT payloads and the engine decodes them with the same parsers it uses on hardware, so
the pipeline under test is the real one.

---

## Known gaps

- **ANT+ needs the adapter described above** before it touches hardware. Everything either side
  of `IAntChannelHost` is implemented and tested.
- **Only the Windows BLE transport exists.** Linux, macOS, Android and iOS each need an
  `IDeviceTransport`; nothing above it changes.
- **`.fit` replay is not implemented** — CSV is. `.fit` needs a decoder for the Garmin binary
  format, which is a self-contained piece of work.
- **The WinRT transport has not been run against a real trainer.** It has been run against live
  BLE traffic — adapter query, advertisement scanning, connect, GATT service discovery and a
  Device Information name read all work against real neighbouring devices — but no FTMS
  hardware was present here, so the notification, CCCD and Control Point paths have only been
  exercised through the simulator. The first run against an actual trainer is yours.
