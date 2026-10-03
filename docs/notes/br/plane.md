# Battle Royale cargo plane (#207, part 5 of #177)

- **Line** (`BrFlight`, pure, zone metres): a seeded angle and an offset up to 30 % of the half side.
  It starts `Lead` 1.5 km before the square; the doors open on entering it and close `DoorMargin`
  300 m before leaving it; the plane flies on 4 km and is gone. 80 m/s (×2 at most on a test pace).
  Every peer rebuilds it from `Seed`, `FlightStart` and `FlightAlt` in `BrState` and `ClockSync.ServerNow`:
  nothing is sent while it flies. 5-7 km regions: 75-135 s to the doors closing; rounds 31-42 min.
- **Altitude** (server, at GO): the highest point of the 100 m horizon lattice under the whole line
  + 650 m, never under 1200 m. The lattice is asked for when the lobby opens, and GO waits for it (30 s
  at most). `--brcheck` compares it with the coarse tiles along the line: ~640 m clear on three real regions.
- **Client** (`BrManager.Flight`):
  - `Board()` (RPC at GO): `EnterMatch`, then `FootPlayer.Carrier = Hold`. The body sits in the
    fuselage each physics step: collision off, hidden, velocity = the plane's (so the replicated
    position extrapolates with it).
  - A chase camera (`PlaneCam`) circles the plane with the look; the HUD gives the door times.
  - **E** (interact) jumps once the doors are open: `Leap` out of the ramp into the wingsuit, at the
    plane's speed capped to 55 m/s. Space is left alone: it opens the parachute a moment later
    (pressing it to jump would open the canopy the same frame).
  - When the doors close, the client pushes itself out. There is no "ground too near" push: the
    ground a client holds under the plane right after the jump across the country is a placeholder
    until real tiles stream in (it fired over Bioggio at 1.3 km).
  - Others aboard are `FootPlayer.Stowed` (hidden, not solid), whatever the interior visibility pass says,
    until the server marks them `Jumped`.
- **Server**: `Jump()` RPC marks `BrEntrant.Jumped` (logs a jump well before the doors); at doors-close + 1 s
  everyone left is marked. `Survived` counts from GO.
- **Look** (#420): the military freighter players fly (`AirlinerRig.CreateFreighter`, the player note
  `airliners`): `BrPlane` shows it gear up, propellers turning, ramp and para doors open, its model's
  fuselage middle on the flight line (`BrPlane.Middle`; the hold's `Carrier` point 1.2 m under it is
  0.5 m over the hold floor); jumpers leave from the open ramp's lip (`BrPlane.Ramp`). Engine drone
  from `SfxSynth.Engine` at 0.55 pitch, slight wing rock. `CargoPlaneMeshBuilder` is gone. Maps draw the line dashed, the jump
  stretch solid, the plane as an arrow, until the doors close.
- **Checked**: loopback on a generated world and on real regions (Altdorf at 3349 m, Mendrisio):
  boarding, hidden bodies on the remote peer, E refused before the doors, jump, push-out, look-to-turn.
  Real-terrain runs under heavy machine load lost a client twice in five (once a native access
  violation, once an ENet drop while stream requests timed out); part 4b saw the same once, before the plane.
- Not yet: a warm-up in the hold during the lobby, plane sound stings (part 6), seats or a visible hold interior.
