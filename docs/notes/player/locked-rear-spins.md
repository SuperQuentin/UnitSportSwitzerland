# A mismanaged brake spins the car, in Game too

- **A mismanaged brake spins the car, in Game too** (`Car.Step` `_help`, `--spincheck`; #52). Game's
  arcade help — the counter-steer assist, the yaw damping and the catch past 35° — held every car
  short of a spin however badly it was braked: full brake with a sweeper's steer at 200 km/h peaked at
  ~63° and came back. Now the help fades (in ~0.15 s) while the FOOT brake takes the whole rear circle
  (the 35% rear share over 85-100% of the rear's grip) AND the rear slides past its peak (|αR| 5-9°),
  and comes back as fast off the pedal. Both conditions: a straight full stop saturates the rear too,
  and with the help intact it stays straight (with the help gone it swapped ends at 170-180° — any seed
  grows); the handbrake is not the foot brake, so drift entries keep all of it.
- `<godot> --headless --path . -- --spincheck`: every car, Game then Sim, flat ground, from
  min(200 km/h, 90% of its top), steer = what a bend at half the grip needs (δ = L·a/v² through the
  speed-scaled rack, ~0.02-0.03 of the stick): full brake must spin (|slip| > 90°) wherever the brakes
  can saturate the rear; 40% brake, no brake and a straight full stop must not (Game). Sim is the bare
  car with a fixed wheel for 4 s: it spins at 40% brake and even lifting (50°) — no driver corrects
  it — so only the full brake and the straight stop are judged there.
- Measured (Game): 23 of 26 spin, at 115-140 km/h (from 174-200 km/h), peak yaw up to 3.4 rad/s; the NSX, SW20 and ZZW30
  (mid-engined, 65/35 brakes against a heavy rear: brakes 10.3-11.1 < rear limit 11.2-11.9 m/s²) lock
  the fronts and plough on, 2-53° — accepted as the physics' answer. Sim: the same 23 spin at
  113-153 km/h (they would not in Game before: peak 45-66°). `--driftcheck` still passes (every car drifts and recovers).
- The scripted pilot never floors the pedal (85% of the rear limit, eases off past 3° of slip), so its
  normal driving is untouched; a pilot's **mistake** (driver-skill note) does floor it.
