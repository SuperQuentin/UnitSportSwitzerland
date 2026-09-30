# Races between players, in anything

- **Races** (`World/RaceManager`, `World/RaceCourse`, `World/RaceGates`, `Player/GatePilot`; issues #24,
  #39). `World/Race` on server and clients — RPCs route by node path, like `World/Chat`.
  - **Any number at once**: server `Dictionary<int, Race>`, 2..32 entrants each, an entrant in at most
    one race (a second `/race start|join|duel` is refused until it leaves, finishes or is out). Every
    RPC carries the race id; a race's state is freed when it ends (`Dropped(raceId, 0)` releases every
    client still holding a racer in it). `SameRace(a, b)` (server) is true for two entrants of one
    running race who have not finished.
  - **Commands**: `/race start [metres] [mount|open]`, `/race start air <place|metres> [mount]`,
    `/race duel <player> [same args]` (a private challenge; the race starts the moment it is accepted
    with `/race join`), `/race join [id]` (no id: the duel you were challenged to, else the newest open
    race), `/race leave`, `/race cancel [id]` (host or console), `/race list`, `/race npc [n] [metres] [mount]`, `/race duel npc` (NPC opponents, below). Mounts: `foot bike skis
    car <car name> moto|r1 monster plane heli paraglider wingsuit`, or `open` (everyone keeps theirs).
    Default: the AE86 on the ground, the plane in the air. A class whose `Rideable.Create` is null (the
    motorbikes, `RideKind` 64/65, before #38 lands) is refused with a message.
  - **Entrants are `long`**: a peer id, or an NPC `-(ownerPeer * 1000 + n)`. A client enters its NPCs
    with `RequestNpcEntrants(raceId, long[] ids)` (`EnterNpcs` also names them first via
    `NameNpcEntrants(ids, names)`); the server checks each id encodes the sender, and accepts
    `Checkpoint`/`Crossed` for an entrant only from its owner. At the close of entry the owner gets
    `NpcSetup` (course, slot, mount); `TrackNpc(id, () => position)` lets the race report that NPC's
    checkpoints like the player's. Not in duels (except `/race duel npc`, below).
  - **NPC opponents** (`World/RaceNpc`, `World/Npcs`): `/race npc [n] [metres] [mount]` puts n (≤ 8) NPCs
    into the sender's open race, or opens one with them (a count ≤ 8, a larger number is metres);
    `/race duel npc [metres] [mount]` is a duel against one (the race's `Invited` is the NPC id, so it
    starts as soon as the course is built). Mount: the race's, else the one asked, else the sender's car,
    else the AE86. The **server** spawns them out of sight on the race road and they drive in to their slots (`npc-arrivals`; before the road is found they are queued, then spawned) (`RaceNpcs.Spawn`, spawn data
    `[owner, n, kind, pos, yaw]`, node `npc_<owner>_<n>`, named "NPC <mount> #n") and enters them itself —
    it already knows the ids, so the client-side `EnterNpcs` path is not used by `/race npc`. An NPC is a
    `FootPlayer` with `Npc = true` and its **current simulator** as authority (first the owner client, see `npc-handoff`): it publishes NetPos like any
    authority player, is its own interest target, is its own collision anchor, has no camera, feel
    or input. Its `RaceNpc` driver takes `NpcSetup` (grid slot), `TrackNpc`s its position (RaceManager
    reports its checkpoints), drives `AutoPilot.For` at GO with `RaceManager.Others` (every player on the
    road, `WorldVelocity`), and brakes after `NpcFinished`. **They retire when their race ends**
    (`End` → `RaceNpcs.Retire`), and otherwise outlive whoever asked for them: handed to another client in their zone, or retired (`npc-handoff`). Cap 8
    road, position and `WorldVelocity` vector), and brakes after `NpcFinished`. Each NPC has its own
    skill (0.8-1) and aggression (0-1) from its id (`AutoPilot.Temperament`, printed `[npc] ... drives,
    skill .. aggression ..`), and logs its spins, mistakes and resets (`driver-skill-mistakes`). **They retire when their race ends**
    (`End` → `RaceNpcs.Retire`), and all of an owner's go when it disconnects (`ForgetOwner`). Cap 8
    per owner, 32 bodies. Classes: `AutoPilot.Drives` (cars only until the ground pilots land); other
    classes are refused with a message. **Air NPCs are refused**: `GatePilot` flies by pressing the
    input actions, which would fly the owner, not its NPC — it needs a `RideControls`-style seam first.
    Check: dedicated server + A (`--raceauto --racestart 1000 --racenpc 2 --at E,N`: `--racenpc N` sends
    `/race npc N` when A's own race opens) + B (`--npccheck`: a shape query on each remote NPC every 5 s,
    prints `solid=`), each with its own `--cache`. Measured at the Col du Mollendruz, 1 km: Takumi 0:37.8,
    NPC #1 0:40.2, NPC #2 0:40.6, solid on B throughout, removed at the results; a duel vs one NPC with A
    killed mid-race: the NPC removed on the server and B at once.
  - **Courses** (`RaceCourse`): ordered checkpoints + a line for progress. Ground (foot, bike, skis,
    car, moto): `RaceRoute` from the host, a checkpoint every 200 m. Air: a straight line of gates to a
    `places.json` place (the same `PlaceIndex.Search` as `/city`) or that far along the host's heading,
    one every 400 m, each a 40 m sphere tested against the whole frame's step (a slow frame must not
    let a plane jump a gate). Plane/heli gates: the highest terrain within 150 m + 60 m. Gliders: grid
    held at terrain + 300 m, gates at terrain + 25 m but never below a glide slope a little steeper
    than the craft's (paraglider 7.5:1, wingsuit 2.2:1 after an 80 m fall); the course ends where the
    terrain rises out of reach. A wingsuit race needs a real drop — none of the Mollendruz data has
    one (300 m buys ~500 m of flight), so it is refused there with a message.
  - **The air course is built on the server** from its own terrain files: it streams heights only
    around players, but it has every tile on disk and a coarse tile is a few KB, so no client has to
    be trusted with the course or send it up.
  - **Fair grid**: two columns staggered 8 m. Ground: each entrant's finish is its own slot's arc +
    the race length, checkpoints likewise (a rear slot drives no further). Air: slots 100 m + 8 m·slot
    behind gate 0, ±12 m; each entrant's clock starts at its own pass through gate 0 (the server's time
    of that checkpoint), a flying start at the same speed for everyone.
  - **Holding the grid** until GO: ground — placed in the slot on the mount, car on the handbrake, other
    mounts on the brake and put back if they drift 1.5 m. Air — mount where you stand (`SetRide` needs
    the floor, so a player already flying something else is told to land), then `DebugLaunch` into the
    slot every frame: plane at 45 m/s, paraglider at trim, helicopter hovering (its rotor spools up
    during the countdown), wingsuit pilot on foot standing on nothing, base-jumping at GO. Set the
    body's yaw BEFORE `SetRide`: a craft starts on the body's heading.
  - **The server times everything**; a client reports checkpoints (accepted in order only) and the
    line. A finish with checkpoints missed is "not counted" and out. DNF at a deadline of the course at a
    slow pace for the class.
  - **Pilots** (`--raceauto`): cars `new AutoPilot(route, player, spec)`, other ground classes
    `AutoPilot.For(route, player)` (null = no pilot for that class yet, logged). Air: `GatePilot` flies
    through the real input actions (like `--flycheck`), pressed from `_PhysicsProcess` so a one-step
    Jump is never lost. Each slot aims at its own lane through the ring (±5/15 m, ±8 m): two pilots
    aimed at the centre flew in formation 9 m apart until one helicopter crashed.
  - Check: `tools/racecheck.sh [car|bike|foot|skis|moto|plane|heli|paraglider|wingsuit] [E,N]
    [metres|place]`, `skip` (plane race, B never reports gate 1: must be refused), `two` (a car duel and
    a plane race at once, 4 clients; B must be refused the second race). Each client has its own
    `--cache`. Needs ~5 GB free (~9 GB for `two`). Scripting flags: `--racestart "<args>"`,
    `--racecmd "<args>"`, `--racejoin [host]`, `--raceskip N`, `--racenpc N`.
  - `CHUNKS=<dir> tools/racecheck.sh ...` passes `--chunks` to every process (a worktree has no
    `terrain_chunks`); a `--raceauto` client prints `[race] (auto) t .. left .. m, off .., km/h` every 2 s.
    #52 fixed `moto` (both R1s ran wide off a R 50 m bend at 1.4 km and sat in a field: DNF) and `foot`
    (both runners stalled at a RoadGen point gap 280 m from the line) — see `autopilot-raceroute`.
    Measured after: car 1:02.61 / 1:02.80, bike 1:25.35 / 1:25.78, moto 0:48.05 / 0:48.25, foot
    4:17.46 / 4:17.48; A with 3 NPC AE86s + B (`--npccheck`, solid=True): 1:03.06, 1:03.31, NPCs
    1:07.02-1:07.16.
  - Measured: Col du Mollendruz 1.5 km AE86 1:05–1:14; Mont-la-Ville → Montricher 5.2 km plane
    1:30–1:32, heli 1:37; Haut du Mollendruz → Pétra Félix 1.9 km paraglider 2:12–2:14.

- **The server verifies every checkpoint and the finish** (`RaceManager.Plausible`) instead of
  believing the client: the entrant's body — the server's proxy copy at its latest replicated
  position — must be within 80 m of that checkpoint (gate radius + 60 m in the air), and it must
  have got there no faster than the mount can go over the straight-line distance from the
  previous one (`MaxPace`: foot 14, bike/skis 45, canopies 70, aircraft 120, cars/motorbikes
  125 m/s). A report from the wrong place is ignored; an impossible pace is out. Before this a
  `/city` teleport across the course was classified. Check: a racer teleported 25 s after GO is
  refused at its next checkpoint ("5031 m from it — ignored") and ends DNF.
