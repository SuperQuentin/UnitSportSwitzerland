# Stutter is a main-thread commit problem, and every commit is now cheap

- **Stutter is a main-thread commit problem, and every commit is now cheap** — measured with
  `--fly x,y,z,yaw,speed,seconds` (`FlightProbe`, prints p50/p95/p99/max frame time and counts
  frames over 20 and 33 ms, non-zero exit on any >33 ms). Baseline after the first async pass: 14
  frames over 33 ms in a 12 s flight at 150 m/s, worst 147 ms. Four causes, each found by logging
  commits over 15 ms: (1) **collision** — a 1001² `HeightMapShape3D` is ~80 ms to build, and it was
  built under the fly camera, which never touches the ground; collision now goes only to anchors
  that ask for it (`AddAnchor(node, collision:)`, default `node is PhysicsBody3D`; `FootPlayer`
  registers itself, `TunnelProbe` registers its point). It is also committed **once**, not interim
  then blended: from local disk the road tile is milliseconds behind, so the ground waits for it
  (`publishInterimCollision`); over the network it does not. (2) **Trees** — `SetInstanceTransform`
  + `SetInstanceColor` per tree was two native calls × 60k; the worker now packs the 16-float
  instance buffer (`ChunkNode.BuildTreeBuffers`) and builds the `MultiMesh` itself with a
  `CustomAabb`, since assigning a buffer without one makes the server walk every instance for the
  bounds on the main thread. (3) **ArrayMesh** creation moved to the worker for terrain, roads,
  buildings and water (`ChunkNode.ToArrayMesh`; `RenderingServer` is thread-safe) — the main thread
  only assigns the resource. (4) **Building collision** (`ConcavePolygonShape3D`, a BVH build, up to
  80 ms for a town tile) now rides with the terrain collision — only the tile a body stands on — not
  with every building mesh. Every result counts against the ms budget, not just surfaces. And the
  ring evaluation's scan + sort (6,561 tiles at 40 rings, previously every 0.1 s and after every
  commit — a 51 ms frame) is cached and redone only when an anchor's tile, the ring table, the view
  octant or the tile set changes. After: **0 frames over 20 ms** at 15 rings, max 12.7 ms; at 40
  rings max 22 ms; at 6 rings / 150 km horizon / 32 builds max 15.9 ms.
