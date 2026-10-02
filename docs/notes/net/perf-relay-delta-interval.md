# Relays check their on-change properties at 10 Hz (#221)

## Rule
- `FootPlayer.MakeRelay` builds `RelayNear`/`RelayFar` with `DeltaInterval = 0.1f`. Keep it: a relay
  is server-owned, and with the default 0 Godot compares every `OnChange` property of every relay
  every frame.
- A new replicated integer/flag (ride, seat, clothes, item, door bits) goes in the `OnChange` list in
  `FootPlayer._Ready`; it then reaches viewers through the relays within 0.1 s.
- Anything a viewer must see in the same packet as the position (it changes every frame, or it
  must line up with `NetTime`) stays `Always`, not `OnChange`.

## Why
14+ `OnChange` properties on two relays per player, read every frame on the server: ~27k property
reads a second at 16 players, ~110k at 32. Numbers: PR (server busy p50/p99, see `perf-interest-round`).

## Same logic, preserved
- What is sent and to whom is unchanged; an on-change value reaches viewers up to 0.1 s later
  (it was one server frame). The owner's own `Sync` (owner -> server) keeps the default.
- Spawn state still carries every property: a viewer that gets a player spawned sees its current
  ride and clothes at once.
- Trap: a property that must arrive with the 30 Hz state (it is read in `OnNetState`) must not be
  `OnChange`: it would now lag by up to 0.1 s.

## Migrating old code / open branches
- `grep -n "MakeRelay" src/Player/FootPlayer.cs`: keep `DeltaInterval = 0.1f` when resolving a
  conflict in the initializer. #269 (floating origin) edits `FootPlayer._Ready` near it; take both.

## How to check
`tools/test.sh net`; on loopback (`tools/useanimcheck.sh`, `--heavynet a|b`), a ride or clothes
change shows on the remote peer.
