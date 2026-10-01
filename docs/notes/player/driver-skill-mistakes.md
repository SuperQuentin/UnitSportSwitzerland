# Driver skill, aggression and mistakes

- **Driver skill, aggression and mistakes** (`AutoPilot.Temperament(skill, aggression, seed)`; #52, #85).
  `Skill` 0.8..1.1: the car profile's corner share and braking share are ×0.94..1 of the defaults at
  0.8..1, and the trend carries on above 1 (an ace at 1.1: ×1.03 corner, ×1.04 braking share); the
  braking share is capped at 0.9 of the rear-lockup limit whatever the skill and temper (still under
  it). `Aggression` 0..1: +0.04 on the braking share (brakes later), follow gap 10 → 5 m, dives up the
  inside only above 0.3. Defaults: skill 1, aggression 0.3 (the `--raceauto` player's own pilot never
  blunders).
- **Grids** (`AutoPilot.GridSkills`): one skill per equal band of 0.8..1.1, shuffled — levels really
  differ, never a grid of 0.8s — and the best raised to an ace (≥ 1.05) if its band did not make one.
  Race NPCs: the server draws them per race (`RaceManager.DrawSkills`, logged `[race] #N NPC skills:
  ...`) and sends each in `Setup`, so a handoff keeps the driver. NPCs race to reach the finish, not
  to fight: temper 0..0.3 (no inside dives, long follow gap), from their id. `--drivecheck` draws the
  same way with a fixed seed (reproducible); `--skill S`, `--aggression A` set them all.
- **Mistakes** (`AutoPilot.Blunder`, cars only): entering a braking zone (4 m/s over the profile to
  enter, under 0.5 to leave — one threshold flickered and counted a "zone" every few frames) UNDER
  PRESSURE (a rival within a second ahead or behind), with chance `max(0, 0.35·(1−skill)·(0.5+aggression))` (none from skill 1 up)
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
