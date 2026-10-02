# A tile worker must not create a Godot object after the engine starts tearing down

- **A tile worker must not create a Godot object after the engine starts tearing down.** Workers
  make `ArrayMesh`/`MultiMesh` themselves, and one that did so during quit was `Fatal error.
  0xC0000005` in `ArrayMesh..ctor` — the process died on exit. It only showed once something was
  always building at quit, which a generated world is (3 of 3 fly probes crashed).
  `ChunkManager._ExitTree` cancels every build and waits up to 3 s for `_buildsInFlight` to reach
  0, and every worker checks its token right before each Godot call; either alone leaves a race.
  Related: `ClientTerrainSync` continues on the thread pool, so anything it raises that touches UI
  or nodes must be marshalled (`Status` is deferred; the merge runs via `OnMainThread`).
