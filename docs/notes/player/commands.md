# Commands

- Riding check: `<godot> --path . -- --ride bike|skis|car:N|r1|monster|moto:N,seconds[,out.png] [--at E,N] [--heading deg]` — mounts,
  holds the throttle via `RideControls`, and prints speed/altitude/clearance every 2 s with a
  non-zero exit if the rider went nowhere or ended under the terrain. Riding is the one part
  that cannot be judged from a screenshot; add `--ridemenu [tab card]` (with `--shot`) to capture the picker (see the ui `travel-menu` note).
- Trucks and buses: `--truckcheck [trace]` (headless, numbers), `--truckprobe N[,s[,shot]] [--trailer M] [--kmh V] [--minor] [--trace] --at E,N` (a real road), `--heavynet a|b [pw]` (two clients), `--passengernet a|b|c [pw]` (three clients, `passengers`), `--ride truck:N --trailer M [--steer x]`, `--gearbox auto|seq|seqclutch|hsplit|h`; see `trucks-buses`. `--exitcheck [pw]` / `--exitcheck watch`: getting out of a car, truck or bus, walled in or not (`vehicle-hull-collision`).
- Crash check (#214): `--ride car,19,out.png --wall 70 [--crashshots 0.4,1,2.5]` drives into a wall 70 m ahead and prints the throw, flight and rest (`crash-ragdoll`); over loopback `tools/crashnetcheck.sh` (`CHUNKS=` from a worktree).
- Cockpit check: `<godot> --headless --path . -- --cockpitcheck` — every car's, truck's and bus's driver fits
  their seat (`car-cabin`, `heavy-cabin`); `--view body|bare` starts in the cockpit, `--mirrors on|off`, `--vsync on|off`,
  `--mirrorperf` logs what the mirrors cost (`cockpit`).
- Spin check: `<godot> --headless --path . -- --spincheck` — every car, Game and Sim, a badly managed
  brake at 200 km/h must spin it and a managed one must not (`locked-rear-spins`).
- `--drivecheck ... --skill S --aggression A` sets every driver's temperament (default: each its own,
  seeded by grid index); `--trace` prints inputs, lateral offset, cap and slipstream every 0.1 s.
- Void rescue check: `<godot> --headless --path . -- --voidcheck [--at E,N]` — falls through the
  world four ways, each must end back on the ground (`void-rescue`).
