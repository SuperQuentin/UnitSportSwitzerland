# Remote players are interpolated, not snapped

- `FootPlayer` replicates `NetPos`/`NetVel`/`NetYaw`/`NetTime` (owner clock, **replicated last** —
  its setter consumes the whole state) at `ReplicationInterval` 1/30 s, not `position`/`rotation`
  every frame. Integer state (`RideKindId`, `HeldItemId`, `PoseKind`, `HeadwearId`) is `OnChange`.
- `Net/RemoteInterpolator`: ring of 32 timestamped states; renders `Delay` (1.6 × send interval +
  20 ms, 60–400 ms) behind the newest, Hermite with the sent velocities; past the newest it
  extrapolates on velocity for ≤ 250 ms; a state that changes "now" becomes an error offset eased
  away in ~100 ms; > 25 m is a teleport and snaps.
- **The render clock must never jump**: the clock offset is the lowest (local − sender) seen,
  creeping up 0.2 ms per state, and the lag actually used moves ≤ 5 % of real time toward it. A
  faster creep made a sawtooth whose resets showed as 2× steps.
- `WorldVelocity` is the velocity for local and remote copies alike (a remote's `Velocity` is 0).
- Remote bodies take their mount's collision size (`FitRemoteBody`): someone else's car blocks
  like a car, not a 0.32 m capsule.
- Self-check (`--interestcheck`): 42 and 83 m/s, 30 Hz, 60 ms jitter, 5 % and 20 % loss → worst
  frame step 1.05 × v·dt, no freeze.
