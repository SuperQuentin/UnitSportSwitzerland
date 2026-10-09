# Movie puppets: a FootPlayer driven from a recording (#638)

- `FootPlayer.Puppet` (init-only) turns a remote-style copy into a movie puppet:
  - **No synchronizers:** `_Ready` skips `SetUpNet`, so nothing joins the multiplayer replication. A puppet in an
    online session exists on this peer only.
  - **No `RemoteInterpolator`:** `OnNetState` places it exactly at `NetGlobal` / `NetYaw`, like the server's proxy.
    The interpolator's render clock only moves forward and smooths corrections, so scrubbing backwards would
    drift and lag.
  - **Never solid** (`_body.Disabled`).
  - **Gait phase straight from `Anim.Y`:** no `AdvancePhase` between packets, so a paused puppet's feet stay put.
- `MovieStage` writes every property each frame (`ActorIo.Write`) and finishes with a new `NetTime` stamp: that
  setter is what applies the state.
- **Names and authority** are `MovieStage.PuppetBase` (2 000 000 000) + lane, far from any peer id. `RidingWith` is
  remapped from the recorded peer id to the puppet of the lane with that `Lane.PeerId`, so a passenger finds its
  host with the sibling lookup `Host` already does.
- **Hiding a puppet with no clip at that time:** `ProcessMode.Disabled` plus `Visible = false`. Setting `Visible`
  alone is undone the next frame, because a remote copy sets its own visibility from `SameSpaceAsLocal`.
- Rides animate through the usual `Rideable.AnimateRemote` from `Anim`, so rotors, cranks and doors come for free.
  `AnimateRemote` integrates over the real frame time, so a paused plane's propeller keeps turning.
