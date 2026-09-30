# Race NPCs and races belong to nobody: handoff and host migration (#50)

- **Simulator = authority.** An NPC's current simulator is its node's multiplayer authority on every
  peer (`FootPlayer.SimPeer`). The id and node name (`npc_<owner>_<n>`, `-(owner*1000+n)`) are only
  names. `RaceManager.SimOf(e)` (not `OwnerOf`) decides whose `Checkpoint`/`Crossed` count and where
  `Setup`/`Result`/`Dropped` go. A peer that spawns the NPC later learns the simulator from `SimPeer`,
  a spawn-only (`ReplicationMode.Never`) property of `Sync` applied inside `_Ready` — the spawn data
  still names the original owner.
- **Zone**: a client may simulate an NPC only while its player is within `RaceNpcs.Zone` = 600 m
  (horizontal); it keeps it to ×1.5 = 900 m. Why: a client has collision on the 1 km tiles within one
  tile of each anchor, so ≥ 1000 m of solid ground around it; an NPC ≤ 900 m away drives on ground its
  simulator already has. A plane pilot 5 km up is never handed a car in a valley.
- **Review every 1 s (server, `RaceNpcs._Process`)**: simulator gone, silent > 2.5 s
  (`StaleSeconds`: a `kill -9` takes ENet 5-30 s to report), or > 900 m away (after a 5 s hold, no
  ping-pong) → `HandOff`: best player in the zone (entrants of the NPC's race, then ground over air,
  then nearest; still sending; under `MaxSimulated` = 12 NPCs), else **retire** (DNF if racing).
  `ServerWorld.OnPeerDisconnected` → `RaceNpcs.PeerLeft` does the same at once.
- **HandOff**: server `SetSimulator` (node + `Sync` authority; children do not follow), `Migrate` RPC
  to everyone, `RefreshRelays` (the server relays skip the current simulator), `RaceManager.ResumeNpc` →
  `Setup(..., next, resume: true)` with the countdown left and the next checkpoint. On the new
  simulator `FootPlayer.SetSimulator` takes the body over where it is drawn, with `NetVel` and the
  replicated mount (`ApplyRide`), `_placed` so it is not dropped again; the `RaceNpc` driver (on every
  peer, active only on the authority) re-arms `AutoPilot` at the line point nearest the car past its
  last checkpoint (`NearestPast`: a forward search from the grid stops at a hairpin behind it). Old
  and other peers: `RemoteInterpolator.NewSender()` (new sender clock; the picture eases, no jump).
- **Host migration** (`RaceManager.MigrateHost`, 1 s review + `/race leave`): nearest human entrant
  still in, else nearest player within the zone of the field (spectators count); a duel before GO
  ends; nobody → a race not started ends ("the host left"), a running one goes on hostless while
  anyone runs an entrant. Announced: `[race] #N is now hosted by X`.
- **Silent remote bodies are not solid** (`FootPlayer.SilentSeconds` = 1 s without a state): a
  crashed leader's frozen car mid-road stopped the whole field behind it until ENet timed out.
  "A state" = a new `NetTime`: the server relays repeat the last one at 30 Hz whatever the owner does.
- Check (loopback, Col du Mollendruz 2518038,1167321, each client its own `--cache`): server + A
  (`--raceauto --racestart <m> --racenpc 2`) + B. `--chatafter "<s> <line>[;<line>]"` sends chat
  lines s seconds after the grid is announced (e.g. `"12 /race leave;/city La Motte"`: out of the
  zone mid-race). `--npccheck` prints every NPC with `v`, `sim=` and `(here)` on its simulator.
  Measured (#50): B racing, A `kill -9` or SIGTERM at GO+12 s → handed to B 2.5 s later, re-armed at
  ~280 m / 115 km/h, classified 2nd/3rd, B made host. B a spectator mid-course (2518450,1166900),
  1 km race, A killed → B simulates and hosts, NPCs P1/P2. B 6 km away → both retired, DNF. A leaves
  and teleports 4 km → NPCs to B; B teleports too → retired, DNF.
- Not solved here: a teleport across the course is accepted as a finish (every checkpoint on one
  segment), and a pilot's reset-to-line can bring a teleported racer back onto the course.
