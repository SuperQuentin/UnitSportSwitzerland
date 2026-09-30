# A gait is solved from the no-slip constraint, and the arithmetic has two traps

- **A gait is solved from the no-slip constraint, and the arithmetic has two traps.** The planted
  foot must travel backwards at exactly the body's speed or the figure moonwalks, so the stance
  sweep is `v × stance time` — and (1) **a cycle is two steps**, so stance time is `duty × 2/cadence`;
  dropping that factor of two halves every stride. (2) The **ankle** does not travel that far,
  because contact rolls heel-to-toe along the foot (~0.22 m walking) while the ankle is nearly
  still. Without that term the sweep comes out at roughly twice what a 0.85 m leg can span and
  every stance frame clamps. Measured slip after both: 0% from a walk to 3.5 m/s, 8% at 4.6, and
  26% at a 6 m/s sprint, which is honestly out of the model's reach.
  Two more, both found by rendering a cycle as a strip: the hip is highest at midstance when
  **walking** and lowest when **running** (one sign for both makes one gait look wheeled), and
  arm swing is about a *third* of the leg's — matching the foot needs ±0.6 m from a 0.52 m arm,
  so the elbows straighten and the runner sleepwalks.
