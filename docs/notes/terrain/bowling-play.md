# Bowling for real (#1323, epic #1322)

Playable lanes in every bowling centre (`bowling-centres` builds them). Plan: `docs/plans/playable-bowling.md`.

- **One set of numbers**: `LaneGeometry` (a lane pair's piece frame: lanes at ±0.8875, foul line, head pin,
  pit, masking, hood, screens) and `BowlingSizes` (the fixed sizes, apart from the plan so the pure parts
  are unit tested). The pair's mesh, its rig, the ball's run and the deck all read them.
- **Lane frame** (`BowlingBall`, `PinDeck`): x across from the lane's centre, **+x the bowler's left** (they
  face +Z), z from the foul line, y up from the lane. Pins numbered from the head pin, then each row left to
  right as the bowler sees it: the 7 far left (+x), the 10 far right.
- **The ball's run is pure** (`BowlingBall.Roll(run, release)`): a sphere sliding on a plane, friction 0.025 on
  the oil (12 m, faded over 1.2 m, scaled on mini lanes) and 0.18 on the dry back end; the side rotation
  (axis tilt) turns into hook through friction until it rolls. Gutter past |x| > 0.53. A `Release` goes on
  the wire as five integers, so every peer computes the same run. Entry angles 3-8° (real 4-6°).
- **The deck is the server's** (`PinDeck`): Jolt, in a `PhysicsServer3D` space of its own per roll, built
  from `BowlingSizes` (the dedicated server builds no interiors): deck, gutters, kickbacks, the pit's floor
  under everything, a **cushion** (bodies over the pit lose speed and never come back: a hard back wall threw
  the ball back onto the deck). Pins: a convex hull of the turned outline, 1.55 kg; ball 6.35 kg; CCD on.
  Real time (Godot steps every active space with the physics tick): no faster-than-real-time stepping.
  Standing = upright (Y·up > 0.94) on the deck. `--bowlingrollcheck --systems physics` (pocket balls strike
  4/5, edges take the corner pin, a spare, a gutter ball).
- **State** (`BowlingLanes`, `World/Bowling`, server + every client): per (centre key, pair index, lane) the
  bowlers in turn, their rolls, standing pins, the cycle (kind, start on `ClockSync.SimNow`), the roll under
  way, the rest poses. Whole centre packed on every change (`Pack`, a few hundred bytes) to the peers inside
  (`SpaceOf`); a client asks once on walking in, and forgets the centre on leaving (so the shoes come off).
  Deck frames: `Frames` RPC, 3 ticks a batch, 10 bytes a body.
- **Showing a roll**: the run locally from the release; the deck's frames replayed `Lag` behind the shared
  clock (0.15 s + RTT online); the roller's own ball leaves at once and eases into the lag along the lane.
- **Actions** (`Serve*`, a bot calls them with its id): `ServeSignIn` (at a shoe counter: a lane with a game
  not begun, else an empty one; again signs out), `ServeTakeBall` (at a rack: colour and weight;
  from the hood: your own), `ServeRelease` (your turn, ball in hand, on the approach, machine idle; a release
  up to 0.5 s in the past is believed). Then: the score, `Reset` after a strike or a frame's end else `Pick`,
  the ball back on the hood 6.5 s later. A game over restarts 20 s after.
- **Score**: `BowlingScore` (pure, `BowlingTests`): frames, marks (X, /, -), the tenth's third ball,
  `FreshRack`/`Standing` for the machine.
- **Rig** (`LaneRig`, per pair in a built interior): pins one MultiMesh (20), each lane's table and sweep,
  hood balls, the rolling ball; `PinsetterCycle` is a pure timeline (pick 7.8 s, reset 5.9 s); the sweep
  rests behind the masking's face. Headless, the MultiMesh's pins read at its origin: the centres' z-fight
  baseline carries 200 places a pair for it. The sweep reaches down into both gutters to their floor (#1334). A pin it pushes
  over the deck's end tumbles 0.45 m into the pit and is gone only then (`LaneRig.Step`, `PitDrop`): it used to vanish
  at the edge, on players' lanes as on the decoration ones.
- **Decoration bowlers** (`BowlingDeco`, #1334): frames of two balls as a real lane plays them (a first ball that
  leaves pins: a pick and the second ball aimed at what stands, `BotRelease`; a strike or a second ball: a reset); the
  deck drawn only while it moves, then the pinsetter has the pins (the finished deck used to be redrawn every frame,
  and the next throw set a full rack at once). The figure (`DecoThrow`, pure): bends over the return and lifts the
  ball, carries it to its stance, four steps and an underarm pendulum (push-away, down, backswing, forward 0.3 s to a
  low release at the foul line, follow-through), the ball in its hand, then walks back; release 4.75 s into a turn of
  23-29 s. The ball comes back up into the hood with its thunk `ReturnTime` after the run. `[bowling] decoration
  bowler on pair ... at plan x,z` in the log says where to look; BowlinGland's pair 25 lane 0 from
  `19.0,l1.7,-15.2,-14,-68` (after the door shot below).
- **Screens** (`LaneRig.Screens`): the game as a `Label3D`, set on change; after each roll the deck's frames
  replayed at 0.35x in a `SubViewport` of its own world (deck, pins, ball, a low camera), rendered only while it plays.
- **Playing**: E / Y (or a VR hand reaching out) at the counter, a rack (the ball reached for: across the
  rack, the lower tier when looking down), the hood, the approach. `BowlingAim`: spot, line, then hold Jump
  to swing (power swells and ebbs), sideways for hook, let go; Sprint looks at the pins; Cancel steps back.
  Phone (#1353): `TouchMode.Bowl` (Bowl held to swing and dragged for hook, Back, Pins; drag turns the line).
  VR: grip your ball on the approach, swing, open the hand (`XrBowling`: the hand's last 0.1 s; wrist twist
  = hook; x1.3 swing gain); or the same steps on the stick.
- **Shoes**: `ItemId.BowlingShoes` (a garment never owned), on the feet while signed in (`OccasionHats`).
- **Sound** (`BowlingSounds`): roll and gutter loops pitched by speed, crash/light on the first pin moving,
  the machine as a cycle starts, the hood's thunk.
- **`/bowl`**: `signin`, `ball [colour] [lb]`, `roll x angle speed revs axis`, `score`, in the centre you are in
  (offline a free camera inside is trusted at the counters). BowlinGland (Gland): `--origin 2510000,1140000`,
  enter by door `2510_1140_26_2`: shot `429.1,i1.6,-978.3,-5,43.4,3,<png>` (door `_26` reads as `_26_1`, which
  has no doorway); pair 25 lane 1: `l` shots at plan x 21-28, z -17.09, yaw 90.
