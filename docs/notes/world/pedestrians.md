# Pedestrians generated where players look, kept by the server (#217)

## Rule

- **The server keeps the people; clients only draw them.** `World/Pedestrians` (at `World/Pedestrians` on
  the server and every client, like `BirdNet`) holds every pedestrian as a light `Ped` record: id, position,
  goal, yaw, seed (look and pace), leg number, state bits (`FlagKnocked`, `FlagDirty`). No physics, no
  animation, no node on the server. Offline the client is its own authority with the same code (its own
  view, the snapshot applied locally).
- **Where they walk**: the town birds' street spots (`TownPerches`, `PerchKind.Street`: 3–5 m out from a
  facade, never inside a building), read through `BirdLife.Town(p)` (one loader for birds and people). A
  leg goes to another spot 6–30 m on that keeps the way it was going (`PedestrianRules.PickNext`, replayable
  from seed + leg), never through a building box (`TownPerches.Blocked`). Towns only: the wanted count per
  player scales with `BuildingsAround` (0 below 10 real buildings in the 192 m square, full from 50).
- **Where they appear** (`PedestrianRules`, pure, tier-0 tested): never in plain sight. `SpawnOk` = the far
  band of the view (112–150 m, a few pixels), or the cone where the player will look in 1.5 s (its velocity
  and the camera's turn rate, which each client reports 4×/s through `ViewRpc`), or just past either edge
  for a still player. A first join or a teleport fills the view at once (no history to anticipate from).
- **Who is drawn, who is solid**: each peer's 4 Hz snapshot (unreliable, ~20 B a person, LV95 anchor + float
  offsets) carries the nearest in its cone (`FlagVisible`, at most `VisibleCap`) and everyone within 30 m
  of it or of where it will be in 1.5 s (`NeedsBody`), whatever the view. Every puppet gets a capsule
  (`AnimatableBody3D`, 0.3 × 1.75 m, layer `TreeColliders.Layer` so players and cars hit it and cameras
  ignore it); only visible ones get a mesh. A puppet not mentioned for 1.5 s is freed (no despawn message).
- **Persistence**: records keep walking while nobody looks; `Seen` is refreshed while in someone's cone; a
  record is forgotten only > 3 km from every player or after 20 min unseen. Turning away and back, or a
  second player looking at the same street, finds the same ids where their walk took them.
- **Bumps**: the local player running (> 2.5 m/s) into a puppet's capsule, drawn or not, sends `BumpRpc`;
  the server checks the sender is within 6 m and knocks the record over for 5 s (`FlagKnocked`: it lies
  down, stops walking) for everyone.
- **Meshes**: 12 looks × 8 stride frames + standing, built once and shared (`Walker`): a puppet swaps a mesh
  reference per frame, never builds one (`perf-pose-mesh-cache`). Puppets stand on the client's ground.
- **Budgets**: `VisibleCap` 40 drawn per client, `WantedPerPlayer` 28 around each player, `RecordCap` 1200
  on the server (16 × 28 = 448 wanted at once, the rest is memory of places left behind).

## Why

Issue #217: pedestrians to drop on, but only where people look, the same for everyone, solid even when
unseen. Server records cost a few floats; meshes and bodies only exist on the clients that need them.

## How to check

- `tools/test.sh unit` (`PedestrianRulesTests`: cone, spawn band, anticipation, bodies, forgetting, walk).
- `CHUNKS=<terrain_chunks> tools/pednetcheck.sh [E,N]` (Sion old town by default): no pop-in, B draws A's
  people, turning away and back, a body behind A, A knocks it over, B sees it.
- Server cost: `tools/loadtest.sh 16` at a town (numbers in the PR).
