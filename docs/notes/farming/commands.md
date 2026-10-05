# Farming commands and checks (#494)

- `--farmmonth N` (1..12): the month the untouched fields show (server and offline; a client takes
  the server's). Falls back to `--birdmonth N`, then today.
- `--farmdir <dir>`: the authority's cell store instead of `user://farm` (checks); `--farmfresh`
  with it: start from untouched fields (deletes that directory).
- `--farmstats`: a `[farm] draw:` line every 10 s (chunks, vertices, builds, main and worker ms).
- `--farmdraw`: draw the fields in a headless run too (`--farmcheck` implies it).
- `--farmcheck [shots]` (quick, `tools/lib/checkmap.txt`): `--systems ui,physics,loot,farming
  --farmmonth 7 --farmdir test_output/farmcheck_store --farmfresh` on the flat fixture: every wheat
  cell ripe, the potatoes growing; the readout; Gathering offers the crop; hand harvest -> stubble ->
  hoe -> seed -> fertiliser; fast-forward (`FarmField.ClockSkew`) half way, then ripe, harvest again;
  machine strokes (combine, plough, seed drill, mower) with their units; grass not harvested, grain
  not mown; the save file; chunks drawn. `shots` (windowed; add `sky` to the systems, `--time 11`,
  `--style ps1|cartoon`): `test_output/farmcheck_<style>.png` from a camera over the worked strips.
- `--farmperf` (windowed, real map, `--at E,N` in farmland, e.g. 2592500,1182500): the farm's
  frame cost turning round, then moving 400 m; `test_output/farmperf_<style>_m<month>.png` over the
  nearest arable field.
- `tools/farmnetcheck.sh` (net): see `field-state-net`.
- Unit tests: `FarmTests` (`tools/test.sh unit`): tables, a cell's year, grass vs grain, the natural
  state, hand yield, the packed store, chunk versions, the strip, the raster.
