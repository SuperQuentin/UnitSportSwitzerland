# Commands

- Riding check: `<godot> --path . -- --ride bike|skis|car:N|r1|monster|moto:N,seconds[,out.png] [--at E,N] [--heading deg]` — mounts,
  holds the throttle via `RideControls`, and prints speed/altitude/clearance every 2 s with a
  non-zero exit if the rider went nowhere or ended under the terrain. Riding is the one part
  that cannot be judged from a screenshot; add `--ridemenu` (with `--shot`) to capture the picker.
- Trucks and buses: `--truckcheck [trace]` (headless, numbers), `--truckprobe N[,s[,shot]] [--trailer M] [--kmh V] [--minor] [--trace] --at E,N` (a real road), `--heavynet a|b [pw]` (two clients), `--passengernet a|b|c [pw]` (three clients, `passengers`), `--decknet a|b|solo [pw]` (walking in a bus, `walk-aboard`), `--ride truck:N --trailer M [--steer x]`, `--gearbox auto|seq|seqclutch|hsplit|h`; see `trucks-buses`.
- Cockpit check: `<godot> --headless --path . -- --cockpitcheck` — every car's, truck's and bus's driver fits
  their seat (`car-cabin`, `heavy-cabin`); `--view body|bare` starts in the cockpit, `--mirrors on|off`, `--vsync on|off`,
  `--mirrorperf` logs what the mirrors cost (`cockpit`).
- Spin check: `<godot> --headless --path . -- --spincheck` — every car, Game and Sim, a badly managed
  brake at 200 km/h must spin it and a managed one must not (`locked-rear-spins`).
- `--drivecheck ... --skill S --aggression A` sets every driver's temperament (default: each its own,
  seeded by grid index); `--trace` prints inputs, lateral offset, cap and slipstream every 0.1 s.
- Void rescue check: `<godot> --headless --path . -- --voidcheck [--at E,N]` — falls through the
  world four ways, each must end back on the ground (`void-rescue`).
