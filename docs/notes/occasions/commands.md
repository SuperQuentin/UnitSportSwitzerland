# Commands

- Occasion calendar check: `<godot> --headless --path . -- --occasioncheck` — both ends of every
  window, the new-year wrap, one-offs, leap day, config merge; non-zero exit on a mismatch.
  Look at an occasion out of season with `--occasion christmas` (or `--date 2026-12-24`).
  `--huntcheck` (with an occasion running) claims a drawn hunt spot **in memory only** and checks
  it disappears, prints reward odds and a counter's seasonal loot; `--decorlog` prints each tile's
  props with a position and facing (and the creatures every 3 s) for aiming a `--shot`;
  `--avatars 2 out.png --hats [--close]` lines up every `Headwear` and the cycling helmet; `--soundcheck` also writes
  `occasion_*.wav`.
- Festivals (#1269): `--festival test` adds the check-only festival (a def and an occasion entry, 10-14 March, held at the fixture start; never
  saved to `occasions.json`); `--festivalcheck --festival test --date 2027-03-11 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure`
  (quick) and `tools/festivalnetcheck.sh` (net); `--festivalnet a|b|b2|a2` is the net probe's (`festival-framework`).
- UFO sightings (#863): `--occasion ufo --time 22` runs the night; `--sighting flyover|abduction|landing|mothership`
  plans one of that kind at once on the authority (day or night, occasion or not); the log's
  `[sightings]` and `[herds]` lines say what was planned, lifted and set down (`ufo-sightings`, `farming/cows`).
