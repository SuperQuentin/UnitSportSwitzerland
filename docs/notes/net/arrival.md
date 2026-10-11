# Arrival: the server decides where players start (#799)

- **Online, nobody picks their own spawn.** Joining (LAN list, saved server, address, hosting) does
  not open the landing map (`GameShell.LaunchVia` skips it for `GameMode.Multiplayer`). `Net/Arrival`
  at `World/Arrival` (server and every client) sends `Rules(e, n, freeTeleport)` to each peer in
  `ServerWorld.OnPeerAccepted`, right after its player is spawned. Server flags: `--spawn E,N`
  (LV95, default Riddes), `--no-teleport`.
- **Client**: `ClientWorld` sets `_awaitingServerSpawn` at boot for a networked launch without `--at`;
  it is part of `SpawnPending`, so the loading screen stays on "Placing you" and the networked body
  waits (`EnterFootWhenGrounded`). `OnServerSpawn` shifts the origin there and replaces the spectator's
  `SpawnPoint` (or teleports, if already on foot); 45 s without it fails the join. A client `--at`
  ignores the server's spawn, so every probe keeps its spot. A returning sleeper gets `Rules` first,
  then `Sleepers.WakeAt` moves it on.
- **Teleport rule**: `Permissions.ServerTeleport` / `CanTeleport` (`!Online || IsAdmin || ServerTeleport`).
  Off: `PlaceSearchUi.Go` and `XrMap` refuse, `ChatManager.CommandCity` refuses non-admins, and
  `ClientWorld.ToggleMode` returns from the fly camera with `EnterFootMode(inPlace)` +
  `RequestReplacement`. Server-ordered moves (`ForceTeleport`: `/tpall`, `/bring`; `WakeAt`) still go
  through `Teleporter`, which is never gated itself. Transforms are client-authoritative: this is a
  rule honest clients follow, not enforcement.
- **Wire**: a new node, so no existing RPC list renumbers; `Handshake.Protocol` 33.
- **Check**: `tools/arrivalcheck.sh` (net): fixture server with `--spawn` 150 m east of Riddes and
  `--no-teleport`; the client (no `--at`, `--arrival check --expect-spawn`) must start there, see
  `CanTeleport` false, be refused `/city`, and come back from a 300 m fly-camera trip within 5 m.
