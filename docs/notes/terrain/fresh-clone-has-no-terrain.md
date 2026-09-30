# A fresh clone has NO terrain

- **A fresh clone has NO terrain** — the generated data is gitignored — so a missing
  `manifest.json` is an ordinary state, not an error. `LocalChunkSource` returns an empty
  manifest and the client boots into the generated fallback world with a message; it used to
  throw `FileNotFoundException` out of `ClientWorld._Ready` and take the game down. A *server*
  still fails fast, because it is the authority on where the world is and has nothing to serve.
