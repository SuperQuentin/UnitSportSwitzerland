# Avatars (`src/Avatar/`)

Procedural human, bike and aircraft meshes and their rigs.

## Architecture

- **Avatars** (`src/Avatar/`): procedural low-poly figures and a road bike, built from two
  primitives only — a tapered tube and a box (`MeshScratch`) — so each is one surface and one
  draw call. `HumanMeshBuilder` poses a figure from a table of joint positions (Standing,
  Running, Cycling); `BikeMeshBuilder` uses real 700c geometry (0.99 m wheelbase, 0.27 m bottom
  bracket, saddle at 0.90 m) because a bike is a shape everyone knows. Walking and running are
  **one gait** in `HumanMeshBuilder.GaitRig`, solved from the constraint that a planted foot
  travels backwards at exactly the body's speed; the walk/run changeover is the duty factor
  crossing 0.5, which is what creates the flight phase. `Cyclist` combines them
  and splits the mesh three ways — frame, rider, and per-leg — so the cranks turn with cadence
  and the knees follow by a two-bone solve rather than keyframes. Preview with
  `<godot> --path . -- --avatars <seconds> <out.png> [--view deg] [--focus 0..4]`;
  `--crank <rad>` parks the cranks and `--stride <m/s>` lays one gait cycle out as a strip.
  Neither a crank's direction nor a foot's slip can be judged from a single frame.
  All limbs go through `Limb.Solve` (two-bone IK), never keyframes.
- **A riding position is derived from the bike, never eyeballed.** The three contact points are
  fixed — hips on the saddle, hands on the drops, feet on the pedals — so the shoulder is the
  one place a 0.52 m torso and a 0.58 m arm can both reach. Hand-placing those joints produced a
  rider lying horizontally in front of the bars; with the ends pinned, the middle is not a free
  choice.
- **Judge model proportions with a long lens.** The avatar preview's focus camera sits 9 m back
  at 13° FOV, near-orthographic. A close wide-angle view of a bicycle enlarges whichever end is
  nearer and makes correct geometry look wrong — that cost an iteration of "fixing" a rider that
  was already right.

## Gotchas

- **A stopped figure is not a slow walk.** `HumanMeshBuilder.Cadence` has a floor — it must, or a
  figure inching forward takes one step a minute — and that floor keeps the legs turning over
  when the body has stopped. Everything the gait displaces is scaled by a `moving` factor that
  reaches zero at 0.25 m/s, and `AdvancePhase` freezes below it. Second half of the same bug:
  `RacePlayback` passed the real frame delta to its runners **while paused**, so a paused replay
  ran on the spot.
- **A gait is solved from the no-slip constraint, and the arithmetic has two traps.** The planted
  foot must travel backwards at exactly the body's speed or the figure moonwalks, so the stance
  sweep is `v × stance time` — and (1) **a cycle is two steps**, so stance time is `duty × 2/cadence`;
  dropping that factor of two halves every stride. (2) The **ankle** does not travel that far,
  because contact rolls heel-to-toe along the foot (~0.22 m walking) while the ankle is nearly
  still. Without that term the sweep comes out at roughly twice what a 0.85 m leg can span and
  every stance frame clamps. Measured slip after both: 0% from a walk to 3.5 m/s, 8% at 4.6, and
  26% at a 6 m/s sprint, which is honestly out of the model's reach.
  Two more, both found by rendering a cycle as a strip: the hip is highest at midstance when
  **walking** and lowest when **running** (one sign for both makes one gait look wheeled), and
  arm swing is about a *third* of the leg's — matching the foot needs ±0.6 m from a 0.52 m arm,
  so the elbows straighten and the runner sleepwalks.
- **Avatar meshes are authored facing +Z; a Godot node faces −Z.** `MeshScratch.Build` applies the
  half turn on the way out, once, instead of at each of the four places a figure is parented to a
  node. Skipping it does not look like a modelling error — the body travels correctly and only the
  machine is turned around, which from a chase camera reads as *riding in reverse*, and on a GPX
  ghost as running backwards. It survives a preview turntable, where there is no direction of
  travel to contradict it. Related: facing +Z, the rider's right is **−X**, so a chainring at +X
  is on the wrong side of the bike.
- **A crank turning the wrong way is instantly obvious to anyone who rides.** The bike faces +Z,
  so driving forward turns the chainring with its top moving toward +Z — meaning a crank starting
  at the front goes *down* next. Taking the obvious `(sin, cos)` circle runs it backwards. The
  same sign appears in `BikeMeshBuilder.Cranks` and `Cyclist.UpdateLegs`; they can only disagree
  if one is edited alone. Check it with `--avatars … --crank <rad>`, which parks the cranks —
  rotation direction cannot be judged from one frame.
