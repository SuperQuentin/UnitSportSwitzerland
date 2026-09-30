# `get_image()` on the root viewport from `_Process` returns whatever the render thread last left there

- **`get_image()` on the root viewport from `_Process` returns whatever the render thread last
  left there.** `ShotRunner` gets away with it because the scene has been static for seconds by
  the time it grabs. A per-frame exporter does not: measured 75 identical frames of empty sky,
  with `RenderTotalPrimitivesInFrame` reading 0 at the moment of capture while a `--shot` from the
  same camera position drew 5.2 M. Await `RenderingServer.FramePostDraw` first.
