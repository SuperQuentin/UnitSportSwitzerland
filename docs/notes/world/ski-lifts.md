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
- **Part 2 (#915)**:
  - **Line properties**: each line carries its own `Speed`, `Spacing`, `Hang`, `Side` (return-side
    offset), `Seats` and `Name`: the kind's defaults, then OSM.
  - **OSM** (`ropeways.json`, written by `--pistes` with the slopes, ODbL; `AssetKind.Ropeways`,
    synced once like the landings; the service rebuilds its blocks when it arrives). Each TLM line is
    matched to the OSM way whose two ends lie within 60 m of its own:
    - `cable_car` becomes `CableCar`;
    - speed = length / `aerialway:duration` (clamped 0.8-12 m/s);
    - a chair with `aerialway:detachable=no` runs at 2.3 m/s;
    - `aerialway:occupancy` gives the seats (a chair as wide as them);
    - the name goes in the prompts (`RopewayService.Called`).
    - Western Valais: 552 aerialways, 216 with a duration.
  - **Cable cars**: `RopewayMotion.Carrier` gives two cabins on their own tracks (the second one
    `Side` m to the left), mirrored: one goes up while the other comes down, with a `Dwell` of 25 s at
    each station. `Next` and `UntilDeparture` count to a cabin leaving the bottom.
  - **Twins**: TLM draws some gondolas as their two cables. `MergeTwins` keeps one line when both
    ends of two same-kind lines lie within 40 m, the gap becoming its `Side`, unless they match two
    different OSM lifts (Médran 2 and Médran 4 run side by side above Verbier).
  - **Riders on every peer**: `FootPlayer.LiftLine` (the line's `Id`, its key folded to an int, never
    0) and `LiftCarrier` are replicated (and in `Movie/ActorIo`). Every peer places the rider with
    `LiftPose`: on its way up, on the carrier; otherwise on the station spot it waits at or just
    reached. A remote copy is moved there after interpolation (`PlaceRemoteOnLift`).
  - **Seated**: on a chair `UpdateLiftSeat` draws a `SeatedFigure`, skis on its feet when on skis, at
    its seat on the bench (a stable hash of the player's name, never `string.GetHashCode`, which
    differs per process), and hides the walking figure. In a cabin the figure is hidden.
  - **Stations**: `RopewayMeshBuilder.Station`, an open shed at each end (posts outside both tracks,
    a roof, the bull-wheel housing), placed again on an origin shift. No collision. Where
    swissBUILDINGS3D has the real station (Médran) the shed sits inside it.
- **Checks**: `--ride skis,150 --at 2583512,1105282 --lift` (a 314 m T-bar near Verbier): boarded at
  once, up 51 m in 104 s, set down on skis, RESULT ok. On the chairlift
  (`--at 2584724,1105182`, 1390 m, about 5 min) the ride and camera are shown by `--midshot`.
  - `CHUNKS=... tools/liftnetcheck.sh` (full tier: real terrain): server plus A and B at that
    chairlift. A boards (`--liftnet A`). B checks that A's copy stands where its chair puts it by
    the clock (within 0.5 m) and is drawn sitting.
