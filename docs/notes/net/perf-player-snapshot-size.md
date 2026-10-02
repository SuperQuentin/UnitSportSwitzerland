# Player snapshot: the pose goes as `NetPose`, train angles only on a train (#221)

## Rule
- `BodyPose` (Transform3D) and `TrainPose` (Vector4) are plain properties, NOT replicated. On the
  wire they are one `float[] NetPose`: quaternion (4), offset (3), landing squash (1), then the
  train's three joint angles only while `TrainPose` is not all zero. 40 bytes off a train
  (was 72: Transform3D 52 + Vector4 20), 52 on one.
- Owner code keeps writing `BodyPose`/`TrainPose`; remote code keeps reading them (the `NetPose`
  setter decodes them). Never add `[Export]` back to either, and never put a new `Always`
  Transform3D in `FootPlayer`'s replication config: send what it needs (a quaternion, an angle).
- The only scale a body pose may carry is the squash `(1 + s/2, 1 - s, 1 + s/2)` after the
  rotation (on foot). Any other scale in `_visual.Transform` on a ride would be lost on the wire:
  add a slot for it in `NetPose` instead.
- Any change to `NetPose`'s layout is a wire change: bump `Handshake.Protocol` once #269 has it.

## Why
~32 B less in every 30 Hz state packet to every viewer. `tools/loadtest.sh` (real terrain), server
net out in steady state (all players in), before -> after: 16 players 169.0 -> 141.0 KB/s (-17 %),
32 players 577.4 -> 461.5 KB/s (-20 %); net in -20 % / -12 % (the owners' sends shrink too).

## Same logic, preserved
- `--synccheck`: fresh-frame pose error 0.0000 before and after (owner vs mirror, every stage
  including the jump's squash, the bike's lean, the plane's roll).
- The getter fills one of two cached arrays (no allocation on the owner); the receiver's
  marshaller allocates one small `float[]` per packet (gen0 only).
- A malformed packet (fewer than 8 floats) changes nothing.
- A straight train (all angles exactly 0) sends 8 floats and decodes to `TrainPose` zero: the same.

## Migrating old code / open branches
- `grep -rn "\.:BodyPose\|\.:TrainPose\|\[Export\] public Transform3D BodyPose\|\[Export\] public Vector4 TrainPose" src`
  on an old branch: those lines become `NetPose` (`PoseProperties` is `{ ".:NetPose", ".:PoseKind", ".:Anim" }`).
- A branch that reads `p.Get("BodyPose")` through Godot (probes): read the C# property instead.
- #269 (LV95 on the wire) changes `NetPos` to `NetE/NetN/NetAlt` in the same config: both
  changes are independent lines; keep both and bump its `Handshake.Protocol` to 3 if it lands
  after this, or this PR bumps it if it lands first.

## How to check
`--synccheck --world flat` (fresh pose 0.0000), `tools/useanimcheck.sh` (remote screenshots),
`--heavynet a|b` (remote train bends), `tools/loadtest.sh 16` net out.
