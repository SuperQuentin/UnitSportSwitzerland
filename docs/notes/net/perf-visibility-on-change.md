# Player visibility is updated on change, never every frame (#221)

## Rule
- Player synchronizers (`Sync`, `RelayNear`, `RelayFar` and `Vis` in `FootPlayer`) keep
  `VisibilityUpdateMode = None`. Do not set them back to `Idle`/`Physics`, and give any new player
  synchronizer `None` as well.
- **Every change to what a peer may see must call `UpdateVisibility` itself**, at the place where
  the change happens:
  - who exists on a peer (spawn and despawn) → `FootPlayer.RefreshNetVisibility(viewer)`, which is
    `_vis.UpdateVisibility(viewer)`;
  - who gets the 30 Hz or 6 Hz stream → `FootPlayer.RefreshRelays()`;
  - a change of authority (race NPC handoff, `SetSimulator`) → refresh the old AND the new
    simulator (`RaceNpcs.HandOff`), and `_sync.UpdateVisibility()` on the peer that takes over.
- A visibility filter may read only state whose every change is followed by one of these calls.
  When you add an input to `InterestService.ServerSees`, `RelaysNear`/`RelaysFar`, or a filter
  (a new team, spectator, death or match rule), find each place that input changes and add the
  refresh there. Inputs that `InterestService.Evaluate` reads every 0.5 s (position, ride,
  `Together`) are already covered, because it refreshes every pair whose answer flipped.
- Never call `UpdateVisibility(peer)` for a peer that has disconnected. Godot logs
  `!peers_info.has(p_peer)` and skips it; check that the peer's player node still exists first.

## Why
With `Idle`, Godot ran every managed filter `Callable` every frame for every peer: O(N²)
native→managed calls. `tools/loadtest.sh`, Windows, steady state t = 20–150 s, per-frame busy (PR #233):

| | 16 players before | after | 32 players before | after |
|---|---|---|---|---|
| busy p50 / p99 / max | 1.78 / 3.48 / 43 ms | 0.83 / 1.78 / 124 ms¹ | 5.23 / 8.08 / 69 ms | 1.43 / 3.58 / 42 ms |
| frames > 50 ms | 4 | 9¹ | 21 | 5 |
| net out avg | 58.7 KB/s | 60.1 KB/s | 311.9 KB/s | 310.8 KB/s |

¹ All in the second when the observer client left (t = 141–143 s).

Net out is unchanged: the same data still goes to the same peers.

## Same logic, preserved
- The filters themselves are unchanged. Godot still evaluates them on its own when a peer connects
  (`on_peer_change`) and when a synchronizer starts (`on_replication_start`), so join and spawn
  need no extra call.
- `InterestService.Evaluate` (every 0.5 s) calls `RefreshNetVisibility` for each pair whose answer
  flipped and `RefreshRelays` for each target whose audience changed. The `_leaving` grace expiry
  calls `RefreshNetVisibility`.
- A disconnect is handled by Godot (despawn) and `InterestService.ForgetPeer`.
- **Trap:** a filter input changed without a refresh is silently ignored until the next unrelated
  refresh. The peer keeps (or never gets) the node, or keeps a stream it should not get. Under
  `Idle` the same bug fixed itself one frame later, so old code may rely on that.

## Migrating old code / open branches
- `grep -n "AddVisibilityFilter\|VisibilityUpdateMode\|new MultiplayerSynchronizer" src`: each player
  synchronizer must say `VisibilityUpdateMode = MultiplayerSynchronizer.VisibilityUpdateModeEnum.None`.
- `grep -n "ServerSees\|RelaysNear\|RelaysFar\|GetMultiplayerAuthority()" src/Player/FootPlayer.cs src/Net/InterestService.cs`:
  every new input a filter reads needs a refresh call at the point where it changes (see Rule).
- A conflict in `FootPlayer.cs` around `MakeRelay`, the `Sync` initializer or `_vis = new ...`: keep
  both sides and keep the `VisibilityUpdateMode` line.
- Open PRs that touch `FootPlayer.cs` (#148, #169, #180, #197, #220, #223) or `RaceNpc.cs` (#229)
  add no visibility filter or synchronizer (checked at this PR). #188 (Battle Royale) feeds
  `InterestService.Together`, which `Evaluate` reads, so it is covered.

## How to check
- `<godot> --headless --path . -- --interestcheck` → `RESULT: ok` (the rules only).
- Loopback: a dedicated server and two clients, A at 2518038,1167321 and B 100 m east. Each logs
  `[net] player <other> came into view`. B leaves with `/city Riddes`: both log `left view`, and so
  does A's race NPC on B. The server console sends `/tpall Montricher`: both log `came into view`
  again. One way to script it: A with `--raceauto --racestart "1500 car" --racenpc 1`, B with
  `--chatafter "20 /city Riddes"`, and `/tpall` fed to the server's stdin.
- `tools/loadtest.sh 32`: per-frame busy p50 around 1.4 ms on Windows (it was 5.2 ms).
