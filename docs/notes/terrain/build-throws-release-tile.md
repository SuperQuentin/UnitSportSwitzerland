# A build that throws must release its tile

- **A build that throws must release its tile.** `PendingStride` is only cleared on commit, so
  an exception or a missing file left the tile pending for ever and it was never retried or
  drawn — one failed worker permanently deleted that piece of the world. Failures now go on
  `_failedBuilds` for the main thread to reset. (Found because a bad preview stride threw on
  every tile and the whole map stayed empty.)
