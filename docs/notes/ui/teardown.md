# Leaving a world (teardown)

- **In place, no restart** (`GameShell.LeaveWorld` -> `Teardown`): wait two frames so the
  "Leaving" screen is drawn (#807: it ran in the click's frame, so the last world frame froze on
  screen), then close the peer, set an
  `OfflineMultiplayerPeer`, stop a hosted server, `RemoveChild` the world (so its `_ExitTree` runs
  now and the name `World` is free at once: a sibling named `World2` would break every RPC path),
  `QueueFree` it, then `WorldStatics.Reset()`. If any of it throws, `HardRestart` starts the game
  again (`OS.SetRestartOnExit`); `--leave-restart` forces that path.
- **Never hold the main thread for worker threads on a leave** (#807): the engine lives on, so a
  worker may finish late as long as it touches no node. `ChunkManager.ReleaseWorkers()` (called
  before `RemoveChild`) cancels every build and skips the `_ExitTree` wait, which stays for quit
  only (an ArrayMesh made while the engine shuts down is an access violation). Results still
  queued are released in `_ExitTree`; a next world has its own manager, so none can reach it.
  `ChunkStreamer._ExitTree` answers every pending request (its deferred RPC never runs on a freed
  node), and pool threads that block on tiles (`DormantVehicles.Fill`) pass a token cancelled in
  their `_ExitTree` and check `IsInstanceValid(this)` after the await. Before: up to 3 s frozen
  (`_ExitTree` busy-waited on builds whose ENet fetch needed the main thread).
- **What leaks if you are not careful**:
  - lambdas on **static C# events** (`GameSettings.Changed`, `VehicleManager.Refused`...): subscribe
    a named method and unsubscribe it in `_ExitTree`. `WorldStatics.Reset` also nulls the
    world-owned static events (`ResetEvents()` on each class), never the shell's
    (`GameSettings.Changed`, `PlayerInput.DeviceChanged`, `Permissions.Changed`).
  - lambdas on **`Multiplayer` signals** that capture nothing: Godot drops a connection by itself
    only when its target is a freed `GodotObject`. Use an instance method
    (`AfricaTwinEgg.DropLocalEgg`) or disconnect in `_ExitTree`; a static lambda is connected again by
    the next world ("already connected").
  - **singletons**: every `Instance` must be nulled in its `_ExitTree` (`BirdLife` was not).
  - **deferred calls** that touch `Multiplayer` after the node left the tree, where `Multiplayer` is
    null (`ChunkStreamer.FetchAsync` checks `IsInsideTree()`).
  - static caches keyed by the old world's tiles (`DoorIndex`, `Audio.Surfaces`) and delegates that
    captured its nodes (`GarageUi.GarageNear`, `OccasionTowns.UseGenerated`).
- **Check**: `<godot> --headless --path . -- --leavecheck [connect <host:port>] [host <port>] --chunks <dir>`
  enters and leaves twice offline (twice more online, once hosted from the menu) and after each
  leave asserts: no `Main/World`, every world singleton null, the node count back to the title's
  (exactly 255 nodes so far), and a hosted server process gone. It also fails a leave whose longest
  frame (wall clock, so also under `--fixed-fps`) is over 1 s, and logs `title after N ms, longest
  frame M ms`; `[shell] teardown N ms` and `[terrain] exit: N builds in flight, waited M ms` say
  where the time went. `RESULT: ok`.
