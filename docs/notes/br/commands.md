# Battle Royale commands and checks

- Chat:
  - Anyone: `/br join`, `/br leave`, `/br status` (bare `/br` = status).
  - Admin: `/br open [town|here] [5|6|7] [short|normal|long] [solo|duos|trios|squads]`, `/br start`, `/br zone`, `/br cancel`.
  - `/br zone` (#425): the current wait ends now and the zone starts closing (from the loot time: phase 1's
    shrink). `BrState.Started` moves back by what was left (`ZoneSchedule.NextShrinkAt`), so every client
    follows from the state message. Refused before the plane's doors close, while shrinking, once over.
  - `/pvp on|off` is separate (free roam).
- Server: `--brpace f` (timing multiplier, for tests). Client: `--br` joins every lobby by itself (#231).
- `<godot> --headless --path . -- [--chunks <dir>] --brcheck`: zone determinism/nesting, round
  lengths (30-45 min target), 200 real region picks against the hard rules, no repeat within 8 km,
  state JSON round trip, inventory lend round trip, and a real region's map rendered to `test_output/br_map_check.png`.
- `GODOT=<exe> [CHUNKS=<dir>] tools/brcheck.sh`: `--brcheck`, then one whole match over loopback on a
  generated world at pace 0.13 (two players get the smallest zone, at half the timetable: the old 0.08 clock, #447) (`BrProbe`, `PORT=` to move it off 7799): admin opens and starts, B joins by itself (`--br`), both board the cargo plane
  (A jumps when the doors open and steers by looking, B is pushed out when they close; each sees the other hidden aboard), both in the region with a
  knife-only pack, A (admin) gets no fly camera, debug menu, catalogue or `/spawn` and forces the zone (`/br zone`), B is hurt outside the zone, A knifes B, B stays down and spectates, A wins, both go
  back to where they started with their own packs. Also: minimap built, waypoint set, M opens and closes the match map; crates and vehicles appear, A empties a supply crate and B's death box, a supply drop is announced and drawn, everything is gone after. `REAL=1 SERVER_WAIT=40 CHUNKS=<dir>` plays it on real terrain (a random Swiss region); add `SITES=1 BRPACE=0.2` for the outdoor sites (crack a bunker, break a crate, fire a flare). Screenshots: `test_output/br_*.png`.
