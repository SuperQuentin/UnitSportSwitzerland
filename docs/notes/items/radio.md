# Radio: a thrown world item that plays CDs everyone hears in time (#104)

- **The first world item.** `ItemId.Radio` with `ItemUse.Throw`: Use opens its panel in the hand,
  Aim + Use throws it (`ItemController.UseSlot`, origin at the eye, `forward*8 + up*3 + player
  velocity`) and it leaves the inventory. It lives on
  as a `RadioBody` (`RigidBody3D`) under `World/Radios`, spawned for everyone by `RadioManager`
  through `World/RadioSpawner` — the `VehicleManager` pattern: offline `AddChild`, online
  request/grant RPCs (`RequestThrow`, `RequestPickUp`, `RequestPlay`, `RequestStop`).
- **Who simulates what.** The thrower is the authority over the fall (`Sync` synchronizer: position,
  rotation, `Settled`); once at rest (`Sleeping`, 1 s still, or 8 s) it freezes and the sync drops
  to 0.5 Hz. Every other peer keeps the body frozen in kinematic mode and is moved by the sync.
  The dedicated server never simulates (no ground): a radio whose thrower left is re-spawned by
  `ForgetOwner` server-owned and `Settled`. The **server** owns what plays (`State` synchronizer,
  authority 1: `CdId`, `StartedAt`, `Playing`, on-change and with the spawn for late joiners).
- **E beside it** (`RadioManager.Reach` 2.5 m) opens `RadioUi`: CD list (shared, then "(mine)"), Play,
  Stop, Remove (own CDs), Pick up (request/grant, one winner), volume slider, a link box to burn a CD
  for everyone or "Just for me" (`docs/notes/audio/cd-beat.md`). `Nearest` in `TryInteract` runs
  before the vehicle lookup; the prompt bar shows "Radio".
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
  the world), times the player's own volume (`UserVolume`, panel slider, `user://radio.cfg`).
- **In the hand (#168).** The playing CD lives in the radio's `ItemStack.Data` as a `RadioPlay`
  (`cd;startedAt;length`): Play/Stop in the held panel rewrite it (`Inventory.SetData`), a throw
  carries it into the world (`RequestThrow` keeps it if `TrustedLength` vouches), a pick-up carries
  the world radio's back into the stack. `ItemController` copies the held stack's data to the
  replicated `FootPlayer.HeldRadio` (OnChange), and `RadioManager.UpdateHeld` hangs a `RadioSpeaker`
  ("HeldRadio", 1.1 m up) on every player holding a playing radio, the holder included. Held
  radios do not count for dancing (`NearestPlaying` is world radios only).
- **Dancing.** `NearestPlaying(pos, DanceRadius 20 m)` decides whether E toggles `FootPlayer.DanceId`
  (replicated int; prompt "Dance"/"Stop dancing"). The beat is never replicated: every peer calls
  `RadioBody.BeatAt(ClockSync.ServerNow)`, so figures on every screen step on the same beat, and a
  client still downloading the CD dances in silence. See `docs/notes/avatar/dance-moves.md`.
- **Check:** `tools/radiocheck.sh` (`CHUNKS=<dir>` in a worktree): server burns two fixture CDs of
  different lengths (`--cdfixture` twice), a headless thrower throws, plays A, dances, changes to B,
  picks up, plays A in the hand, burns and plays a personal CD (`--radiopersonal <wav>`); a windowed
  watcher must hear each in sync (0.1 s) from the right file (`LoadedLength`), and silence for the
  personal CD. Takes ~3.5 min. Read the RESULT lines.
