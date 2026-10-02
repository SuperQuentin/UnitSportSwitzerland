# No whole-tree walk per frame (#221)

## Rule
- Never call `GetTree().Root.FindChildren(...)` (or `FindChild`, `GetChildren()` recursion over the
  root) in `_Process` / `_PhysicsProcess`. The tree holds every terrain tile's nodes: ~6.6k tiles
  and tens of thousands of nodes at 40 rings.
- Walk once, keep the list, follow `GetTree().NodeAdded` for nodes made later (prune freed ones
  with `IsInstanceValid`), unsubscribe in `_ExitTree`. Pattern: `ShotRunner.HideHud`.
- A one-off walk (a probe's setup, a key press, a settings change) is fine.

## Why
`ShotRunner --nohud` hid the CanvasLayers with a whole-tree `FindChildren` every frame. dotnet-trace
at 40 rings: 91 % of the main thread inside that call. It was the "~4.5 ms per frame that grows with
the tile count, independent of draws" every `--shot-queue --nohud` perf measurement saw; the game
itself never paid it. Medians, Sion, windowed, vsync off, real chunks (PR #<this PR>):

| rings / view | frame before | frame after | process_ms before / after |
|---|---|---|---|
| 15 ground | 3.33 ms | 1.73 ms | 4.40 / 2.67 |
| 15 sky    | 2.38 ms | 0.84 ms | 3.87 / 2.16 |
| 40 ground | 9.72 ms | 2.38 ms | 11.27 / 3.61 |
| 40 sky    | 7.41 ms | 0.90 ms | 9.07 / 2.35 |

GPU and render-CPU are unchanged. Perf numbers from shot runs before this PR overstate the CPU cost
of many rings; re-measure before acting on them.

## Same logic, preserved
- Every CanvasLayer (subclasses too, as `FindChildren`'s type filter matched) is still forced
  hidden every frame, so a layer that shows itself again, or one added later (start menu, toast),
  stays out of the picture.

## Migrating old code / open branches
- `grep -rn "FindChildren\|FindChild(" src` inside `_Process`/`_PhysicsProcess` or anything they
  call every frame: cache the result as above.
- A branch touching `ShotRunner._Process`: keep `if (_hideHud) HideHud();`, never the old loop.

## How to check
`--shot-queue <file> --nohud --perflog N --rings 40`, look at the sky: `frame_ms` median about 1 ms
(RTX 4070 Ti), not 7. For a new suspect, `dotnet-trace collect -p <pid> --profile
dotnet-sampled-thread-time --format Speedscope` and look for native calls under one `_Process`.
