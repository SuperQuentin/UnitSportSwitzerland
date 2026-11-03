# Concert crew and crew zones (#985)

## The crew (`ConcertCrew`)

- **Who**: at the front-of-house desk a lighting operator (1.8 m left of the desk's screen point),
  a sound engineer with headphones (0.9 m right) and a video director in a grey hoodie (2 m right,
  turned a little). The middle stays free: a player opens the desk screen there (`ConcertDeskUi`,
  `StagePlan.Desk`). An operator stands at every camera with `CameraRig.Crewed > 0`: the two
  dollies (pit, side track), the jib's base, the Steadicam, and the tripods on the platforms (FOH
  long lens, wide FOH, wide far stand). Wire cam, drone, crowd cam and artist POV are unmanned.
- **Where, pure**: `ConcertCrew.Posts(plan, rigs)`, read by the node and by `--concertcheck`.
  `Crewed` is how far below the lens the operator's feet are (`CameraRig.Tripod` 1.35, `Dolly`
  1.29, Steadicam 1.6); a jib's operator stands at its base beside the arm, on `FloorTop`.
- **Figure**: boxes through `StadiumBuilder.Geo` and `ChunkNode.ToPropMesh` in the stage's own
  prop material (so `--style` restyles it). A root (body and legs), a head on a neck pivot and two
  arms on shoulder pivots: four MeshInstances a person, ten people. Soles 12 mm up, parts overlapping
  or more than 8 mm apart: no zfight (`--zfightcheck Concert`: 0 cm²).
- **Animation**: `Step(live, dt)` from `ConcertRig._Process` only while a camera is near, writing
  the transforms in place (no allocation). The nod dips on every beat as hard as the section goes;
  each bar a hash picks who looks up from the desk at the stage (the video director turns to a side
  screen; the lighting operator looks up at a chorus's start); hands rest on the console (arms 0.8
  rad forward), the sound engineer rides faders, the video director cuts on the bar, the lighting
  operator's arms go up and slam down on a drop (`MusicMoment.SinceDrop < 0.5 s`) and tap the beat
  while the rig strobes or peaks. A camera operator stands 0.6 m behind the lens, turned with it,
  head tilted with its pitch, following the dolly or the Steadicam as it moves.
- **Scenery, not players**: no collision, no input, no network state (every peer animates from its
  own desk). Bots have nothing to do with them.
- Model viewer: `Concert / Crew - <role>` (`[Showcase("Concert", "Crew")]`).

## Crew zones (`StagePlan.Zones`)

- A `CrewZone` is an oriented rectangle of the stadium frame (u, v), a name, whether it is fenced.
  `StagePlan.Blocked(uv, margin)` tests the zones after the boxes, so the crowd's floor
  (`ConcertCrowd.Places`) never fills them.
- **The zones**: `front of house` (riser and its camera platform, 1.5 m round: x -6.5..9.6, z
  foh-1.5..foh+7.5 in the stage frame), each `delay tower` (3.2 x 3.5 m), the `pit` (deck front to
  the barrier, unfenced: the barrier is its fence), the far `camera tower` (4.8 m square), and each
  camera rig's `CameraRig.Footprint(margin 1)`: a track's rails and dolly (1.9 m to each side, 1.9 m
  past each end), the jib's whole sweep (its arm ±70°, the counterweight behind: about 13 x 19 m),
  a crewed tripod (1.9 m square). Footprints are computed from `CameraRig.For(plan)`, the same rigs
  the cameras use.
- **Camera platforms** (new kit, in `Boxes`): a 3 x 3 m deck 3 m up beside the FOH riser (stage x
  5.6..8.6) carries the FOH long lens and the wide FOH (`StagePlan.FohCamera`); a 2.4 m tower 10 m
  up (an arena's ceiling caps it) out on the floor carries the wide far stand camera
  (`StagePlan.FarCamera`; behind FOH on an end stage when the floor has room, across the bowl from
  FOH in the round; with no room the camera goes on the FOH platform). Before, those cameras floated
  (over the tent, 22 m over the crowd).
- **Rigs moved to the floor**: the side track's x is clamped to the floor (an arena's seats came
  within its zone) and its far end stops `3 + FrontCorner` m short of the far seating front; a round
  stage's pit dolly and jib start from the deck's edge, not its middle.
- **Barriers**: every fenced zone gets panels (black scrim, a rail, two feet; 1.1 m) along its edges,
  `StagePlan.Panel`, from `FenceFrom` on in `Boxes`: none inside another zone (overlapping zones
  merge their outline), none within 0.3 m of a stage box on the ground, none off the floor; a gate
  (one panel left out) in the edge furthest from the stage. Corners are left 0.3 m open, so panels
  never cross.
- **Check**: `--concertcheck` builds every venue in every layout it offers (two seeds each) and
  fails if anyone of the crowd stands in a zone, the desk has not its three, a crewed camera has no
  operator, or there is no barrier. It prints the desk and far camera of seeds 1..24 for shots.
- Not done: cable paths (no cable runs are drawn yet); a drone pilot and the wire cam's operator.
