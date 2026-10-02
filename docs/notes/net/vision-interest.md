# Vision-based interest: a player out of sight does not exist on your client

- `Net/Interest` (pure rules) + `Net/InterestService` at `World/Interest` on server and clients (#37).
- **Measured in LV95** (#185): from what each owner published (`FootPlayer.Global`, exact on the
  server's proxies), each viewer's pairs judged in an `OriginFrame` anchored at that viewer, and the
  ground (`HorizonGround`) read by LV95. The server's own origin plays no part: players 1,000 km apart
  are judged as precisely as two at its origin.
- **The server decides**, every 0.5 s for every pair: range = the target's size at the 1152 px render
  width, cut at 1.5 px (walker ~0.9 km, car ~2.2 km, plane/heli ~5 km; ×1.6 above 25 m AGL — a
  silhouette against the sky carries), clamped to the viewer's reported `CameraFar`; line of sight
  over `horizon.bin` (100 m lattice, 12 m margin); always within 150 m and always for the same race
  or the same vehicle (`Together`: `RaceManager.SameRace` or `PassengerService.Together`, #158); hysteresis ×1.15 and at most one flip per pair per second. A newcomer sees nobody
  until its first round (0.5 s) instead of being sent everyone and having most taken back.
- **The server rebroadcasts; owners send once.** `Sync` (owner authority) goes to the server ONLY.
  The server's proxy copy carries two server-owned relays with the same properties: `RelayNear`
  (30 Hz, spawn state) for viewers within 300 m or in the same race, `RelayFar` (6 Hz) for viewers
  further away who can still see the player. Filters read `InterestService.RelaysNear/RelaysFar`,
  recomputed every round from the viewers' sets — the AUDIENCE of a target (who sees it), since
  visibility is asymmetric (a plane is seen 8 km away, a walker 0.9 km). Owners used to send one
  copy per viewer for the server to relay: N·(N−1) datagrams into one socket, which overflowed at
  a 32-car race start (5k packets/s, thousands of drops); now ~N·30 in (877/s at 32, 0 drops).
- `Vis` (server authority, EMPTY config) decides spawn visibility: **Godot consults only
  server-authority synchronizers for spawn visibility** (`SceneReplicationInterface::
  _update_spawn_visibility` skips the rest). Spawn visibility is the OR of Vis and the relays.
  A target leaving a viewer's sight is despawned there after a 0.4-0.5 s grace (`_leaving`).
- **Visibility filter gotchas** (both cost a debugging round): Godot also calls a filter with
  peer **0** = "everyone?" — answering true makes it broadcast; `ServerSees` must answer **true for
  the owner itself** or the owner loses its own player. `Vis` has `ReplicationInterval` 3600 s:
  empty or not, Godot asks its filter every frame for every peer otherwise.
  All player synchronizers have `VisibilityUpdateMode = None`: filters run only on an explicit
  `UpdateVisibility` (`perf-visibility-on-change`).
- Race NPCs (`npc_<owner>_<n>`, `FootPlayer.NetId` = their negative entrant id) are interest targets of
  their own: seen and relayed from where the NPC is, whoever simulates it; their simulator (the node's
  authority) never gets the relays, always has the node (`Vis`). See `docs/notes/world/npc-handoff.md`.
- Gunfire goes to the server once and is relayed only to the peers who can see the shooter (`CombatManager.Shot` → `ShotFrom`).
- Check: `<godot> --headless --path . -- --interestcheck` (rules + interpolation self-checks);
  loopback: two clients 100 m apart at the Mollendruz see each other, one at Riddes (70 km) never
  spawns them.
