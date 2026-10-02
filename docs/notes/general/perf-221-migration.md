# Performance pass #221: what changed, and how to bring an old branch up to date

Read this before you rebase or merge `main` into a branch started before October 2026, or before you write
code in a hot path (per frame, per tick, per peer). Each line points to the note with the rule, the
reason (numbers), the traps and the exact migration steps; read only the ones your branch touches.

## Rules that apply to every branch

- **Tests and RAM**: run checks with `tools/test.sh unit|quick|net|full`, cheapest tier first; every Godot,
  server, client or bot run goes through `tools/lib/guard.sh` (`guard_run` / `guard_watch`: killed below
  1500 MB free, exit 137 = rerun later; one heavy run at a time with `guard_lock`). → `testing`
- **Boot only what a check tests**: new probes declare `--systems` / `--world flat|fixture|real`; driving
  checks use fixture courses (`--chunks fixture:<course>`), never a required real map. → `test-systems-optin`
- **Nothing allocated per frame** (LINQ, `new` collections, string building, `"literal"` → `StringName`,
  new ray queries, shader params / label text written without a change). → `perf-no-per-frame-allocations`
- **Saves**: every persisted JSON file through `JsonStore.Save` (atomic); saves a player triggers while
  playing through `JsonStore.SaveAsync` (background writer, flushed on quit). → `json-store`, `perf-saves-background`
- **Visibility**: player synchronizers update visibility on change only; any change to what a peer may see
  calls `UpdateVisibility`. → `perf-visibility-on-change`

## By area (note name → what to do on an old branch)

| Area | Notes |
|---|---|
| Server / net | `perf-server-frame-metrics` (busy is per frame now), `perf-no-main-thread-periodic-jobs` (no IO / prints per player on the main thread; `--player-status` is opt-in), `perf-mcp-logger-gated`, `perf-visibility-on-change`, `udp-receive-loop` (`Udp.ReceiveLoop`), `region-file-sync`, `is-online` (`NetLink.Online`) |
| Terrain | `perf-collision-commits` (collision is queued in 4×4 cell pieces; a new collision layer goes through the queue), `perf-lod-trees` (ring strides, trees by ring, shared tree meshes, free replaced meshes), `perf-door-portals`, `building-triangles` (`b.Tri(t)`, one `RoofNormalY`) |
| Avatars | `perf-pose-mesh-cache` (rebuild a figure only on a new pose key, in place; no `ArrayMesh` per frame), `perf-shared-materials`, `cockpit-kit` (cabin wheel/dials/lamps/pedals/mirrors through `CockpitKit` + `CockpitSpec`) |
| Vehicles / world / audio | `perf-surface-grid` (`Surfaces.At(..., caller)`), `perf-traffic-tick` (lazy obstacle sampling, lane neighbours, shared traffic meshes, 600 m car draw), `perf-racenpc-server-physics` (no NPC physics step on the server), `perf-parked-vehicles` (nothing per frame while asleep, reused ray queries), `perf-engine-synth-idle`, `perf-player-snapshot` (per-tick player scans read `PlayerSnapshot.Of`: traffic, combat, slipstream, overlap, race pilots), `perf-camera-rays` (per-frame rays through `Core.RayQuery`, cached exclude arrays: `SelfExclude`, `TrainRids()`) |
| Probes / UI | `chat-probe` (two-client probes derive from `ChatProbe`, register in `ClientWorld`'s check tables), `ui-theme-panels` (`UiTheme.Title/Prompt/Flat/Amber`, no hand-built styles) |
| Tools / formats | `perf-tile-header` (`TileHeader`, byte-identical goldens in `tests/`), `perf-road-segment-helpers` (`RoadSegment.Lv95`, `RoadProfiles.For`), `dead-code-and-shared-helpers`, `mathx` (`MathX.Flat/Damp/WrapAngle`, `Mathf.SmoothStep`) |

## Rebasing an old branch: the order that conflicts least

1. `git merge origin/main` (never rebase a pushed branch). Delete untracked `*.uid` files first if the
   merge refuses: main now tracks many that an import generated locally.
2. Resolve conflicts with the notes above; the usual ones: `ClientWorld.cs` probe dispatch (`chat-probe`),
   `FootPlayer` synchronizers (`perf-visibility-on-change`), `Traffic.cs` (`perf-traffic-tick`),
   `ChunkManager`/`ChunkNode` (`perf-collision-commits`), per-frame code in `PlayerFeel`/`DayNight`/
   `PlayerInput` (`perf-no-per-frame-allocations`), `tools/*check.sh` (keep the `guard_watch $$` line).
3. Grep your own new code for the patterns each note lists.
4. `tools/test.sh quick`, then `tools/test.sh net` if you touched replication.

## Known open problems found during #221

#259 (`--drivecheck --at` after an origin shift), #279 (`--synccheck` crank flaky under load),
#284 (`lootsynccheck` on main), #286 (racers thrown through the windscreen on side contact).
