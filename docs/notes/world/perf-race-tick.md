# Race server tick: nothing allocated, no node lookups per frame (#221)

## Rule
- `RaceManager.ServerTick` runs every frame: no LINQ, no `ToList`, no `GetNodeOrNull` there. It
  walks a reused `_tick` copy of the races and the loops `GoDue`/`AllDone`.
- Lookups of player nodes (host left, entrant left) go on the 1 s review pass (`_hostReview`);
  `DropDeparted` also runs right before GO so a grid never lines up someone who left.

## Why
Per frame and per race it allocated a list, a LINQ chain with closures and one `GetNodeOrNull`
(a string build + native call) per entrant: at 32 entrants ~2k native lookups a second per race.

## Same logic, preserved
- GO and the results fire on the same frame as before (same conditions, now loops).
- An entrant that disconnects is dropped (Out, or removed before GO) within 1 s instead of within a
  frame; before GO the drop is immediate.

## Migrating old code / open branches
- New per-frame race conditions: a small loop helper next to `GoDue`/`AllDone`, not LINQ.
- #269 does not touch `ServerTick`; it edits `MigrateHost` and the RPCs only.

## How to check
`tools/test.sh quick` (race checks), a `/race` over loopback.
