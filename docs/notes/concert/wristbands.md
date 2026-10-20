# LED wristbands (#986)

Every member of the audience wears an LED wristband, as at PixMob or Xylobands shows: IR emitters
in the rig light the bands they cover in their colour code. Nothing per person runs on the CPU.

## The fixtures (`Dmx.cs`, patched by `StagePlan.IrRig`/`IrRound`)

Patched **after** the rest of the rig, so earlier addresses (and saved patch CSVs) stay put; new
groups `IrHeads`, `IrFloods`, `Wristbands` are appended to `RigGroup` (cue numbers unchanged).

| type | kind | channels | beam |
|---|---|---|---|
| `IR emitter head 10ch` | `IrHead` | pan, pan fine, tilt, tilt fine, speed, dimmer, R, G, B, zoom | 6..40° |
| `IR flood 5ch` | `IrFlood` | dimmer, R, G, B, zoom (fixed, home = where it points) | 30..100° |
| `Wristband transmitter 6ch` | `Wristbands` | dimmer, R, G, B, effect, rate | every band |

- Dimmer is the emitter's power (0 = off), R G B the colour code the bands show.
- Transmitter effect: 0..63 solid, 64..127 twinkle (each band at random, rate = speed),
  128..191 beat flash (a random half of the bands flash on each beat), 192..255 solid
  (`Wristbands.EffectValue/EffectOf`).
- End, side and thrust stages: 4 heads hung on the front truss, 2 standing on each delay tower
  (8); floods: the floor (front truss middle), each side stand (truss ends), the far stand (4);
  transmitter at front of house. In the round: 2 heads and a flood per side of the grid (8 + 4).
- The heads use the rig's stepper motors (`ConcertRig.Motor`, a spot's speeds) and
  `ConcertRig.Direction(fixture, pan, tilt)`. Beams are invisible (IR): nothing is drawn but the bands.
- External control: they are fixtures like the rest, so Art-Net/sACN from a console drives them
  and `/concert patch` lists them. The MVR/GDTF export (`src/Concert/Mvr`) was not on `main` when
  this landed: map the three kinds there when it merges.

## The bands (`Wristbands.cs`, `ConcertCrowd.DriveBands`, `shaders/concert/crowd.gdshader`)

- The crowd figure has a fourth box, the band on the right wrist (vertex alpha 0.25): by the hip,
  up with the hands in a chorus (same people as the raised shirt corners). Lit, it swells (an LED
  reads bigger than its plastic) and ignores the rig's tint.
- `Wristbands.Step` (from `ConcertRig._Process`, only while the crowd shows) reads the DMX, runs
  the head motors, and packs up to 16 lit emitters (heads and floods together) as 4 `vec4` each,
  world frame: (position, cos half angle), (beam now), (beam ~0.25 s ago), (colour, power).
  `ir_count` limits the shader's loop. Uniforms are sent only when something changed; the array's
  `Variant` is disposed at once (no finalizer, `general/perf-no-per-frame-allocations`).
- **Hold**: a band keeps its colour ~0.5 s after the IR goes. Done on the CPU per emitter: off,
  it keeps its last cone and colour for `Hold`; moving, the shader tests the arc from the trailing
  direction to the current one (closest point on the segment), so a sweep leaves colour behind.
- Shader (vertex stage, band vertices only, so 36 of the figure's 144 vertices pay): per emitter a
  cone test of the wrist against the swept beam, soft at the edge; the strongest wins; the
  transmitter wins where it is brighter. Twinkle runs on `TIME`, beat flash on the `beat` uniform.

## The desk's looks (`LightDesk.Bands`)

- Walk-in, blackout between songs: dark. Calm verse: dark, except a ballad (genre `Ballad` or
  under 90 bpm): slow twinkle in the artist's colour (`_pal[0]`).
- Groove: the IR heads sweep a line across the floor and up the far stand and back (8 bars),
  each head a slot across, ordered by where it hangs (`_irLateral`).
- Chorus: every band on in the artist's colour for 2 bars, then the floods chase stand by stand
  on the beat, the colour turning every 2 bars. A held chorus (`/concert cue chorus`) opens again
  every 16 bars.
- Peak: beat flash on every band in `_pal[1]`, the heads jump to a random spot of the crowd each beat.
- Build (song analysis): twinkle, faster and brighter as it rises. Drop (4 s): every band solid.

## Checks

- `--concertcheck`: every venue patches 4+ heads, 4+ floods, at most 16 emitters, one transmitter;
  a groove's head points down into the crowd once the motors arrive; chorus opens all on then
  chases; the drop lights all; blackout is dark and the bands hold 0.3 s, dark at 0.6 s; a fast
  song's calm verse is dark.
- `/concert debug` lists `IrHeads`, `IrFloods`, `Wristbands` under "firing" while on.
