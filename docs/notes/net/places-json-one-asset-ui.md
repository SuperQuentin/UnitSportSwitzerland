# `places.json` is the one asset the UI reads, not the streamer

- **`places.json` is the one asset the UI reads, not the streamer** — so it was silently left
  out of `AssetKind` and a streaming client connected fine, pulled terrain fine, and showed an
  empty Tab search. It is now `AssetKind.Places`, fetched during sync into the cache, and
  `PlaceSearchUi.ReloadIndex()` re-reads it (the UI is built long before the connection).
