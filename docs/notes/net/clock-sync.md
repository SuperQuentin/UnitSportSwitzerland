# One shared clock: `ClockSync.ServerNow` (`Net/ClockSync`, `World/Clock`)

- Nothing before the radio needed the machines to agree on the time (races send seconds, poses
  carry the sender's clock). A CD "started at T" only means something if every listener can turn
  T into "how far in am I", and dancers on every screen must be on the same beat.
- Clients ping the server unreliably (5/s for the first 3 s, then every 2 s); the pong carries the
  server's `Time.GetTicksUsec()` clock. Of the last 8 samples the one with the **shortest round
  trip** decides: `offset = serverNow + rtt/2 − recvNow`, `ServerNow = local + offset`. Unreliable on
  purpose: a retried ping would put queueing delay into the measurement.
- Offline and on the server the offset is 0 — `Multiplayer.IsServer()` is true in both cases,
  which is right here. `Synced` is false until the first pong; the radio plays from 0 and is
  corrected within a second.
- Static, one per process; reset when the node leaves the tree. Anything that must happen at the
  same wall moment on every peer (song position, beat phase) is a function of `ServerNow` and a
  replicated start time, never of anything local like playback position.
