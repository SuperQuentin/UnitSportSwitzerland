# Car radio: live web radio, relayed by the server so everyone hears the same sample (#179)

- **What.** Cars, trucks and buses have a radio: **U** next station, **P** previous, through "off"
  (keyboard only, no pad button is free in a car). 14 live MP3 streams in `Audio/Live/Stations`
  (SRF 1/3/Virus, La 1ère, Couleur 3, Rete Uno, RTR, Radio Swiss Pop/Jazz/Classic, Energy Bern,
  One FM, FIP, Radio Paradise; URLs checked Oct 2026). Ids are replicated: append only.
- **Who owns the station.** The driver's replicated `FootPlayer.CarRadio` (OnChange, owner
  authority, like `HeldRadio`); `PlayingCarRadio` is 0 unless the body drives a car/truck/bus and is
  not a passenger. `ApplyRide` clears it (any change of ride); `CaptureVehicle` puts it in
  `VehicleState.Radio` (`"radio"` key), so a **parked** car keeps playing (`VehicleBody.Radio`, spawn
  data only — nobody tunes a parked car) and getting back in restores it. A wreck is silent.
- **Why a relay, not each client on the stream.** Every Icecast connect starts with its own burst,
  so clients fetching the URL themselves were seconds apart. The **server** (`StationTap`) runs
  `ffmpeg -i <url> -ac 1 -ar 16000 -f s16le -` per station (worker thread, restarted every 10 s if it
  dies) and hands samples out **on the shared clock**: sample n is `T0 + n/16000` on
  `ClockSync.ServerNow` (T0 = first audio). Short of audio → silence; more than 1 s ahead (the
  connect burst) → dropped; a stall > 2 s → jump. So upstream timing never reaches a client.
- **The wire.** 0.2 s chunks (3200 samples, aligned on multiples of 3200) as 8-bit G.711 µ-law
  (`MuLaw`, 128 kbit/s per station per listener), reliable RPC on ENet channel 3 (`WebRadio.Chunk`).
  Clients need no ffmpeg and never see a URL.
- **Who gets what.** Each client (`WebRadio.Scan`, 4 Hz) hangs a `WebRadioSpeaker` (`AudioStreamPlayer3D`,
  node `WebRadio`, 1 m up) on every source — players and parked `VehicleBody`s — and tells the server
  (`Want`) the stations of the ones within 50 m of the camera, nearest first, at most 3, plus for
  4 s the ones whose radio just went (`KeepFor`): getting out hands the station from the driver to
  the parked car a round trip later, and dropping the buffer in between cost 2 s of silence. The server
  opens a tap for any wanted station and closes it 20 s after nobody wants it. Offline the game is
  its own server: same code, `Receive` called directly.
- **Playback.** `StationBuffer` keeps ~13 s by index (missing = silence). The speaker feeds an
  `AudioStreamGenerator` (16 kHz, 0.25 s) so the sample heard at local time t is
  `(ServerNow(t) − T0 − Delay) · Rate`, counting the frames still queued and the output latency;
  `Delay` = 2 s covers the round trip and resends. More than 50 ms off → jump back on the clock
  (`Resyncs`). Volume: the boombox's `RadioSpeaker.UserVolume`, −6 dB base, max 45 m.
- **Server needs** ffmpeg (bundled `bin/` or PATH, as for CDs) and outbound HTTP. No ffmpeg → a
  warning per retry and silence.
- **Check:** `tools/webradiocheck.sh` (`CHUNKS=<dir>` in a worktree; internet): dedicated server,
  headless driver (admin, takes a car, SRF 3, then Swiss Pop, then parks) and a **windowed** watcher
  that must hear each for 8 s within 50 ms of the clock; both clients print the sum of the samples
  due at every 5 s mark and the script requires them identical (17/17 on the final run, offsets within ±50 ms).
  Offline: `<godot> --headless --path . -- --ride car:0,60 --webradiocheck offline`. A headless
  client's dummy audio driver consumes irregularly, so its speaker resyncs constantly; judge
  sync on a windowed client.
