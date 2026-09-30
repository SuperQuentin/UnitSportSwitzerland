# Vision-based interest: a player out of sight does not exist on your client

- `Net/Interest` (pure rules) + `Net/InterestService` at `World/Interest` on server and clients (#37).
- **The server decides**, every 0.5 s for every pair: range = the target's size at the 1152 px render
  width, cut at 1.5 px (walker ~0.9 km, car ~2.2 km, plane/heli ~5 km; ×1.6 above 25 m AGL — a
  silhouette against the sky carries), clamped to the viewer's reported `CameraFar`; line of sight
  over `horizon.bin` (100 m lattice, 12 m margin); always within 150 m and always for the same race
  (`Together`); hysteresis ×1.15 and at most one flip per pair per second. A newcomer sees nobody
  until its first round (0.5 s) instead of being sent everyone and having most taken back.
- **Two synchronizers per player.** `Sync` (owner authority) carries the data; its filter sends
  only to the owner's set, which the server RPCs to it (`SetRelevant`). `Vis` (server authority,
  EMPTY config) carries only the decision: **Godot consults only server-authority synchronizers
  for spawn visibility** (`SceneReplicationInterface::_update_spawn_visibility` skips the rest), so
  without it the server can never despawn a client-owned node. Out of view the node is despawned on
  that peer and respawned with current state when back.
- **Visibility filter gotchas** (both cost a debugging round): Godot also calls a filter with
  peer **0** = "everyone?" — answering true makes it broadcast; `ServerSees` must answer **true for
  the owner itself** or the owner loses its own player. `Vis` has `ReplicationInterval` 3600 s:
  empty or not, Godot asks its filter every frame for every peer otherwise.
- Race NPCs (`npc_<owner>_<n>`) are shown to whoever sees their owner (`FootPlayer.NetOwner`).
- Gunfire goes only to peers in the shooter's set (`CombatManager.SendShot`).
- Check: `<godot> --headless --path . -- --interestcheck` (rules + interpolation self-checks);
  loopback: two clients 100 m apart at the Mollendruz see each other, one at Riddes (70 km) never
  spawns them.
