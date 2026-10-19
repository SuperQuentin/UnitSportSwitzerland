# Concerts (`src/Concert/`)

Random artist events in the stadiums (#885): the event generator, stages, the DMX rig, the light desk.

Index only: one line per note in `docs/notes/concert/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/concert`.

- `concerts` — Concerts (#885): a seeded event a day per stadium (artist, genre, layout end/side/thrust/round, screens, rig), `StagePlan`, real DMX layouts patched into universes, `LightDesk` writes / `ConcertRig` reads, made-up songs on the shared clock until the music drives it, `--concert N|off`, `--concertcheck`
