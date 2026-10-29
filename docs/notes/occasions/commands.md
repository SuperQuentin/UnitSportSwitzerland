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
  `--fireworkscheck --festival test --date 2027-03-14 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure` (quick) plays the closing show (`festival-fireworks`); `tools/fireworksnetcheck.sh` (net) does it over the wire (`--festivalnet show`).
  `--fahnecheck --festival test --date 2027-08-16 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure` (quick) swings the Älplerfest's flag through the real game; `tools/fahnenetcheck.sh` (net) does it with two clients in step; `--fahnenet a|b` is the net probe's (`festival-fahnenschwingen`).
  `--desalpecheck --festival test --date 2027-09-24 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure` (quick) dresses the Désalpe's herd, drives it to the fair and looks at the ring through the real game; `tools/desalpenetcheck.sh` (net) does it with two clients; `--desalpenet a|b` is the net probe's (`festival-desalpe`).
  `--christmascheck --festival test --date 2027-11-30 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure` (quick) walks the Christmas market's stalls, buys and drinks the Glühwein mug, takes the Samichlaus's bag through the real game on a scratch folder; `tools/christmasnetcheck.sh` (net) does it with two clients; `--christmasnet a|b` is the net probe's (`festival-christmas`).
  `--escaladecheck --escaladefast --festival test --date 2027-12-11 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure` (quick) smashes the Escalade's Marmite, ladles the soupe and runs the Course through the real game on a scratch folder (`--escaladefast` lifts the sprint limit so a check may teleport between gates); `tools/escaladenetcheck.sh` (net) does it with two clients in a party; `--escaladenet a|b` is the net probe's (`festival-escalade`).
  `--sechscheck --sechsefast --festival test --date 2027-04-19 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure` (quick) takes the Sechseläuten's purse, bets on the Böögg, lights the pyre by the sky clock, bursts the head and shares the pool through the real game on a scratch folder (`--sechsefast` shortens a minute of the burn to 0.4 s); `tools/sechsenetcheck.sh` (net) does it with two clients and checks the burn time never reaches them before the bang (`--sechsenet a|b` is the net probe's; `--sechsepace S` and `--sechsedraw M` fix the pace and the drawn time on a check's server) (`festival-sechselaeuten`).
- UFO sightings (#863): `--occasion ufo --time 22` runs the night; `--sighting flyover|abduction|landing|mothership`
  plans one of that kind at once on the authority (day or night, occasion or not); the log's
  `[sightings]` and `[herds]` lines say what was planned, lifted and set down (`ufo-sightings`, `farming/cows`).
