# Trailer storyboard (#706)

A private showcase trailer, 2:07, staged and filmed inside the game by the trailer director
(`--trailer`, `src/Trailer/`). Every shot below is a row of `TrailerScript` (36 shots); this file
is the creative side of it: the story, the cut and why each shot is there. Places were read off
overhead stills (`--trailer-scout`), timings off the actor logs (`--trailer-log`).

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
| 2 | 4-5 | 0:06.0-0:09.9 | **The lake.** The CGN paddle steamer under way below the Lavaux vineyards (Epesses), Lake Geneva and the French Alps behind. *"Built from real survey data."* | High on the vineyard slope, slow truck along the shore, 35 mm | Real buildings, vineyards, lakes, the steamer |
| 3 | 6-7 | 0:09.9-0:13.8 | **Village morning.** Lauterbrunnen's main street under the cliff; a road cyclist rides at the camera and past. *"Every road. Every house."* | Low at the kerb, pans with the rider, 28 mm | Villages, streets, the road bike |

### Act II: every way to move (groove A, bars 8-24)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 4 | 8 | 0:13.8-0:15.8 | **Pigeon.** On the fill: a pigeon takes off and swoops down Bern's Kramgasse toward the Zytglogge. *"Go anywhere."* | Chase, just behind and above the bird, 24 mm | Play as a pigeon |
| 5 | 9-10 | 0:15.8-0:19.7 | **Tremola.** An AE86 comes down the old Gotthard road and round a hairpin. | On the outside of the hairpin, pans with the car, 50 mm | Cars, the race autopilot |
| 6 | 11-12 | 0:19.7-0:23.6 | **Car to car.** An FD3S side by side with it down the next leg. | Tracking alongside at door height, 35 mm | Racing, racecraft |
| 7 | 13 | 0:23.6-0:25.6 | **Furka.** A Honda Africa Twin leans through a bend of the Furka pass road. | Low on the outside of the bend, 50 mm | Motorbikes |
| 8 | 14 | 0:25.6-0:27.5 | **Glacier run.** A skier carves down a 37 % slope below the Theodul glacier, the Matterhorn to one side. | Chase, behind and to the right, 28 mm | Skis |
| 9 | 15-16 | 0:27.5-0:31.4 | **Heavy.** A Scania with its curtainsider climbs the Gotthard pass road to a hairpin, traffic around it. | High inside the hairpin, pans with it, 50 mm | Trucks, traffic |
| 10 | 17-18 | 0:31.4-0:35.3 | **Work site.** Three real building sites above Pully (swissBUILDINGS3D "Im Bau"): shells, tower cranes, an excavator, a wheel loader and a roller. | Rising crane move, 35 mm | Building sites, works machinery |
| 11 | 19-20 | 0:35.3-0:39.2 | **Chillon.** A jetski and a speedboat cross in front of the Château de Chillon, the motorway viaduct above. | At water level, locked on the castle, 35 mm | Boats, waves, real lake depths |
| 12 | 21-22 | 0:39.2-0:43.1 | **Take-off.** An A320 climbs out of Geneva's runway 23 straight over the camera. | On the extended centreline past the runway end, tilting up, 28 mm | Airliners |
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
| 18 | 33-34 | 1:02.6-1:06.5 | **Night town.** A car rolls down Bern's Kramgasse at night, every window lit. | Low on the pavement, pans with the headlights, 35 mm | Night lighting, headlights |
| 19 | 35-36 | 1:06.5-1:10.4 | **Party.** Friends dance in the park by the lake at Nyon at dusk (YMCA, the cabbage patch, the chicken, the Macarena, the griddy, Gangnam style). *"Together."* | Slow orbit at chest height, 28 mm | Emotes, dances, multiplayer |
| 20 | 37 | 1:10.4-1:12.4 | **PS1.** The Lauterbrunnen valley from the air. *"PS1"* | One continuous drone move across shots 20-22, 35 mm | Visual styles |
| 21 | 38 | 1:12.4-1:14.3 | **Cartoon.** The same move goes on, Cartoon style. *"Cartoon"* | (continues) | |
| 22 | 39 | 1:14.3-1:16.3 | **Realistic.** The same move goes on, Realistic style. *"Realistic"* | (continues) | |
| 23 | 40 | 1:16.3-1:18.2 | **The drop plane.** The military freighter over the Bernese Alps at golden hour, its ramp open, on the riser. *"Drop in."* | Formation flight beside it, 35 mm | Battle Royale plane |

