# Trailer storyboard (#706)

A private showcase trailer, 2:07, staged and filmed inside the game by the trailer director
(`--trailer`, `src/Trailer/`). Every shot below is a row of `TrailerScript`; this file is the
creative side of it: the story, the cut and why each shot is there.

## The song

**"Voxel Revolution", Kevin MacLeod (incompetech.com)**, licensed under
[Creative Commons: By Attribution 4.0](https://creativecommons.org/licenses/by/4.0/): free to use,
credit required (it is on the end card). Download:
`https://incompetech.com/music/royalty-free/mp3-royaltyfree/Voxel%20Revolution.mp3`
(`tools/trailer.sh` fetches it into `test_output/trailer/`; the file is not committed).

Why this one: a bright, driving chiptune-flavoured electro track for a low-poly game with a PS1
soul, 2:10 long (a trailer's length without edits), and it already has a trailer's shape.
Measured (spectral-flux tempo, RMS and band share per bar):

- **123 BPM**, one beat 0.488 s, one bar 1.951 s, the first downbeat at 0.186 s.
  Bar *k* starts at `0.186 + (k − 1) × 1.9512` s. Every cut lands on a bar line.
- **Intro**, bars 1-7 (0:00-0:13.8): thin, no kick for 3 bars, the kick enters at bar 4 (0:06).
- **Lift**, bar 8 (0:13.8): a fill, then **groove A**, bars 9-24 (0:15.8-0:47.0).
- **Section B**, bars 25-30 (0:47.0-0:58.7): the lead changes, more melody.
- **Breakdown**, bars 31-40 (0:58.7-1:18.2): quieter, the dip at bars 34-35, a riser in bar 40.
- **Drop**, bar 41 (1:18.2): full energy to bar 57 (1:51.4).
- **Outro**, bars 58-63 (1:51.4-2:03): the kick and bass fall away, only the melody.
- **Tail**, bars 64-65 (2:03-2:07): fade out.

## The story

*One country, the real one. Every way across it. Together.*

Five acts follow the song's sections: the country wakes up (intro), everything that moves crosses
it in daylight (groove A), it takes to the air (section B), the sun goes down and the players come
together (breakdown), they drop in and everything happens at once (drop), and they end on a summit
at sunset, the whole of Switzerland below them (outro).

The camera grammar follows the acts: slow drone moves and long lenses in the intro, a moving camera
that stays with each machine in act II, wide floating moves for the flying, close and warm at night,
one bar per shot after the drop, then one long pull-out.

Captions are white, centred low, on the beat; never more than five words.

## Shot list

Times are song time. Lens in mm (full-frame equivalent; the director converts to a vertical FOV).

### Act I: the country (intro, bars 1-7)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 1 | 1-3 | 0:00.0-0:06.0 | **Alpenglow.** Sunrise on the Matterhorn, seen from the air above the Gornergrat; fade in from black. *"The whole of Switzerland."* | Drone push-in toward the peak, rising slowly, 50 mm | swissALTI3D relief, sunrise sky |
| 2 | 4-5 | 0:06.0-0:09.9 | **The lake.** The CGN paddle steamer below the Lavaux terraces, Lake Geneva and the French Alps behind. *"Built from real survey data."* | High on the slope, slow truck along the shore, 35 mm | Real buildings, vineyards, lakes, the steamer |
| 3 | 6-7 | 0:09.9-0:13.8 | **Village morning.** Lauterbrunnen's main street under the cliff; a road cyclist rides at the camera and past. *"Every road. Every house."* | Low at the kerb, pans with the rider, 28 mm | Villages, streets, the road bike |

### Act II: every way to move (groove A, bars 8-24)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 4 | 8 | 0:13.8-0:15.8 | **Pigeon.** On the fill: a pigeon dives down a Bern old-town street. *"Go anywhere."* | Chase, just behind and above the bird, 24 mm | Play as a pigeon |
| 5 | 9-10 | 0:15.8-0:19.7 | **Tremola.** An AE86 slides through a hairpin of the old Gotthard road. | Low on the outside of the hairpin, pans with the car, 85 mm | Cars, drifting, the autopilot |
| 6 | 11-12 | 0:19.7-0:23.6 | **Car to car.** An FD3S hunts it down the next straight. | Tracking alongside at door height, 35 mm | Racing, slipstream |
| 7 | 13 | 0:23.6-0:25.6 | **Furka.** A motorbike leans through a bend of the Furka pass. | Low at the apex, 50 mm | Motorbikes |
| 8 | 14 | 0:25.6-0:27.5 | **Glacier run.** A skier carves down a snow slope under the Matterhorn. | Chase, low behind, 28 mm | Skis |
| 9 | 15-16 | 0:27.5-0:31.4 | **Heavy.** A truck with its trailer and a postbus climb a mountain road, traffic around them. | High on the hillside, slow pan, 70 mm | Trucks, buses, traffic |
| 10 | 17-18 | 0:31.4-0:35.3 | **Work site.** An excavator, a wheel loader and a roller at work by a tower crane. | Rising crane move, 35 mm | Building sites, works machinery |
| 11 | 19-20 | 0:35.3-0:39.2 | **Chillon.** A jetski and a speedboat carve past the Château de Chillon. | At water level, the jetski crosses the frame, 35 mm | Boats, waves, real lake depths |
| 12 | 21-22 | 0:39.2-0:43.1 | **Take-off.** An A320 climbs out over the camera at Geneva. | On the ground past the runway end, tilting up, 28 mm | Airliners |
| 13 | 23-24 | 0:43.1-0:47.0 | **Valley.** A helicopter flies up the Lauterbrunnen valley past the cliffs. | Chase from behind and to the right, 35 mm | Helicopter |

### Act II b: the air (section B, bars 25-30)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 14 | 25-26 | 0:47.0-0:50.9 | **Interlaken.** A paraglider between the two lakes. *"Fly."* | Slow orbit round the canopy, 35 mm | Paraglider |
| 15 | 27-28 | 0:50.9-0:54.8 | **Wingsuit.** A wingsuit flyer skims the Lauterbrunnen cliffs. | Close chase, 24 mm | Wingsuit |
| 16 | 29-30 | 0:54.8-0:58.7 | **Aletsch.** A light plane crosses the great Aletsch glacier, the Jungfrau behind. | Wide drone, the plane crossing the frame, 50 mm | Plane |

### Act III: day and night (breakdown, bars 31-40)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 17 | 31-32 | 0:58.7-1:02.6 | **Sunset.** Lausanne and the lake from above Lutry; time-lapse, the sun goes down and the town lights up. *"Day and night."* | Locked off, very slow push, 35 mm | Day/night, lit windows |
| 18 | 33-34 | 1:02.6-1:06.5 | **Night town.** A car rolls through a lit old-town street. | Low, pans with the headlights, 35 mm | Night lighting, headlights |
| 19 | 35-36 | 1:06.5-1:10.4 | **Party.** Friends dance by the lake at night (YMCA, the cabbage patch, the chicken). *"Together."* | Slow orbit at chest height, 28 mm | Emotes, dances, multiplayer |
| 20 | 37 | 1:10.4-1:12.4 | **PS1.** The Lauterbrunnen valley from the air. *"PS1"* | One continuous drone move across shots 20-22, 35 mm | Visual styles |
| 21 | 38 | 1:12.4-1:14.3 | **Cartoon.** The same move goes on, Cartoon style. *"Cartoon"* | (continues) | |
| 22 | 39 | 1:14.3-1:16.3 | **Realistic.** The same move goes on, Realistic style. *"Realistic"* | (continues) | |
| 23 | 40 | 1:16.3-1:18.2 | **The drop plane.** The military freighter over the Alps, its ramp open, on the riser. *"Drop in."* | Formation flight beside it, slow push to the ramp, 35 mm | Battle Royale plane |

### Act IV: everything at once (drop, bars 41-57)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 24 | 41 | 1:18.2-1:20.2 | **Jump.** On the drop: the squad leaves the plane. | Behind the freighter, looking back as they fall away, 24 mm | Battle Royale drop |
| 25 | 42 | 1:20.2-1:22.1 | **Dive.** Wingsuits dive past the camera toward a valley. | Static in the air, they streak by, 24 mm | Wingsuit |
| 26 | 43 | 1:22.1-1:24.1 | **Crash.** A car hits a wall; the driver goes through the windscreen. | Low, side on, 35 mm | Crash ragdoll |
| 27 | 44 | 1:24.1-1:26.0 | **Grid.** Six cars leave a race grid. | Low in front of the grid, 24 mm | Races, NPC drivers |
| 28 | 45-46 | 1:26.0-1:29.9 | **Pack.** The pack through the hairpins. | Top-down drone tracking the leader, 35 mm | Racecraft |
| 29 | 47 | 1:29.9-1:31.9 | **Lean.** A motorbike chases a car through a bend. | Tracking, low, 50 mm | Motorbikes |
| 30 | 48 | 1:31.9-1:33.8 | **Air.** A jetski leaps a wave. | Water level, 24 mm | Boats, sea state |
| 31 | 49-50 | 1:33.8-1:37.8 | **Heavy metal.** The AN-124 low over the lake at sunset. | Under the path, tilting up as it passes, 24 mm | AN-124 |
| 32 | 51-52 | 1:37.8-1:41.7 | **Swim.** A player runs off a pier and swims. | Low on the pier, 28 mm | Swimming |
| 33 | 53-54 | 1:41.7-1:45.6 | **Downhill.** A road cyclist flat out down the Tremola. | Chase, 35 mm | Road bike |
| 34 | 55-56 | 1:45.6-1:49.5 | **Gridlock.** A city crossroads at dusk: traffic, a tram, a bus. | High, slow pan, 70 mm | City traffic |
| 35 | 57 | 1:49.5-1:51.4 | **Flag.** A player raises the Swiss flag on a summit. | Low hero angle, 24 mm | The Swiss flag |

### Act V: together (outro, bars 58-65)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 36 | 58-61 | 1:51.4-1:59.2 | **Summit.** The whole crew on the Gornergrat at sunset, the Matterhorn beyond. Title on bar 59: **UNITSPORT SWITZERLAND** | Slow orbit pulling out to reveal the Alps, 28 mm | Multiplayer |
| 37 | 62-65 | 1:59.2-2:07.0 | **End card.** The title stays: *"On foot · on wheels · on water · in the air"*, then the music credit; fade to black. | The pull-out drifts on | |

Music credit on the end card: *"Voxel Revolution" Kevin MacLeod (incompetech.com),
Licensed under Creative Commons: By Attribution 4.0*.

## Making it

- Preview one shot in a window: `tools/trailer.sh preview 5` (or a range, `5-9`).
- Render everything and cut it on the song: `tools/trailer.sh render` → `test_output/trailer/trailer.mp4`.
- Stills of a shot's start, middle and end to frame it: `tools/trailer.sh stills 5`.
- How the director works: `docs/notes/trailer/director.md`.
