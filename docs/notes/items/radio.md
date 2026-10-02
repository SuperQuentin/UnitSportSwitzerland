# Radio: a thrown world item that plays CDs everyone hears in time (#104)

- **The first world item.** `ItemId.Radio` with `ItemUse.Throw`: Use opens its panel in the hand,
  Aim + hold Use winds up a throw (`ThrowAim`, the `throw-drop` note; Q drops it at your feet) and it
  leaves the inventory. It lives on
  as a `RadioBody` (`RigidBody3D`) under `World/Radios`, spawned for everyone by `RadioManager`
  through `World/RadioSpawner` — the `VehicleManager` pattern: offline `AddChild`, online
  request/grant RPCs (`RequestThrow`, `RequestPickUp`, `RequestPlay`, `RequestStop`).
- **Who simulates what.** The thrower is the authority over the fall (`Sync` synchronizer: position,
  rotation, `Settled`); once at rest (`Sleeping`, 1 s still, or 8 s) it freezes and the sync drops
  to 0.5 Hz. Every other peer keeps the body frozen in kinematic mode and is moved by the sync.
  The dedicated server never simulates (no ground): a radio whose thrower left is re-spawned by
  `ForgetOwner` server-owned and `Settled`. The **server** owns what plays (`State` synchronizer,
  authority 1: `CdId`, `StartedAt`, `Playing`, on-change and with the spawn for late joiners).
- **Pointed at, Use (click) takes it straight into the hand (#261)**: `ItemController.TakeRadio` (the
  mesh flies to the hand, the server's pick-up as before, the stack keeps the CD/start/mode, the
  slot is selected or swapped in from the pack). **E** on the pointed radio opens `RadioUi`; E with
  none pointed opens the nearest within `RadioManager.Reach` 2.5 m, but only after the vehicle at
  hand (`door-reach`). Use opens the panel on the radio in the hand, R on a car stereo (below).
- **Carried, it keeps playing (#261)**: `Inventory.RadioSlot()` (the hand's radio, else the first
  with a CD, else the first) writes `FootPlayer.HeldRadio`; one not in the hand sets the replicated
  `FootPlayer.BackItemId` (OnChange) and is drawn on the back (`FootPlayer.Back.cs`, on the posed
  chest frame, child of the body mesh, with straps), bouncing to its beat at 0.55 size. The
  changer (`RadioUi.Changer`) follows that slot too. Only one radio sounds at a time.
