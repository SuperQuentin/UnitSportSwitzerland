# Festival alphorn and yodel: a song for up to four voices, graded by the server, answered by the valley (#1269 part 4)

`src/Occasions/Festivals/Horn/` (namespace `UnitSport.Occasions.Festivals.Horn`) and `FestivalService.Horn.cs`; the plan is `docs/plans/adventure-festivals.md` section 4
("Part 4 as built" lists the deviations). The game of the Älplerfest's day 1; it is the only game of `FestivalCatalog.Aelplerfest` until the other parts add theirs.

## What the player does

- At a running festival whose `alphorn` game is open, stand at the **alphorn row** (three horns on trestles, `HornGround.AlphornRow`, 16 m east and 7 m north of the site's
  middle: that is where you stand, at the mouthpiece end, the horns lie `HornsAheadM` = 3.2 m further east; 7 m reach): the prompt is "[E] Play the alphorn and yodel" (`HornGround.InteractionFor`, in the outdoors chain of `InteriorManager.Interact`; `TryInteract` in `FootPlayer.TryInteract`).
- The server seats you in a song (`FestivalService.ServeHornStart`): the first player to sit down opens one, whoever sits down within 45 s joins it, and they play the same chart in the order they sit:
  I melody, II a voice below it, III the drone, IV the yodel. A fifth finds the row full.
- `HornGame` (a CanvasLayer at the bottom, clear of the centre and the hotbar) scrolls the part's notes towards a line after a four-beat count-in. **Pick** the note (the part's
  partials are its lanes): mouse wheel, W / S, the right stick; **hold** blow (`use_item`, RT) from the note's start to its end; **yodel** (voice IV): tap `jump` on each beat.
  Esc leaves; walking 25 m from the row ends the song with nothing paid.
- The **breath bar** drains one beat per beat of blowing and refills two per beat of rest (`AlphornRules.BreathBeats` 8, `RefillPerBeat` 2); empty cuts the blow. Charts always
  leave rests long enough that a perfect part is never winded (a unit test over 300 seeds).
- When the song is over the whole part's blows go to the server (`RequestHornSong`); the answer is the score, the harmony, the Batzen and the party points
  (`TellHornResult`), shown for 9 s.

## The rules (pure, tier 0: `AlphornTests`)

- **`AlphornChart.Make(seed)`**: 16-32 melody notes at 60-76 BPM on the ladder 4 5 6 8 9 10 11 12 (the 11th is the "Alphorn-Fa"), a random walk of at most two steps, lengths 1-4 beats
  with a rest of 1 beat (2 after 3-4), ending on partial 8 for 4 beats; `Of(part)`: Third = for each melody note the lower partial with the best interval, Drone = 2 beats on / 2 off
  on partial 2-4 under the bar's melody, Yodel = a tap on every other melody note, chest voice (3-5) and head voice (6-10) taking turns. Four beats of count-in.
- **`AlphornRules`**: `Frequency(p) = 46.2 Hz x p` (F-sharp 1; partials 2-12). `Interval(a, b)` folds the ratio into one octave: unison / octave 3, fifth 3:2, fourth 4:3, major third 5:4 = 2,
  minor third 6:5, sixths 5:3 and 8:5, and 11:8 (the Fa) = 1, seconds and sevenths -1, else 0. Timing is the pogo's (`PogoBeat.Window`: Perfect 70 ms, Good 140 ms, capped at 0.4 beat).
  A note is worth 10 / 6 for its start plus 5 / 3 for its end (15), a yodel tap 10 / 6; the right partial is required.
- **`Grade(chart, part, plays)`**: a blow counts for the nearest note no earlier blow took, if inside the Good window; breath is simulated; returns the points and the blows as they
  sounded. **`Harmony(chart, voices)`**: every half beat of the song, each pair of sounding voices is scored by `Interval`; a mean of 2 or more is the full x1.6, a clash is x1, and under
  eight half beats of two voices together there is none. **Chart-relative**: each player runs their own clock, so nothing is aligned over the wire.
