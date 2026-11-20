# The fly camera's frame, blended by distance (#2053)

Design and curves: `docs/plans/planets.md` section 8, "The spectator's frame, blended by distance".

- **Three weights** (`Player/SpectatorFrame`, pure, `SpectatorFrameTests`), each 1 near a body and 0 away from it:
  - `SpinWeight`, co-rotation: gone 0.002 to 0.02 R over the air's top or the ground;
  - `UpWeight`, up and level: gone 0.005 to 0.04 R over it;
  - `RestShare`, the rest frame: a body's own within 0.6 of its sphere of influence, none past 1.4, nested from the Sun down (`RestStep`).
- **`SpectatorCamera.Drift`**, each frame before the keys:
  - the body's turn since the last frame (`Unspin`: its axes in the frame tree, the local day included) is undone for `1 − SpinWeight`, place and view;
  - the rest frame's displacement less the anchor's own is added.
  - It is off the earth frame (no `CrossBodies`) and on a jump of the clock.
  - The weights come from the camera's place and the bodies', never the anchor's, so a handover changes nothing.
- **Up fading** (`UpWeight` under 0.9999, else the old code bit for bit):
  - the keys blend the level directions with the camera's own;
  - the look turns about a blend of the vertical and the camera's own up, its pitch held off the vertical only as far as there is one;
  - `Level`'s turn and its roll easing are weighted;
  - `TurnToward` turns the shortest way.
- **No new control.** VR: no horizon lock in free space; the game never rolls the rig there.
- **Checks**:
  - `--spectatorframecheck [all|hover|strafe|flight]` (headless, `--fixed-fps 60`). The flight measures the frame's velocity from the anchor's centre, not the Sun's: a body's local day turns its orbit with the camera's distance, a motion nobody sees. It is averaged over 6 frames (the Earth's angle steps a little unevenly frame to frame), with an allowance of 1.4e-5 of the speed for the measurement's own noise, the same before.
  - `--spectatorcrosscheck` allows the frame's drift (`LastDrift`) in its pop test.
