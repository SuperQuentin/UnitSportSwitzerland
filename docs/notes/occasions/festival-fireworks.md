# Fireworks and the closing show (#1269 part 2)

`src/Occasions/Festivals/` (`FireworkShow.cs`, `FireworkSounds.cs`, `Fireworks.cs`, the start in `FestivalService.cs`); the plan is
`docs/plans/adventure-festivals.md` section 2. A festival whose `FestivalDef.Show` is `Fireworks` or `Both` ends on a show on its **last real day**.
`Concert` is not built yet (see the end); `Bonfires` belongs to the 1 August part.

## What is pure (tier 0, `FireworkShowTests`)

- **`FireworkShow.Plan(seed)`**: a script of `Shell(T, East, North, Height, Kind, Colour)` from a seed alone, 6 to 12 minutes (`DurationOf(seed)`,
  whole minutes). Three acts (35 % single shells every 4-7 s, 40 % pairs and triples of willows, palms, rings every 3-5.5 s ending on a Swiss cross,
  the rest salvos of 2-4 every 2-4 s with crossettes) and a **finale of 40 shells in the last 20 s** (a Swiss cross in the middle, willows last). Kinds:
  peony, chrysanthemum, willow, palm, ring, crossette, Swiss cross. Metres east/north of the launch point, in a 280 m wide fan 60-180 m out in
  the seed's heading; heights 80-170 m by kind. A shell bursts at `T` and left the mortar `RiseSeconds` (2.2) before (`Launch`). `First(t)` is the
  binary-search cursor.
- **`FestivalShows`**: `Starts(lastHour, hour)` (the sky crossed **21:30**, or the first look found the evening under way; never after midnight),
  `SeedOf(instance, n)` (FNV-1a: `string.GetHashCode` changes between runs), `Active(start, seed, now)`, `SoundDelay(distance)` = distance / 343,
  the watch rule (1.5 km, 20 s), the draw range (15 km).

## The server decides, every peer plays the same show

No new RPC (protocol stays 60): `RunningFestival` gained `ShowStart` (server time, `ClockSync.SimNow`) and `ShowSeed`, which travel in the
running list `TellFestivals` already sends. In `FestivalService.Evaluate` (every 5 s) `ShowOf`: on the festival's **last day** (`Day >= Length`), when
`Hour()` (the sky clock, `WorldClock.CurrentHour`; a check overrides it) crosses 21:30 and no show is on the books, it fixes `ShowStart = now + 15 s`
and `ShowSeed = SeedOf(instance, n)`; **every game evening of that day gets a show** (a game day is ~22 real minutes; a show is 6-12). A site that is not
held in the region has no show. The show stays on the books for its length + 15 s, then `ShowStart` is 0 again. In memory only: a restarted server
may show again that evening.

**Watching**: `FestivalService.CheckShow` (every 2 s) counts, for each adventuring peer within 1.5 km of the site while the show plays, and after 20 s raises
`FestivalShowWatched(peer, key, festival)` once per player and instance: the errands plan's world event (the plan's `FestivalShowStep` is not built; it
reads this). Nothing is asked of the client.

## The client: `Fireworks` (a child of the client world, `World/Fireworks`)

- Every 0.5 s it looks in `FestivalService.Running` for a `ShowStart` it has not played, plans `FireworkShow.Plan(seed)` and walks two cursors
  over the script from `ClockSync.SimNow`: one for **launches** (`T - 2.2 s`), one for **bursts**. A peer that joins at 200 s starts the cursor at
  200 s (`First(t - 0.5)`): shells already gone are not played in a pile. A show is played to `Duration + 6 s`, then never again.
- **Pools, made once** (`_Ready`), nothing built per frame or per shell: 32 one-shot `GpuParticles3D` bursts of 150 stars, 12 climbing sparks
  (48 each), 4 `OmniLight3D` flashes (decay over 0.5 s, only within 6 km), 10 `AudioStreamPlayer3D` voices. That is 5 376 live particles at most (the plan
  allows 6 000: `Fireworks.ParticleBudget`). A shell costs a material assignment, a transform and `Restart()`; the quad's size is set once per burst
  by distance (a shell 3 km off is still a visible spark). Process materials are cached per look and colour (`Material(look, colour)`).
- **Looks**: peony (damped sphere), chrysanthemum (longer), willow (slow, long golden droop), palm (few big heavy arms), ring (the ring emission
  shape, tilted at random), crossette (a short burst and four small peonies 0.9 s later along the fan and up and down), **Swiss cross** (a red peony
  and a white cross of stars: the `Points` emission shape from a baked texture, turned to face the camera).
- **Sound** (`FireworkSounds`, static banks, so the sound player lists them: *firework bang* x4, *launch* x3, *crackle* x3): a bang is queued at
  `t + distance / 343` (a shell 2 km off, 6 s late), a launch hiss with the climb, a crackle after willows, chrysanthemums and crossettes. A sound
  more than a second late (a seek, a stalled frame) is dropped.
- **Off switches**: the occasion's **Decorations** facet hides the shells and flashes, its **Audio** facet the bangs (a player's "Off" is both); the
  server's watch count and the score do not depend on them (the `Games` facet).
- **The rack**: `FestivalOccasion.MortarRack` (two rows of seven tubes, a fuse box) stands at the site's middle when the festival has fireworks; in the
  model viewer under Occasions › Prop. The shells fan out ahead of the seed's heading.
- Origin shifts: the node is an `IOriginContainer`; every shell is placed from LV95 at the moment it fires. Ground height comes from the chunk manager
  at the site (kept), else the camera's height: a show far off is drawn a few tens of metres out and nobody can tell.

## How to check

- Tier 0: `FireworkShowTests` (determinism, 6-12 min, the finale, order and box, every kind, the cross over the fan, the cursor, `Starts` over a wrapped
  clock, seeds, `Active`, the sound delay).
- Quick: `--fireworkscheck --festival test --date 2027-03-14 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure`
  (`FireworksCheck`): no show at noon; the sky crossing 21:30 starts exactly one; the same seed on the client; the first shell's climb, flash and sounds; a
  late join at 200 s fires at most 3 shells; the 40-shell finale fits the pools; the child count never changes; the player at the site has watched; the
  node falls silent at the end and for an ended festival. `Fireworks.Seek(t)` jumps through the show (checks only). Windowed it saves
  `test_output/fireworks_finale.png`.
- Not in a check: how it looks and sounds in each style (Cartoon, PS1, Realistic) and in VR: `tests/TO-TEST-BY-HUMAN.md`.

## Not built (still in the plan)

- **`Concert` / `Both` with a concert**: `ConcertPlanner` taking a festival seed and an open-air `StadiumSpec`, the fireworks in the blackout after the
  last song. `Both` plays the fireworks alone for now. Own part, since it touches the concert planner.
- **`FestivalShowStep`** as a quest step kind (the event `FestivalShowWatched` is the seam), the Journal line "the show tonight" beyond the existing
  "closing show: fireworks".
- **A real launch line** (a lake's shore or a barge) instead of the festival site's middle; the 1 August lake fireworks and a player's own fireworks
  (later parts, they call `FireworkShow` with their own seed and start).
- **Input, bots**: nothing to press. A show is watched, never acted on: no key, pad or VR way; a bot has no sense for it and earns nothing at festivals.
