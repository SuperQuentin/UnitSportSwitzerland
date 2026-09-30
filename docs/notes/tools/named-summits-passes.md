# Named summits and passes

- **Named summits and passes**: `tlm_namen_name_pkt` -> `places.json` alongside the GWR towns,
  so **Tab** finds mountains. `Place.Kind` and `Elevation` are what let search rank a peak among
  peaks by height and a town among towns by size — a mountain has no buildings, so without a
  per-kind `Rank` every summit sorted below the smallest hamlet. Names are multilingual and
  pipe-separated (`Nordend | Punta Nordend`); the first form is kept. Region: 329 towns, 1,243
  summits, 402 passes. **This layer is POINT geometry** — `GeoPackageReader.ParseLines` returns
  nothing for it and fails silently; use `ParsePoints`.
