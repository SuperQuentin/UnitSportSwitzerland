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
