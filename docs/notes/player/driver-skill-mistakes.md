# Driver skill, aggression and mistakes

- **Driver skill, aggression and mistakes** (`AutoPilot.Temperament(skill, aggression, seed)`; #52).
  `Skill` 0.8..1: the car profile's corner share and braking share are ×0.94..1 of the defaults, and
  the chance of a mistake. `Aggression` 0..1: +0.04 on the braking share (brakes later; 0.89 of the
  rear-lockup limit at most, still under it), follow gap 10 → 5 m, dives up the inside only above 0.3.
  Defaults: skill 1, aggression 0.3 (the `--raceauto` player's own pilot never blunders).
  `--drivecheck` gives each car its own, seeded by its grid index (`--skill S`, `--aggression A` set
  them all); a race NPC's come from its entrant id (`RaceNpc`), the same every race, printed as
  `[npc] ... drives, skill .. aggression ..`.
- **Mistakes** (`AutoPilot.Blunder`, cars only): entering a braking zone (4 m/s over the profile to
  enter, under 0.5 to leave — one threshold flickered and counted a "zone" every few frames) UNDER
  PRESSURE (a rival within a second ahead or behind), with chance `0.35·(1−skill)·(0.5+aggression)`
  and at most one per 20 s: 0.1-0.3 s late on the brakes (gas at 0.6), then the pedal floored for
  1.2 s with no easing as the rear steps out. The physics decides the rest — a spin (locked-rear
  note), a lock-up that runs wide, or nothing. At 3× that chance and no cooldown, a pack blundered
  every other corner and 3 of 6 went into the trees.
- Every spin (`Spins`, past 90° going forward), mistake and reset is logged by the pilot (`Log`):
  `--drivecheck` prints them, NPCs print `[npc] <name>: SPIN at ... m, ... km/h`, a `--raceauto`
  pilot `[race] #id ...`.
- Recovery: a car facing back up the road after a spin (nose > 120° off the line, < 5 m/s) for 3 s,
  or standing still for 10 s with the race on (a jam), is put back on the line at the first point
  ahead with nobody within 8 m; stuck off the road it backs out as before.
