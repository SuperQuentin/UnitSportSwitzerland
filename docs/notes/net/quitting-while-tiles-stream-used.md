# Quitting while tiles stream used to crash the process

- **Quitting while tiles stream used to crash the process.** `ChunkStreamer.FetchAsync` runs on
  worker threads and defers onto the main one; the workers outlive the tree, and deferring onto a
  freed native object is a 0xC0000005, not a managed exception. `_shuttingDown` is set in
  `_ExitTree` so the workers stop queueing before Godot frees anything.
