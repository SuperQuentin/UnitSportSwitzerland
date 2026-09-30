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
- Check (scratch script, see PR): server + A (`--raceauto --racestart 3000 --racenpc 2`) + B at the
  same spot, `kill -9` A mid-race; `--chatafter "<s> /city Riddes"` sends a chat line s seconds after
  the grid is announced (teleports a client out of the zone mid-race). `--npccheck` prints every NPC
  with `v`, `sim=` and `(here)` on its simulator.
