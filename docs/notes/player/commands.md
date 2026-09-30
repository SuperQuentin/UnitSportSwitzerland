# Commands

- Riding check: `<godot> --path . -- --ride bike|skis|car:N|r1|monster|moto:N,seconds[,out.png] [--at E,N] [--heading deg]` — mounts,
  holds the throttle via `RideControls`, and prints speed/altitude/clearance every 2 s with a
  non-zero exit if the rider went nowhere or ended under the terrain. Riding is the one part
  that cannot be judged from a screenshot; add `--ridemenu` (with `--shot`) to capture the picker.
