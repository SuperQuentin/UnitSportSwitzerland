# Race NPC: no physics step on the dedicated server (#221)

## Rule

- `RaceNpc._Ready` calls `SetPhysicsProcess(false)` when `NetworkManager.DedicatedServer`: the
  server's copy of an NPC is a data proxy and never drives it.
- If the server ever simulates an NPC itself (not today), re-enable it where its authority becomes
  the server (`FootPlayer.SetSimulator`, which today returns early on a proxy).

## Why

Investigation #13 in #221: one managed `_PhysicsProcess` call per NPC per physics tick on the
server that always returned at `!IsMultiplayerAuthority()`. Small (a 32-NPC race: ~1900 native to
managed calls a second); a static argument, no measurable frame change.

## Same logic, preserved

- The server is never an NPC's authority: `RaceNpcs.Best` hands NPCs to clients only, a retired
  NPC is freed, and `SetSimulator` returns before any change on a proxy. So the skipped step was
  always the early return, with `_active` already false.
- Clients unchanged: a non-simulating client still runs the step (it resets `_active` on handoff).

## Migrating old code / open branches

- Open PR #269 edits `RaceNpc.cs` just above `_Ready` (origin shift): the added line sits after
  `_me.RideControls = Hold;`, no overlap. Keep both.

## How to check

`tools/racecheck.sh` (NPCs race and are handed off), or a server with `--npccheck` clients.
