# Battle Royale commands and checks

- Chat:
  - Anyone: `/br join`, `/br leave`, `/br status` (bare `/br` = status).
  - Admin: `/br open [town|here] [5|6|7] [short|normal|long]`, `/br start`, `/br cancel`.
  - `/pvp on|off` is separate (free roam).
- Server: `--brpace f` (timing multiplier, for tests).
- `<godot> --headless --path . -- [--chunks <dir>] --brcheck`: zone determinism/nesting, round
  lengths (30-45 min target), 200 real region picks against the hard rules, no repeat within 8 km,
  state JSON round trip, inventory lend round trip, and a real region's map rendered to `test_output/br_map_check.png`.
- `GODOT=<exe> [CHUNKS=<dir>] tools/brcheck.sh`: `--brcheck`, then one whole match over loopback on a
  generated world at pace 0.05 (`BrProbe`): admin opens and starts, both drop in the region with a
  knife-only pack, B is hurt outside the zone, A knifes B, B stays down and spectates, A wins, both go
  back to where they started with their own packs. Also: minimap built, waypoint set, M opens and closes the match map; crates and vehicles appear, A empties a supply crate and B's death box, a supply drop is announced and drawn, everything is gone after. `REAL=1 SERVER_WAIT=40 CHUNKS=<dir>` plays it on real terrain (a random Swiss region). Screenshots: `test_output/br_*.png`.
