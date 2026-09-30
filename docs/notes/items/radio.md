# Radio: a thrown world item that plays CDs everyone hears in time (#104)

- **The first world item.** `ItemId.Radio` with `ItemUse.Throw`: Use throws it (`ItemController.UseSlot`,
  origin at the eye, `forward*8 + up*3 + player velocity`) and it leaves the inventory. It lives on
  as a `RadioBody` (`RigidBody3D`) under `World/Radios`, spawned for everyone by `RadioManager`
  through `World/RadioSpawner` — the `VehicleManager` pattern: offline `AddChild`, online
  request/grant RPCs (`RequestThrow`, `RequestPickUp`, `RequestPlay`, `RequestStop`).
- **Who simulates what.** The thrower is the authority over the fall (`Sync` synchronizer: position,
  rotation, `Settled`); once at rest (`Sleeping`, 1 s still, or 8 s) it freezes and the sync drops
  to 0.5 Hz. Every other peer keeps the body frozen in kinematic mode and is moved by the sync.
  The dedicated server never simulates (no ground): a radio whose thrower left is re-spawned by
  `ForgetOwner` server-owned and `Settled`. The **server** owns what plays (`State` synchronizer,
  authority 1: `CdId`, `StartedAt`, `Playing`, on-change and with the spawn for late joiners).
- **E beside it** (`RadioManager.Reach` 2.5 m) opens `RadioUi`: CD list from the library, Play, Stop,
  Pick up (request/grant, one winner), a link box to burn a CD. `Nearest` in `TryInteract` runs
  before the vehicle lookup; the prompt bar shows "Radio".
- **Playback is clock-driven, not streamed.** Each client fetches the Ogg once (`CdCache`, through
  `ChunkStreamer` as `AssetKind.Cd` — a CD is a file the client lacks, metered like a tile) and
  plays it on an `AudioStreamPlayer3D` (Sfx bus, unit size 8, max 120 m) from
  `ClockSync.ServerNow − StartedAt`, seeking back into line when the heard position drifts by more
  than 80 ms (`RadioBody.UpdateSpeaker`). The server ends a CD when `Duration` runs out.
- **Dancing.** `NearestPlaying(pos, DanceRadius 20 m)` decides whether E toggles `FootPlayer.DanceId`
  (replicated int; prompt "Dance"/"Stop dancing"). The beat is never replicated: every peer calls
  `RadioBody.BeatAt(ClockSync.ServerNow)`, so figures on every screen step on the same beat, and a
  client still downloading the CD dances in silence. See `docs/notes/avatar/dance-moves.md`.
- **Check:** `tools/radiocheck.sh` (`CHUNKS=<dir>` in a worktree): server burns a fixture CD from a
  generated WAV (`--cdfixture`), a headless thrower throws/plays/dances, a windowed watcher must
  hear the CD within 0.1 s of the clock for 10 s and see the thrower's `DanceId`. Read the RESULT lines.
