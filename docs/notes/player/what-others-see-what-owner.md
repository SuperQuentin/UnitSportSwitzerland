# What others see is what the owner sees

- **What others see is what the owner sees** (`FootPlayer` pose sync, issue #9). A remote copy
  used to get position, yaw and ride kind only, so nobody else ever saw a lean, a trick, a bail, a
  craft's attitude (it was posed level), a turning rotor or crank, a slide, a jump or a stunned
  body, and every peer ran its own gait phase. Three more synced properties now carry it:
  **`BodyPose`** (the visual's local transform — lean, bank, flips/spins, bails, flight attitude,
  landing squash, stun all in one value, applied as-is), **`PoseKind`** (stride / air / tucked) and
  **`Anim`** (a `Vector4`: on foot speed + gait phase; mounted whatever the `Rideable` writes in
  `WritePose` and reads in `AnimateRemote` — bike cadence + crank angle, craft spool + throttle).
  Owner and remote draw the on-foot figure through the same `ApplyFootPose`; a fresh gait phase or
  crank angle is taken as-is and only integrated between updates. **A new mount with moving parts
  plugs into `WritePose`/`AnimateRemote` and never touches the sync code** — the drift cars'
  slip, steer angle, wheel spin and rpm go there. The pose is reset on every ride change (see the
  gotcha). Remote helicopters and planes are heard (spatial `EngineSynth` from `Anim`), and a
  parked craft's rotor follows the synced `VehicleBody.Spool` instead of a guess from `EngineOn`.
  Check: `<godot> --path . -- --synccheck [--at E,N]` — an owner runs walk, sprint, jump, slide, a
  leaning bike ride, a helicopter climb and a rolling plane while a MIRROR (foreign authority, so
  it takes the remote path) is fed the owner's real `ReplicationConfig` properties at 20 Hz of
  wall time; non-zero exit if the mirror differs the frame after an update (must be 0: that is
  state not replicated) or drifts between updates past one interval. Measured: 0.0000 fresh on
  pose, hand and crank; with the old replication set, plane attitude off by 3.0, crank 3 rad,
  bike lean 0.6, hand 0.64 m. One process, no sockets: it tests that the state is complete and
  both sides derive the same picture, not ENet.
