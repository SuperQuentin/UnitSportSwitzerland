# Remote players are interpolated, not snapped

- `FootPlayer` replicates `NetE`/`NetN`/`NetAlt` (LV95 doubles, #185: `NetGlobal`)/`NetVel`/`NetYaw`/`NetTime` (owner clock, **replicated last** —
  its setter consumes the whole state) at `ReplicationInterval` 1/30 s, not `position`/`rotation`
  every frame. Integer state (`RideKindId`, `HeldItemId`, `PoseKind`, `HeadwearId`) is `OnChange`.
- `Net/RemoteInterpolator`: ring of 32 timestamped states; renders `Delay` (1.6 × send interval +
  20 ms, 60–400 ms) behind the newest, Hermite with the sent velocities; past the newest it
  extrapolates on velocity for ≤ 250 ms; a state that changes "now" becomes an error offset eased
  away in ~100 ms; > 25 m is a teleport and snaps. The states are `GlobalPos` and the Hermite works
  on offsets between neighbouring states, mapped to world space only on output: the receiver's
  origin shifts while they sit in the ring, and that must never read as a 3 km teleport (#185).
- **The render clock must never jump**: the clock offset is the lowest (local − sender) seen,
  creeping up 0.2 ms per state, and the lag actually used moves ≤ 5 % of real time toward it. A
  faster creep made a sawtooth whose resets showed as 2× steps.
- `WorldVelocity` is the velocity for local and remote copies alike (a remote's `Velocity` is 0).
- Remote bodies take their mount's collision size (`FitRemoteBody`): someone else's car blocks
  like a car, not a 0.32 m capsule.
- Self-check (`--interestcheck`, `--origincheck`): 42 and 83 m/s, 30 Hz, 60 ms jitter, 5 % and 20 %
  loss, at LV95 E 3,600 km (a float there steps 25 cm) → worst frame step 1.05 × v·dt, no freeze.
  `--netsmooth[,seconds[,label[,minspeed]]]` measures a real remote over loopback; it records LV95, so
  it runs under `--originstress`. `minspeed` (m/s) waits up to 90 s for a remote that fast: two
  `--raceauto` racers with `--netsmooth,15,x,15` measure each other at speed while both shift.