- **Pay**: Batzen = accuracy x 20 x harmony (nothing under 25 %), at most 32 a song; the festival game's daily cap is 80 (`AlphornRules.DayCap`), so about three full-harmony songs a day; party points
  = score x harmony (never cut). A song of one voice has no harmony and says so.
- **`EchoFinder.Find(ground, e, n, bearing, listenerE, listenerN)`**: five rays in the bell's direction +/- 20 degrees, 25 m steps to 4 km, the first sample 15 m over the horn's height is the
  wall; delay (blower to wall + wall to listener) / 343, gain 60 m / path (6 dB a doubling), low-pass 7 kHz - 1.5 Hz per metre of path, 0.9 kHz at least. A valley 1 km across answers in 5.8 s.
  `HornGame` calls it **once per blow** (never per frame) on `ChunkManager.TryGetHeight` (loaded tiles only: a wall on an unloaded tile does not answer) and replays the note from
  the wall (`AudioStreamPlayer3D`, four pooled, attenuation filter) after the delay; the held tone is stopped the same delay after the release.

## The server (`FestivalService.Horn.cs`, protocol 67)

- Three RPCs on the festival service's node: `RequestHornStart()`, `RequestHornSong(string plays)` (client to server) and `TellHornSeat(seed, part, voices)`, `TellHornResult(...)` (server
  to the owner). Blows travel as `partial,on_ms,off_ms;...` (`HornWire`, strict: a bad string is refused whole; at most 140 blows, 4000 characters).
- `ServeHornStart(peer, out seat)`: needs a running festival with the game open, an adventuring peer (a bot is exempt), the body within 12 m of the row, 10 requests a second at most.
  The seed is `HornWire.SeedOf(instance, n)` for the instance's n-th song (a server count, so no two songs of a festival share one). A second request by the same peer drops its old voice.
- `ServeHornSong(peer, plays)`: the seat must be 90 % of the song old (so a forged early song pays nothing), the body within 40 m of the row, plays well-formed. When all voices of the session
  have sent theirs the song is finished; otherwise `HornTick` (every 2 s) finishes it 25 s after the song would have ended for the last to sit down. The finish grades everyone, scores
  the harmony of the voices that played and calls `Award(peer, festival, batzen, why, "alphorn", points)` for each human; `HornPaid` keeps the results (checks).
- **Bots**: `ServeHornBot(peer, skill, rng)` sits down and sends `AlphornRules.BotPlays`; they raise the humans' harmony and earn nothing. Row in `bots/bot-capability-map`.

## Sounds and models

- `HornSounds` (sound player category "Alphorn", `--sounds,check`): `Hold(p)` a 1 s loop and `Note(p)` a 3 s swell of each partial 2-12 (seven harmonics, dark roll-off, a puff of breath),
  `Yodel(p)` chest or head voice (a buzz through two formants, vibrato) and `Juuz`, the leap. The set `Showcase` lists them lazily.
- `HornGround.AlphornRow` is a `PropKind`, so the model viewer (Occasions › Prop) shows it by itself.

## Checks

- Tier 0: `AlphornTests` (determinism, ranges, the intervals, a perfect song on 300 seeds, wrong or late scores nothing, breath, one blow one note, harmony, the bot, pay, the echo, the wire).
- Quick: `--hornscheck --festival test --date 2027-03-11 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure`: no seat away from the row, E at it, three bots take the other
  voices, a forged and an early song refused, the whole song played frame by frame through `HornGame` (`Scripted`), a ring of walls a kilometre out echoes in 5.4-6.4 s, four voices graded and paid
  (the book, the board), then the yodel seat.
- Net: `tools/hornnetcheck.sh` (Lea melody, Tom second voice, same seed, an early song refused, both results and books on the remote peers). Tom sits down within 45 s of Lea.

## Gaps

Others do not hear a blower (only the blower and the echo). A held `ItemId.Alphorn` still has only today's call. VR plays through the pad route (right stick, trigger); raising a horn to the mouth is a later
target. The Älplerfest's other games, its Schlussgang and the Schwingerkönig's crowning come with their parts.
