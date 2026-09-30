# Car races between players

- **Car races between players** (`World/RaceManager`, `World/Race` on server and clients — RPCs route by
  path, like `World/Chat`; issue #24). `/race start [metres]` (any player) opens a race on the main road
  from the host's position (`Player/RaceRoute`, built on the server from its `.road` tiles), 15 s to
  `/race join`, `/race leave`, `/race cancel` (host or console). The server sends each entrant the route
  (centreline + widths to just past the finish), its slot on a single-file grid and a **relative**
  countdown in seconds (never a clock time: the machines' clocks need not agree). The client stands its
  player on the grid in a car (mounts the AE86 if on foot), holds the **handbrake** until GO (the brake at
  a standstill selects reverse), then gives the car back to the player — or with `--raceauto` to an
  `AutoPilot`. **The server times everything**: a client only reports checkpoints (every 200 m, accepted
  in order only — a shortcut misses one) and crossing the line; the finish time is the server's own
  clock from its own start. Standings go out on chat; DNF at a deadline of the distance at 10 m/s.
  Check: `tools/racecheck.sh [E,N] [metres]` — a dedicated server and two `--raceauto` clients on
  loopback (`--racestart`, `--racejoin` script the chat), passes when the server classifies both.
  Measured at the Col du Mollendruz, 1.5 km: Takumi (AE86) 0:58.13, Keisuke 1:01.46.
- **NPC opponents** (`World/RaceNpc`, `World/Npcs`, issue #39): `/race npc [n] [car]` (opens a race if none)
  and `/race duel npc`. An NPC is a `FootPlayer` with `Npc = true`, spawned by the server through the player
  spawner (spawn data `[owner, n, kind, pos, yaw]`, node `npc_<owner>_<n>`, entrant id `-(owner*1000+n)`),
  with the **owner client** as its authority: it simulates the car (AutoPilot via `RideControls`), is its own
  collision anchor, and reports its checkpoints/crossing for it (the server checks the sender owns that id).
  No camera, feel layer or input on the owner's copy. Cap 8 per owner, 32 bodies total; the owner's previous
  NPCs are removed on its next request and all of them when it disconnects. Check: server + client A
  (`--raceauto --racestart 1000 --racenpc 2`) + client B (`--npccheck`: shape query on each remote NPC every
  5 s); each client with its own `--cache`. Measured at the Col du Mollendruz, 1 km: Takumi 0:36.6,
  NPC #1 0:39.9, NPC #2 0:40.4, solid on B throughout, both gone on B and server when A quits.
