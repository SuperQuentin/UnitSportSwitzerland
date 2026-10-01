# Leaving a world (teardown)

- **In place, no restart** (`GameShell.LeaveWorld` -> `Teardown`): close the peer, set an
  `OfflineMultiplayerPeer`, stop a hosted server, `RemoveChild` the world (so its `_ExitTree` runs
  now and the name `World` is free at once: a sibling named `World2` would break every RPC path),
  `QueueFree` it, then `WorldStatics.Reset()`. If any of it throws, `HardRestart` starts the game
  again (`OS.SetRestartOnExit`); `--leave-restart` forces that path.
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
  (exactly 255 nodes so far), and a hosted server process gone. `RESULT: ok`.
