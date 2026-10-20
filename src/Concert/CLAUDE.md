# Concerts (`src/Concert/`)

Random artist events in the stadiums (#885): the event generator, stages, the DMX rig, the light desk.

Index only: one line per note in `docs/notes/concert/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/concert`.

- `concerts` — Concerts (#885): a seeded event a day per stadium (artist, genre, layout end/side/thrust/round, screens, rig), `StagePlan`, real DMX layouts patched into universes, `LightDesk` writes / `ConcertRig` reads, made-up songs on the shared clock until the music drives it, `--concert N|off`, `--concertcheck` ; show control: Art-Net/sACN/OSC into the operator's game (`ShowInput`), `/concert host|input|patch`, DMX relayed by `ConcertService` (channel 4), `tools/concertnetcheck.sh` ; music (a CD setlist from the PA drives the desk), effects (flames, CO2, confetti, gerbs, airbursts, fog on DMX), the crowd (instanced, one shader), the light desk screen at FOH, `--concertdeskcheck` ; cameras: rigs (rails, wire cam, jib, drone...), IMAG, program, NDI out, a crew running them (`/concert camera take`), beams stopped by walls, `--concertcamcheck` ; phrases (sync, stagger, mirror, cycle...), rhythm pump, stepper motors, the PA bouncing on the kick, `/concert debug` beat window, performance (IMAG range, pose cache)
- `wristbands` — LED wristbands in the crowd (#986): IR emitter heads, IR floods and a transmitter on DMX (kinds, channels, how many per layout), the crowd shader's per-band cone test with a 0.5 s hold and sweep trail, the desk's looks (sweeps, stand chases, all on at the drop, ballad twinkle), what turns bands on
