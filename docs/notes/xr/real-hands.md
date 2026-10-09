# Real hands in the headset (#648, `XR/XrHand`)

- **What you see of your hands.** One `XrHand` per physical hand replaces the old box markers: a
  palm, the forearm's end and fifteen finger bones (tubes) in the figure material, coloured by
  `HumanMeshBuilder.HandsOf(palette)`: skin, gloves (fingerless ones leave the fingers bare), the
  sleeve or glove at the wrist, the build's hand scale. Rebuilt only when the player's
  `FootPlayer.WalkPalette` changes (compared by value); the joints are only turned per frame, no allocation.
  Headset-only layer: mirrors and the monitor's third person show the figure's own hands (#439).
- **Where.** On the controller's **grip** pose (`XrRig` `LeftGrip` / `RightGrip`, `Pose = "grip"`),
  the origin in the middle of the handle in the closed hand: open fingers along −Y, wrist +Y, thumb
  side −Z, right palm facing −X (left +X; the left hand mirrors positions and bend signs, the meshes are
  symmetric). The grip nodes stay on their physical side: left-handed play (#439) swaps the aim nodes,
  so `UpdateRealHands` drives each hand from the aim node now on its side. On the steering wheel the hand's
  origin goes to `XrHands`' rim point (its marker, an empty node on the aim controller).
- **Fingers, per joint** (`XrHand.Drive`):
  1. **OpenXR hand tracking** (`XRServer.GetTracker("/user/hand_tracker/<side>")` as `XRHandTracker`):
     each joint's bend is the next bone's −Z seen from the bone before, toward the palm (−Y, OpenXR joints
     have +Y out of the back of the hand); the thumb's up/down from its metacarpal's swing off the palm's
     forward (70° up, 35° down). Used whenever every joint's orientation is valid. Enabled in
     `project.godot`: `openxr/extensions/hand_tracking`, `..._unobstructed_data_source`,
     `..._controller_data_source` (Meta's fingers inferred from the Touch controllers, when the runtime has it).
  2. **Otherwise the controller's sensors**, as Meta's own hands: `grip` closes middle/ring/pinky,
     `trigger` the index (resting on it while `trigger_touch`, pointing when not), a thumb on
     `ax_touch` / `by_touch` / `primary_touch` / `thumbrest_touch` lies down, else stands up
     (a thumbs-up with the grip closed). Eased (22/s; tracking 40/s).
- **Checks.** `--xrsim --xrhands --xrheadshot <abs png> 8` (title screen): both hands before the eyes, left
  thumbs-up, right pointing. Model viewer `Figures / VR hand` (open, fist, point, thumbs up, each side,
  fingerless gloves). Finger tracking itself needs a headset: Quest hand tracking on, over Link.
