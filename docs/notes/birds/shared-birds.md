# Shared birds (#143)

- **The dedicated server owns the birds** (`BirdLife` with `Headless = true` in `ServerWorld`, wire in
  `BirdNet` at `World/BirdNet` on both sides). It spawns around every player in turn (≤ 32 within
  240 m of each, ≤ 256 in all), steps them, flushes them (nearest player inside the flush distance,
  shots) and kills them. A client's birds are **puppets** (`Bird.Remote`) built from snapshots; any
  bird of its own (spawned before it was connected) is dropped once online. Offline, `BirdLife` is its
  own authority exactly as before (`Authority`).
- **Snapshots**: 8 Hz, unreliable, birds within 270 m of the peer's body, 35 B each (id, species
  index, state with a town bit, position, yaw, velocity) in packets of ≤ 32. Flying and falling birds
  every time, walking and swimming ones every 4th, perched and dead ones every 8th (1 Hz). No despawn
  message: a puppet not mentioned for 4.5 s is gone. Town birds, landing, droppings: `town-birds`.
  The species index is the `BirdCatalog` position, so both sides must run the same catalogue.
- **The lean server has no cover raster and a coarse 10 m ground** (`lean-dedicated-server`): the
  birds load the cover of a tile themselves (`CoverAt`, `LoadCoverAsync`) and its `.trees` for perches,
  and forget both when no player is within 2 km. Puppets on the ground, on water or dead stand on
  the CLIENT's ground, and a falling one stops there; perched ones keep the server's height (tree tops
  are absolute, the same data on both sides). Server birds have no meshes (`Bird(visual: false)`).
- **Reports, not kills**: a client's shotgun picks the bird (it has the walls) and sends
  `Report(Shot, id, eye, dir)`; a gun round or a strike sends `Report(Kill, id, point)`, an aircraft
  dodge `Report(Flush)`. The server checks the sender's body is within 8 m of a shot's eye (300 m for
  the rest) and that the bird is alive and inside the pattern give or take 2.5 m (it moved); then it
  flushes everything within 150 m of a shot and kills the bird. `Killed` (reliable) goes to every peer
  within 400 m and always to the shooter, whose journal alone scores it (`RemoteKilled` → `Bag`).
  A bird a client reported is skipped for 0.8 s (`Bird.Reported`) so one shot is not counted twice.
  Strike damage to the craft stays the pilot's client's decision, as before.
- **The shooting bug**: the wall test cast to the body's centre and called any hit more than 0.5 m
  short of it a wall. A perched bird sits INSIDE its tree's trunk cylinder (`TreeColliders`: the
  trunk is the whole tree height, the perch 0.92 of it), so it was always "behind a wall" once trunks
  became solid; and a bird on the ground was hidden by the ground at grazing angles. Now the ray goes
  to 0.25 m above the bird, a hit within 1 m of the end is not a wall, and the perch's own trunk is
  skipped. `--birdcheck` at Riddes: 30/30 perched tits hit (the old test lost 25), 31/31 ground sparrows.
- **Cost**, 16 players at Riddes (`tools/loadtest.sh 16`): server busy p99 199 ms (195 without
  birds; 460 before the server birds lost their meshes and the tree tops were bucketed in 16 m cells),
  net out +35 KB/s for 16 peers. Swarm bots carry a `BirdNet` with no `BirdLife` to take the RPCs.
- **Check**: `tools/birdnetcheck.sh [E,N]` (server `--generated-world`; `SERVER_ARGS= UNITSPORT_CHUNKS=<dir>`
  for real terrain): two clients get the same birds, A shoots one through the item path (killed for
  both, in A's journal only), A's air shot flushes resting birds on B's screen, an aircraft round
  through a bird kills it. Kills are logged `[birds] killed #id`, client shots `[birds] shot: ...`.
