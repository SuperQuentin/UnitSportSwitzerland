# Saturn's rings: a slab with a thickness, ice chunks on the fly (#1854)

Under `--frame earth` with Saturn's globe (`--bodies DIR`), the rings are two things:

- **The slab** (`shaders/ring_slab.gdshader`, `BodyGlobe.AddRings`): one quad over the whole screen; each pixel's view
  ray is cut analytically to the slab (|z| ≤ 200 m round the equator), to the outer edge's cylinder and to what the depth
  buffer holds, then walked in 10 steps, each step's length over the thickness times the ring map's alpha (its normal
  optical depth, τ = 2 where the alpha is 1) at that step's radius. So: the familiar ring face-on from afar, a flat
  surface from just above, a band of its true height edge-on (from past the outer edge), a haze inside it where only
  steep rays see Saturn through. Saturn's shadow darkens it, the part behind Saturn is hidden.
- **The chunks** (`Terrain/Bodies/RingField`, `World/Bodies/RingChunks`, `RingRocks`): ice and a few dusty rocks within
  1,500 m of the camera, rebuilt on a worker after 250 m, solid (a sphere each, 0.85 of the radius) within 500 m.

## Gotchas

- **Every peer makes the same chunks**: a chunk is a function of (seed, row, cell) through SplitMix64 (cells 100 m along
  the radius and the arc, each the whole slab thick; the count by the map's alpha at the row, radius by a power law from
  1 to 30 m). Nothing is sent; `RingFieldTests` hold it. The server makes none (no bot goes there yet).
- **The quad hangs under the globe's root**, re-placed each frame: under the plain `BodyGlobe` node the origin's shift to
  Saturn (10^12 m) carried it out of its custom AABB and nothing drew (a debug red showed nothing).
- **Depths come in two scales**: what the depth buffer holds nearer than `true_reach` (3 km) is true scale round the
  camera (chunks, a craft) and is brought to the shrunk system's units; farther is Saturn's shrunk globe, already in
  them. Everything is measured from the camera (`cam_height` from doubles): centre-relative floats were 8 m out.
- **The camera's planes** (`Styles.CameraGlobal.CloseContent`): above 1,000 km the near plane is altitude/10,000, 4 km
  at the rings, so the chunks were clipped. `RingChunks` says when the camera is among the rings and the planes stay
  the game's own.
- **Edge-on band**: the map's RGB where its alpha is ~0 (past the A ring) is a dark blue, and 400 km along the plane is
  opaque even there: the band past the outer edge reads dark, as at equinox.

## Check

`--frame earth --chunks fixture:straight --sun real --date 2027-07-15 --time 10 --occasion none --bodies
E:/UnitSportSwitzerland/planets --ringcheck` (~1 min, not in the checkmap: it needs Saturn's pyramid): chunks made round
the camera in the B ring (3,615 drawn, 365 solid), a ray at the nearest chunk the field names hits its collider, the
same chunk at the same place after a 400 m move. Windowed: `test_output/rings/{among_chunks,edge_on,over_2km,afar}.png`.
The rock shapes are in the model viewer (Space).
