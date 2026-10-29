# Occasions: seasonal and community events (`src/Occasions/`)

Time-limited themes and events (#18): the calendar, replication, per-player opt-out, and each
occasion's content (props, sky, creatures, sounds, loot, hunt, hats).

Index only: one line per note in `docs/notes/occasions/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/occasions`.

## Architecture

- `occasions` — Occasions: (`src/Occasions/`, #18): time-limited themes and events — Halloween (1 Sep–31 Oct) and Christmas (1...

- `ufo-sightings` — the `ufo` occasion (#863): first Friday nights (`fri#1` .. `+1`), the server plans per 2 km cell and 75 s slot, every peer plays `SightingPath` from the shared clock, real beams via `TractorBeams.Scripted`, a landed saucer is a parked UFO to board, facets and opt-out, bots, `--sighting`, `Shot.Ufos`

- `festival-framework` — festivals (#1269 part 1): `FestivalDef` (sites, day table, milestones, show), `FestivalOccasion` + the `Games` facet (Off hides the bunting, never the score), the tokens `04-mon#3` and `easter-46`, relocation to the largest town, the `festivals` section and Batzen (cap 600, 1 Batzen = CHF 1, converted once after the end), the party board keyed by sorted member keys, `FestivalService.Award`, the Journal's Festival tab, `--festival test`, protocol 59

- `festival-fahnenschwingen` — the Älplerfest's flag swinging (#1269 part 5): `GestureRing` + `FahnenRules` (pure: routine from a slot's seed, windows, score, x1.3 in step), the server in `FestivalService.Fahnen.cs` (protocol 67), `FahnenGame` (HUD, stick/mouse figures, the use key's throw), `FahnenFlag` + `flag_cloth.gdshader`, `FahnenSounds`, `--fahnecheck`, `tools/fahnenetcheck.sh`
- `festival-fireworks` — the closing show (#1269 part 2): `FireworkShow.Plan(seed)` (pure, 6-12 min, three acts and a 40-shell finale), the server fixes `ShowStart`/`ShowSeed` in the running list on the last day at 21:30 game time (no new RPC), `Fireworks` plays it from `ClockSync.ServerNow` with pooled GPU particles, flashes and bangs delayed by distance / 343, `FestivalShowWatched`, `--fireworkscheck`

- `festival-steinstossen` — the stone put (#1269 part 3): `SteinRules` (pure: six press stamps, a seeded meter, the 90 BPM run-up graded with the pogo's windows, a ballistic flight, Batzen by distance over par), the server path `SteinService` (`ServeBegin`/`ServeAttempt`/`ServeBot`, protocol 72), `SteinGame` (prompt, meter, drum, arc, stakes), the ground as `PropKind`s, `FestivalCatalog.Aelplerfest()`, `--steincheck`, `tools/steinnetcheck.sh`
- `festival-desalpe` — the Désalpe cow drive (#1269 part 7): `DesalpeRoute` + `HerdFlock` (pure: a Leitkuh on a carrot that walks only while a driver is near, slots behind her, drivers push by the point of balance, strays at junctions, lost at 150 m), `DesalpeRun` (grazing, driving, arrived; score cows x 50 + dressed x 10 + time), `DesalpeVote`; the server in `FestivalService.Desalpe.cs` (protocol 73, farmhands make up the flanks), `DesalpeHerd` (snapshots drawn as MultiMeshes: bodies, heads, Treicheln, one MultiMesh per headdress in use), `DesalpeGame` (E headdress, Use colour, the sign, the vote), `tools/desalpenetcheck.sh`
- `festival-christmas` — the Christmas market (#1269 part 8, #1348): `christmasmarket` (25 Nov - 23 Dec, beside the Christmas occasion), `ChristmasRules` (pure: eight stalls round the site, wares and Batzen prices, the free taste = the stall-hop's stamp, the Marktgang, the Samichlaus on 6 December), the server in `FestivalService.Christmas.cs` (`RequestMarket`, protocol 76), the Glühwein mug (`ItemId` 501, 2 dl 10 % through `AlcoholService.ServeDrink`, none with `--alcohol off`), `ChristmasGame` (E at a stall or the Samichlaus), `ChristmasModels` / `ChristmasSounds`, `--christmascheck`, `tools/christmasnetcheck.sh`
- `festival-fasnacht` — the Fasnacht (#1269 part 8, #1357): Luzern's `fasnacht` (`easter-52 .. +5`) and Basel's `baslerfasnacht` (`easter-41 .. +2`), the Larven stall (a mask item 506-509 and Räppli 505 once a day, `Headwear` 5-10), Räppli to throw (`ItemUse.Confetti`, `ItemEventKind.Confetti`, `RaeppliFx`), the Guggenmusik (drum, trumpet, sousaphone on the alphorn game's rhythm engine, 120-140 BPM, in-sync din scored: `GuggenChart`/`GuggenRules`), Basel's Morgestraich (street lamps out 04:00-05:30 of day 1 through `world_lamps`, lit lanterns); `FestivalService.Fasnacht.cs` (protocol 78), `--fasnachtcheck`, `tools/fasnachtnetcheck.sh`
- `festival-escalade` — the Escalade (#1269 part 8, #1351): `12-sat#2 .. +1` at Genève (relocated otherwise), three games on the interact key: the Marmite (youngest and oldest account of the party smash it together, the saying, chocolate), the Course de l'Escalade (six gates, the server follows the position, pay by time against par), the soupe pot (feeds the party); `EscaladeRules`/`EscaladeGame`/`EscaladeModels`/`EscaladeSounds`, `FestivalService.Escalade.cs` (protocol 77), `ItemId` 503-504, `--escaladecheck`, `tools/escaladenetcheck.sh`
- `festival-alphorn` — the Älplerfest's alphorn and yodel game (#1269 part 4): the row of horns at the site, a song opened by the first player to sit down, joined within 45 s (up to four voices: melody, second voice, drone, yodel), natural tones 2-12 of F-sharp 1, the breath bar, blows graded by the server from the whole song, harmony scored chart-relative (x1.6), the valley echo (`EchoFinder`), bots, protocol 71, `--hornscheck`, `tools/hornnetcheck.sh`

## Commands

- `commands` — Commands: --avatars, --date, --decorlog, --hats, --headless, --huntcheck, --occasion, --occasioncheck, --path, --shot, --sighting, --soundcheck

## Gotchas

- `new-color-has-alpha-1` — `new Color(r, g, b)` has alpha 1, and `ps1_prop` reads vertex alpha as "this is a light"
- `headless-runs-cannot-read-multimesh` — Headless runs cannot read MultiMesh transforms back
- `live-start-stop` — `/occasion start` shows the look whatever the preference; stop tears down; GPU fence timeouts, not occasions, made the broken shapes (#881)
