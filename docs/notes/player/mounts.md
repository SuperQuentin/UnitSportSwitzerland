# Mounts

- **Mounts** (`src/Player/Rideable.cs`): **R** opens a picker (`RideUi`) — On foot / Road bike /
  Skis. (E interacts; on a pad Y opens the picker when there is nothing to interact with.) Online,
  the vehicle rows are greyed for a non-admin (`Core/Permissions`, the core `permissions` note).
  Blurbs name their controls with `{action}` placeholders, filled by `InputHints.Format`. A vehicle is a table of numbers plus a mesh: everything touching the body, the network,
  the camera and the UI lives once in `FootPlayer`, so adding one is a class plus a line in
  `Rideable.Create`. Both share one model — mass, a resistive force, `SlopeAccel` — and differ
  only in where propulsion comes from. Mounted, speed is a **scalar along a heading**, not a
  velocity vector: a bike goes where it points, and strafing is something people do, not
  vehicles. The camera goes third-person with a raycast pull-in, and the machine's lean is
  *derived* (`tan φ = v·ω/g`), never authored.
  - `Bicycle` runs the real power equation, `m·a = P/v − ½ρ·CdA·v² − Crr·m·g − m·g·sinθ`.
    Nothing is tuned: 180 W gives 32.7 km/h flat, 9.3 km/h up 8%, and 63.8 km/h freewheeling
    down it. Steering is lean-limited, so the turn radius grows with speed. `RiderWatts` is the
    input **because a home trainer measures watts** — RideLink drops straight into it.
  - `Skis` have no engine. Turning *costs* speed (`EdgeScrub`), which is the whole of skiing:
    pointed straight down a 30% face you reach 80 km/h, and carving across the fall line is the
    only brake. W is a capped poling shuffle, because skis on the flat would otherwise strand you.
  - `FootPlayer.RideControls` replaces the keyboard when set — one movement path for a keyboard
    rider and a pedalling one, and the seam `RideProbe` and the trainer both use.
  - `RideKindId` is replicated, so remote players are seen on the bike rather than sprinting
    at 40 km/h in a running pose.
  - **Space hops** on either mount (`FootPlayer.RideJumpVelocity`, 3.2 m/s, edge-triggered, ground
    only): a bunny hop or a pop off a lip that carries the momentum it already had. The free-fly
    camera uses the same keys vertically — **Space** up, **Shift** down (Q/E still work), boost
    moved to **Ctrl**.
