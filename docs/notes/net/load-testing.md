# Load testing: swarm bots, server stats, remote smoothness

- `tools/loadtest.sh <players> [label] [seconds]` (env `CHUNKS`, `PORT`, `SEED`, `AT`): dedicated
  server with `--serverstats`, bots in `--swarm` processes of ≤ 16 (each bot its own
  `SceneMultiplayer` on `/root/BotK`, a minimal mirror of the client tree, kinematic driving in
  `Swarm.Drive` — the one place to adapt when replication changes), one real observer client with
  `--netsmooth`. Every process has its own `--cache`. Output in `test_output/loadtest/<label>/`.
- Swarm cost: ~190 MB per process + ~3 MB and ~1.5 % of a core per bot. A full client is ~2.2 GB:
  on a 15 GB machine several agents running clients at once get OOM-killed — check `free -g`,
  and a run with a process killed (exit 137) is invalid.
- `--serverstats`: frame (wall clock) and busy ms p50/p99/max, working set, heap, GC counts and
  pauses, ENet bytes/packets per second total and per peer, rtt/loss, kernel UDP receive drops.
- **Results, 32 players (issue #37)**:

  | | before | after |
  |---|---|---|
  | UDP drops at the server socket | 647,712 | 0 |
  | bots disconnected | 15 / 31 | 0 |
  | packets in / s (peak) | 24,019 | ~2,300 |
  | net in / out | 2,074 / 1,952 KB/s | ~330 / ~227 KB/s |
  | observer: freeze frames / longest | 1,253 / 198 ms | 0 / 0 |
  | observer: step p99 / (v·dt) | 5.79 | 1.09 (plane, 55 m/s, 1.5 km) |

  Soak, 32 players × 30 min: working set 360 → 374 MB over the last 20 min (+3.9 %), heap flat
  ~150 MB, GC pause max 5.6 ms, steady-state frame max 17–28 ms, 0 drops, 0 disconnects.
- **After the server relay** (owners send once; server rebroadcasts near 30 Hz / far 6 Hz): 32
  players incl. the grouped race start — packets in 877/s peak (was ~5,000 with owner relaying to
  audiences, 24,019 originally), 0 UDP drops, net in 106 KB/s, out 539 KB/s; observer 0 freezes.
- The one frame spike left (~110 ms) is 31 bots joining in the same second at startup: a join costs
  < 0.6 ms on the server, the first one 30 ms (JIT of the cold paths).
- **Relay was O(N²) before**: packets in tracked N·(N−1)·60 because every client sent every frame to
  every peer through the server; the Linux default 208 KB socket buffer overflows from ~20 players.
