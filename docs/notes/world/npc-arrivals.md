# Race NPCs drive in to the grid

- **NPC arrivals** (`World/NpcArrival`, issue #51): a race NPC is not placed in its slot; it
  appears out of sight on the race road and drives there with the real car, through
  `FootPlayer.RideControls` only.
  - **Server** (`RaceManager.SpawnNpcs`): the lane is `RaceRoute.Behind` (the walk from the host the
    route did not keep, 600 m) reversed + the route to 750 m. `NpcArrival.Plan` gives each NPC a
    style and an entry point: the front NPC slots come **from ahead** (three-point turn, handbrake
    U-turn or donut, cycled from a seeded start), the rear ones **from behind** (plain, handbrake
    flick, or late on the brakes). Entry 160 m+ from the grid, 45 m apart, hidden from every player
    (220 m, or 110 m with the road bending 15 m off the sightline), 15 m from any body. Then
    `NpcArrive` RPC to the owner (lane, style, provisional slot = its index among the entrants so
    far). `/race npc` while the road is still being found queues them (`PendingNpcs`) until
    `Opened`. GO waits until every NPC reports `NpcStaged`, at most 75 s after they spawned.
  - **Order, so nobody crosses a parked car**: from ahead they settle rear-slot first (each turns
    round 12 m past its own slot and reverses down its column, past the empty slots ahead); from
    behind front-slot first (driving up its column past the empty ones behind). Entry points are
    spaced in that order, so the queue is already right. A car stops 5.5 m behind a body, 12 m
    behind one that is turning, and waits 22 m short of a turning spot someone is on; a parked
    player (not an NPC) is gone round if the road leaves 2.9 m of offset.
  - **Owner** (`RaceNpc` → `NpcArrival.Drive`): approach along the lane (pure pursuit), then the
    manoeuvre, then `Park` (forward or reverse along its column; if off by > 0.7 m / 6° it pulls 6 m
    forward and comes back, twice at most). The handbrake turn and the donut need a clear disc
    (6.5 / 5.5 m: ground within 0.9 m of the centre, knee-height rays hit nothing) checked 30 m
    before, else they become a three-point turn; a drift that ends not straight carries on as one.
    `NpcSetup` only moves the target (`SetSlot`: a later entrant pushes every slot 8 m up); not in by
    GO − 2 s it parks, by GO − 0.35 s more than 1.5 m / 10° out it is placed by hand (`PlaceAt`,
    logged). At GO the AutoPilot takes over as before.
  - **Car-model traps**: at a crawl on full lock the front tyres scrub hard (the tyre model sees the
    kinematic steer angle as slip), so 0.45 throttle did not move the car — the pedals add throttle
    with the lock. The Game profile's catch straightens any slide within ~1 s, so a donut is a
    tight circle at ~5 m/s with a handbrake flick each time it grips. Brake at a standstill selects
    reverse: stop with the handbrake below 1.2 m/s. Setting `Rotation` does not turn a ridden car
    (its step writes it back from `Motion.Yaw`): use `FootPlayer.PlaceAt`.
  - Check: `<godot> --path . -- --arrivalcheck[,prefix] [--at E,N] [--npcs N] [--style 0..3]
    [--shift N] [--seed N] [--trace]` — a parked stand-in player at the start, N NPCs arriving,
    grid "sent" when all are in; per NPC distance, top speed, metres reversed, degrees turned,
    legs, slot error at GO, impacts, footprint contacts. Windowed with a prefix: a frame every
    0.4 s from a camera on whoever is manoeuvring. Measured at 2518038,1167321 (Col du
    Mollendruz, 6 m road): all five in within 0.3 m / 1.3°, no contact; three-point turn 4 legs,
    13 m reversed; handbrake turn 153° then reversed 16 m; donut ~500° in 7 s.