- **It bounces (#261)**: `RadioBody.Bounce(phase, beat, half, amount)` squashes on the beat about the
  bottom face, springs up 3 cm and rocks to alternate sides; the world radio while its speaker
  really plays, the one in the hand (figure 0.7, viewmodel 0.45) and on the back from
  `RadioBody.BeatOf(cd, startedAt, clock)`.
- **The panel is a music picker (#211)**, menu look (`UiTheme`/`UiKit`, glass 0.95, at most
  700 x 660 px, re-fitted on resize): now playing (title, bpm/style, "CD n of m", elapsed / length
  bar), previous / Play-Stop / next (round the list; on a station, the next station), the mode
  button, a search box (words AND-matched on title and style, Enter plays the first hit, `/` or
  Ctrl+F focuses it), rows as focusable buttons under "Shared CDs" / "My CDs" (bin on your own)
  and, in a car, "Live stations". Playing row amber with a play mark. Arrows / D-pad move, Enter / A
  play, Esc / B close. Pick up (world radio), volume, burn box with "Just for me"; the burn line maps
  the burner's stages to a 3-step bar (Downloading / Analysing / Encoding), green when burnt, amber
  on a refusal (`docs/notes/audio/cd-beat.md`).
- **Modes (`RadioMode`, `RadioQueue`)**: Play once / Repeat this CD / Play the list (shared by id,
  then mine by title) / Shuffle. Applied by **whoever owns the play state**: the server for a world
  radio (`RadioBody.Mode`, a 5th property on the `State` synchronizer; `RadioManager.Queue`:
  `SetMode` -> `RequestMode`, and `Ended` puts the next CD on when the 1 Hz housekeeping sees one
  run out, so up to 1 s of gap; after a personal CD "the list" goes on with shared ones), and the
  holder / driver for hand and car radios: `RadioUi.Changer` runs every frame, open or not, and
  writes the next `RadioPlay` (the mode is its optional 4th field, `cd;at;len;mode`) starting where
  the last one ended on the clock. A thrown radio starts on "once" (`RadioState` carries no mode);
  a pick-up keeps it.
- **Playback is clock-driven, not streamed.** `RadioSpeaker` (an `AudioStreamPlayer3D`, world or hand)
  fetches the Ogg once (`CdCache`, through `ChunkStreamer` as `AssetKind.Cd` — a CD is a file the
  client lacks, metered like a tile) and plays it from `ClockSync.ServerNow − StartedAt`, seeking
  back into line when the heard position drifts by more than 80 ms. The server ends a CD when the
  replicated `Length` runs out (set with the CD: it may not know a personal CD's duration).
- **Changing CD bug (#168).** The speaker kept one Ogg path for its whole life (`_oggPath`, set on the
  first load, never cleared) and a sticky `_fetchFailed`: a new CD loaded the *old* file on the new
  CD's clock, everywhere, so "Play" looked like it did nothing. Everything loaded or fetching is now
  keyed by CD id (`RadioSpeaker.TryLoad`); `LoadedCd`/`LoadedLength` let the probe check the file.
- **Loudness.** `RadioSpeaker`: −8 dB base, unit size 3, max 45 m (was 0 dB, 8, 120 m — it drowned
  the world), on the Music bus (the panel slider is Settings' Music volume) and muffled by walls
  and doorways (`docs/notes/audio/hearing.md`).
- **In the hand (#168).** The playing CD lives in the radio's `ItemStack.Data` as a `RadioPlay`
  (`cd;startedAt;length`): Play/Stop in the held panel rewrite it (`Inventory.SetData`), a throw
  carries it into the world (`RequestThrow` keeps it if `TrustedLength` vouches), a pick-up carries
  the world radio's back into the stack. `ItemController` copies the held stack's data to the
  replicated `FootPlayer.HeldRadio` (OnChange), and `RadioManager.UpdateHeld` hangs a `RadioSpeaker`
  ("HeldRadio", 1.1 m up) on every player holding a playing radio, the holder included. Held
  radios count for dancing too since #261 (`NearestMusic`).
- **Dancing.** `NearestMusic(pos, DanceRadius 20 m)` (a world radio or a carried one) decides whether E toggles `FootPlayer.DanceId`
  (replicated int; prompt "Dance"/"Stop dancing"). The beat is never replicated: every peer calls
  `RadioBody.BeatOf(cd, startedAt, ClockSync.ServerNow)`, so figures on every screen step on the same beat, and a
  client still downloading the CD dances in silence. See `docs/notes/avatar/dance-moves.md`.
- **Car stereo (#211):** `PlayerInput.RadioPanel` (R, keyboard only like U / P; shared with the travel picker: in a vehicle with a stereo R opens the radio and `ClientWorld` skips the picker, on foot R is the picker; F1 row, prompt
  "Radio") opens the panel on `FootPlayer.StereoOwner`: yourself at the wheel of a car/truck/bus, or
  the driver when riding along (read only: rows dimmed, "Only the driver changes the music"). A CD
  is the driver's replicated `FootPlayer.CarCd` (a `RadioPlay`, OnChange, `FootPlayer.CarCd.cs`),
  never together with `CarRadio`: picking either clears the other (U / P clear the CD too).
  `ApplyRide` clears it, `CaptureVehicle` keeps it in `VehicleState.Cd` (`"cd"`, validated by
  `RadioPlay.Decode`), so a parked car (`VehicleBody.Cd`, spawn data) plays on and getting back in
  restores it. `WebRadio.Scan` hangs a `RadioSpeaker` "CarCd" (1 m up) on every source with a CD:
  no relay, the boombox's clock-driven playback; not headless. A CD played "once" is taken out
  when it ends; a parked car plays its CD to the end, nobody advances it. A car coasts while the
  panel is open (it holds `UiFocus`, as chat does).
- **No error flood when the link drops (#211):** `ClientWorld.GetLocalNetPlayer` (behind every
  `LocalPlayer`, so `RadioManager.Players`, `WebRadio.Players`, `RadioUi`) and the per-frame
  `IsServer` checks go through `Net/NetLink`; see `docs/notes/net/netlink-dead-peer.md`.
- **Check:** `tools/carcdcheck.sh` (`CHUNKS=<dir>`, port 7811): a headless driver works the panel (a
  station row, mode to "the list", CD A's row), A runs out and its changer puts on B, then it parks;
  a windowed watcher must hear A and B from the car and B from the parked car within 0.1 s.
  `<godot> --path . -- --carcdcheck shots` (offline, windowed) writes `test_output/radio_car.png`,
  `radio_held.png` and `radio_world.png`. Both write the shared user dir (fixture CDs in `cds/`, a
  radio in `inventory.json`): back those up.
- **Check:** `tools/radiocheck.sh` (`CHUNKS=<dir>` in a worktree): server burns two fixture CDs of
  different lengths (`--cdfixture` twice), a headless thrower throws, plays A, dances, changes to B,
  picks up, plays A in the hand, burns and plays a personal CD (`--radiopersonal <wav>`); a windowed
  watcher must hear each in sync (0.1 s) from the right file (`LoadedLength`), and silence for the
  personal CD. Takes ~3.5 min. Read the RESULT lines.