### Act IV: everything at once (drop, bars 41-57)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 24 | 41 | 1:18.2-1:20.2 | **Jump.** On the drop: the squad leaves the ramp in wingsuits. | On the open ramp, looking back as they fall away, 35 mm | Battle Royale drop |
| 25 | 42 | 1:20.2-1:22.1 | **Dive.** Three wingsuits streak past the camera over the Lauterbrunnen valley. | Static in the air, whip-panning with them, 24 mm | Wingsuit |
| 26 | 43 | 1:22.1-1:24.1 | **Crash.** A rally saloon at 85 km/h into a wall on the Riddes straight; the driver goes through the windscreen. | Across the road before the wall, 35 mm | Crash ragdoll |
| 27 | 44 | 1:24.1-1:26.0 | **Grid.** Six cars leave a race grid on the Furka. | Low beside the front row, 24 mm | Races, NPC drivers |
| 28 | 45-46 | 1:26.0-1:29.9 | **Pack.** The pack through a bend over the young Rhône. | Straight down from 80 m, 35 mm | Racecraft |
| 29 | 47 | 1:29.9-1:31.9 | **Lean.** A Ducati Monster chases a Skyline through the Furka sweepers. | Tracking, low beside the bike, 50 mm | Motorbikes |
| 30 | 48 | 1:31.9-1:33.8 | **Air.** A jetski through a storm sea off Chillon. | Ahead of it at water level, 24 mm | Boats, sea state |
| 31 | 49-50 | 1:33.8-1:37.8 | **Heavy metal.** The AN-124 low along the shore off Nyon at sunset. | From the jetty, panning as it passes, 35 mm | AN-124 |
| 32 | 51-52 | 1:37.8-1:41.7 | **Swim.** A player runs off the grass into the lake at Nyon and swims. | Behind her, 28 mm | Swimming |
| 33 | 53-54 | 1:41.7-1:45.6 | **Downhill.** A road cyclist down the Tremola's hairpins. | Chase, 35 mm | Road bike |
| 34 | 55-56 | 1:45.6-1:49.5 | **Gridlock.** Zürich at dusk from above Bellevue: lit blocks, traffic. | High, slow pan, 50 mm | City traffic |
| 35 | 57 | 1:49.5-1:51.4 | **Flag.** A player cheers with the Swiss flag on the Gornergrat, the Matterhorn behind. | Low hero angle, 24 mm | The Swiss flag |

### Act V: together (outro, bars 58-65)

| # | Bars | Time | Shot | Camera | Feature |
|---|---|---|---|---|---|
| 36 | 58-65 | 1:51.4-2:07.0 | **Summit and end card.** The whole crew dancing by the Gornergrat Kulmhotel at sunset; the camera pulls out and turns to the Matterhorn. Title on bar 59: **UNITSPORT SWITZERLAND** / *"On foot · on wheels · on water · in the air"*; the music credit from 2:01; fade to black. | One long pull-out over 15.6 s, rising to 90 m, 28 mm | Multiplayer |

Music credit on the end card: *"Voxel Revolution" Kevin MacLeod (incompetech.com),
Licensed under Creative Commons: By Attribution 4.0*.

## Making it

- Preview one shot in a window: `tools/trailer.sh preview 5` (or a range, `5-9`).
- Render everything and cut it on the song: `tools/trailer.sh render` → `test_output/trailer/trailer.mp4`.
- Stills of a shot's start, middle and end to frame it: `tools/trailer.sh stills 5`.
- How the director works: `docs/notes/trailer/director.md`.
