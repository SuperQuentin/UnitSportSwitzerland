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
    race), `/race leave`, `/race cancel [id]` (host or console), `/race list`. Mounts: `foot bike skis
    car <car name> moto|r1 monster plane heli paraglider wingsuit`, or `open` (everyone keeps theirs).
    Default: the AE86 on the ground, the plane in the air. A class whose `Rideable.Create` is null (the
    motorbikes, `RideKind` 64/65, before #38 lands) is refused with a message.
  - **Entrants are `long`**: a peer id, or an NPC `-(ownerPeer * 1000 + n)`. A client enters its NPCs
    with `RequestNpcEntrants(raceId, long[] ids)` (`EnterNpcs` also names them first via
    `NameNpcEntrants(ids, names)`); the server checks each id encodes the sender, and accepts
    `Checkpoint`/`Crossed` for an entrant only from its owner. At the close of entry the owner gets
    `NpcSetup` (course, slot, mount); `TrackNpc(id, () => position)` lets the race report that NPC's
    checkpoints like the player's. Not in duels.
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
    `--racecmd "<args>"`, `--racejoin [host]`, `--raceskip N`.
  - Measured: Col du Mollendruz 1.5 km AE86 1:05–1:14; Mont-la-Ville → Montricher 5.2 km plane
    1:30–1:32, heli 1:37; Haut du Mollendruz → Pétra Félix 1.9 km paraglider 2:12–2:14.
