# Simulate a network's rate in wall time, not frames

- **Simulate a network's rate in wall time, not frames.** `--synccheck` first copied every third
  frame; at WSL's 33 fps that is 11 Hz, and a plane rolling at 2.2 rad/s legitimately drifted
  0.26 rad between updates — a failure that was the probe's, not the sync's.
