# Build cancellation

- **Build cancellation** (`ChunkState.Cts`/`Generation`): every `source.Load*Async` gets the tile's
  token and the worker checks it between stages. A tile that leaves the desired set (beyond
  `MaxDist + UnloadSlack`) or whose *pending* stride is finer than what it now wants past
  `RoadMaxDist` is cancelled on the spot — the worker slot frees now instead of when the chain it was
  reading finishes, and `CommitReadyResults` drops any result whose generation is stale. Measured: 12
  in-flight Riddes builds cancelled within one evaluation of a 50 km jump (`SettleReport` prints
  `cancelled=`). Tiles **off screen** are queued later, never skipped: `view-cone-priority`.
