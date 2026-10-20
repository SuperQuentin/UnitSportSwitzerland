# Ski lifts: riding the real ropeway lines (#912)

- **Lines** (`World/Ropeways`):
  - `RopewayService.Load` takes the road tiles' aerial segments (`RoadFormat.IsAerial`) in blocks
    of 4×4 tiles, with a 2-tile margin. `RopewayJoin.Join` chains the per-tile pieces whose ends
    meet within 0.5 m into whole lines, bottom station first (by altitude). Lines under 30 m (seam
    scraps) are dropped.
  - Kind: Cableway → Gondola (TLM does not tell a gondola from an aerial tramway), Chairlift →
    Chair, SkiLift → TBar, RopeTow → RopeTow.
  - `Key` is the bottom station's LV95 rounded to a metre, the same on every peer.
  - The service runs on clients and on the server (`Systems.Lifts`, "lifts").
  - Some gondolas are drawn by TLM as two parallel cables, so they show as two lines (follow-up).
- **Carriers** (`RopewayMotion`, pure, `RopewayTests`): each loops up the line and back down it,
  2.5 m to the side, evenly spaced, at s = (sim·speed + i·2L/n) mod 2L, on `ClockSync.SimNow`.
  Every peer draws the same carrier in the same place; nothing is sent.

  | Kind | Speed (m/s) | Spacing (m) | Hang below the cable (m) |
  |---|---|---|---|
  | Gondola | 5 | 45 | 3.2 |
  | Chair | 4.5 | 22 | 2.3 |
  | T-bar | 3 | 14 | the rod is stretched to the snow |
  | Rope tow | 2 | 8 | — |
- **Drawn** (`RopewayLayer`): lines within 3 km, one MultiMesh per part per line, posed every
  frame within 1.2 km and every tenth beyond. Models: `RopewayMeshBuilder`, model viewer
  "World / Ropeways".
  - **Gotcha 1, the floating origin**: it moves every node under the world by each shift, and the
    carriers are written in world space from LV95, so the layer is pinned at the origin each frame.
  - **Gotcha 2, culling**: a MultiMesh's culling box is stale once its instances move, so each line
    gets a `CustomAabb` round itself, redone on a shift. Without both the carriers were drawn
    kilometres away, or culled.
- **Riding** (`FootPlayer.Lift.cs`), on foot or on skis, within 8 m of a bottom station:
  - interact (E, the pad's interact, a VR grip) boards the next carrier (`RopewayMotion.Next`);
  - the body waits on the station spot, facing up the line, then `Carrier` follows the carrier on
    the clock: on the chair, on the cabin's floor (hidden inside), or pulled along the snow on a
    T-bar or rope tow;
  - 2 m before the top it is set down 4 m past the station: `ReleaseRiding` on skis (2 m/s, the
    `RideMotion` made afresh) or `Release` on foot;
  - a drag lift is let go of with interact again; there is no getting off a chair half way.
  - **Camera**: `Carried()` returned before anything else ran, so the chase camera stood still.
    On a lift it is updated, and on a chair or in a cabin it sits higher and further back
    (`LiftCamera`): at a skier's chase height the cable was right in front of it.
- **Network**: nothing new.
  - The rider's position replicates as any player's (30 Hz, interpolated), so a remote rider can be
    a little off the chair.
  - A replicated lift key, to place a remote rider exactly on the chair, is a follow-up.
- **Bots**: `TryLift` on the server body (its own simulator) is the same call. No brain uses lifts
  yet (`bot-capability-map`).
- **Checks**: `--ride skis,150 --at 2583512,1105282 --lift` (a 314 m T-bar near Verbier): boarded at
  once, up 51 m in 104 s, set down on skis, RESULT ok. On the chairlift
  (`--at 2584724,1105182`, 1390 m, about 5 min) the ride and camera are shown by `--midshot`.
