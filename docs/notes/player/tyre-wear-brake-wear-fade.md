# Tyre wear, brake wear and fade

- **Tyre wear, brake wear and fade** (Settings -> Feel, off by default; `--tyrewear on`,
  `--brakewear on`; #20). Tyres wear per axle with sliding work (side force x slip speed + wheelspin,
  ~25 MJ per axle) and lose up to 30% of peak grip when finished — a drift burns the rears, grip driving
  barely scrubs. Discs heat with braking power (capacity 9 J/K per kg of car), cool faster with airflow,
  fade past 450 C (braking down to 40% at worst), and pads wear with the energy put through them. The
  car HUD shows tyres % and disc temperature. `--driftcheck` checks both: 30 s of drift wears the rears
  6%, fronts 1.6%; 15 stops 150->50 km/h take an AE86's discs to ~570 C (braking at 65%), a minute of
  cruising cools them to ~110 C.
