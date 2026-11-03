# Live skier and snowboarder (#933)

- **What**: `SnowRig` (`src/Avatar/SnowRig.cs`) replaces the skier's one baked crouch and the
  boarder's fixed figure. `SnowPose` (pure, `SnowPoseTests`) computes the 20 joints from a
  `SnowPose.Look`: crouch (0 stance .. 1 schuss), the ride's lean, plough, stride (±(1 + phase),
  the sign the pushing leg), absorb (a landing), air.
- **Body**: the torso is a pelvis height and a pitch; legs and arms are two-bone solves
  (`SnowPose.TwoBone`), so bones keep their length (tested). Skier: knees forward, the plough's V
  (tips in 0.32 rad), a skate push (one ski out and back, tip out 0.42 rad, both poles pushing
  back), the schuss egg (hands before the chin, poles under the arms), arms out in the air.
  Boarder: toe edge knees over the toes, heel edge sitting back, the back foot out of its binding
  pushing on the toe side, arms out along the board.
- **Angulation**: the ride rolls the whole figure by `RideMotion.Lean` (`PoseRideVisual`); the
  body above the hips takes back 45 % (skis) / 50 % (board) of it, and the skier's inside hand
  plants its pole ahead.
- **The half turn trap**: `MeshScratch.Build` turns author space (+Z forward) half round,
  `(−x, y, −z)`, into the ride's frame. So in author space a + lean takes the head toward **+X**
  (the inside of the turn), the angulation turns `Basis(Back, +lean·share)`, and the first-person
  eye is flipped too (`SnowRig.Eye`). Got all three backwards once: the camera sat behind the
  head and the upper body leaned further in instead of out.
- **Cost**: the body is a skinned figure (#1202) posed by its bones only when the pose's `Key`
  (its values in steps) changes, the skis and poles nodes moved to their boots and grips; a held
  line costs nothing.
- **Driving it**: `SnowRide.Glide` records the tuck, air, plough and strides (a counter: physics
  steps and frames do not run one for one); `SnowRide.Animate` eases crouch, plough and absorb and
  runs a stride for `StrideTime` (0.42 s skis, 0.5 s board), legs taking turns. The pose travels
  as the ride pose (`SnowPose.Pack`/`Unpack` in `FootPlayer.Anim`), so every peer draws it.
- **First person**: `SnowRide.FirstPersonEye` is the rig's eye, so the view drops with a tuck.
- **Model viewer**: Figures / Snow poses (each pose held still, rolled by its lean).
