# A loopback server test leaves its manifest in the client's chunk cache

- **A loopback server test leaves its manifest in the client's chunk cache.** `ClientTerrainSync`
  saves the server's index as `server-manifest.json`, and the next *offline* boot merges it,
  retires the generated world and loads a tile that does not exist — an empty world, `prims=4`.
  Give a test client its own `--cache <scratch dir>`, or delete that file afterwards.
