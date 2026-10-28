# Fahnenschwingen, the Älplerfest's flag-swinging game (#1269 part 5)

`src/Occasions/Festivals/Fahnen/` (namespace `UnitSport.Occasions.Festivals.Fahnen`), the server half in `FestivalService.Fahnen.cs`, the Älplerfest's def in
`FestivalCatalog.Aelplerfest.cs`; the plan is `docs/plans/adventure-festivals.md` section 5, the framework `festival-framework`. A routine of 8 to 12 figures
on a 96 BPM march, drawn with the mouse or the right stick, a high throw and a catch; the grades and the score are the server's.

## The festival

`FestivalCatalog.Aelplerfest()` registers `aelplerfest` ("Älplerfest", 14-24 August: Höhematte Interlaken, else Schwägalp, else Schwarzsee, else the largest town)
from the catalogue's static constructor; its default `OccasionEntry` is in `OccasionConfig.Defaults` (priority 8, Atmosphere off). **The other Älplerfest games add
themselves to this one def** (`Games`, `Days`): the def lists only `fahne` (open from day 3) and day 1 opens nothing yet. Milestones 1500/4000/8000 points pay 20/40/80.
Under `--festival test` the def is re-registered at the fixture's start so the checks can play it on `--date 2027-08-16`.

The **flag ground** is 12 m east of the site's middle (`FestivalCatalog.FahneGround`, 16 m round it counts); `FestivalOccasion.FlagStage` (a square of planks with a
Swiss flag at each corner and a swag of pennants, model viewer under Occasions › Prop) stands there.

## What is pure (tier 0, `FahnenRulesTests`, `FlagMotionTests`)

- **`GestureRing`**: a path of 8-sector samples (0 = up, clockwise; 30 Hz) is read as `Up`, `Down`, `Left`, `Right`, `CircleCw/Ccw` (7 sectors one way, at most 2 back),
  `Eight` (3+ each way, 10+ in all) or nothing. The client records the sectors and the server reads them again, so a figure is judged where it is paid.
- **`FahnenRules.Plan(seed)`**: 8-12 figures, the Ufzug first, one Hochwurf (two in a routine of 11+), never the same figure twice; a gesture takes 4 beats, the throw 8, the count-in 4.
  Figures: Ufzug (up), Mühlirad (a circle), Achter (an eight), Lufthieb (left or right), Unterschwung (down), Hochwurf.
- **Windows** (seconds round the figure's last beat): Perfect 0.15, Good 0.30, Off 0.50. The throw is held from its start, **let go on beat 1, 2 or 3** (the nearest beat; windows
  0.12 / 0.22 / 0.30, kept under half a beat) and **caught on the beat it lands** (the release beat plus the flight, held beats + 1: 2 to 4 beats in the air). The worse of release and catch
  counts; a missed catch **drops the flag and ends the routine**. A throw is worth `1 + 0.4 x (flight - 2)` (1.0 to 1.8).
- **Score**: 100 / 70 / 40 / 0 a figure, out of 1000 (`MaxScore`); `BatzenFor` = score / 25 (a perfect routine is 40 Batzen, the daily cap of 100 is two and a half of them).
  Points to the party board are the score.
- **In step**: routines start on the festival clock's 8-beat grid (`SlotOf`: the next boundary at least 3 s away; `SeedOf(instance, slot)`), so **players who ask in the same slot swing the
  same routine**. Two routines of a slot are in step when both landed Good or better on 60 % of the same figures; every routine with a partner in step earns **x1.3** (`SyncedOf`).
- **`FlagMotion`**: the flag's pose (offset, roll, swing) per figure and phase, and the throw's parabola (1.1 m a beat of flight); the flag is a rigid node moved by this.
- The wire format (`ToWire`/`FromWire`: four ints a figure and one byte string of sectors, refused when malformed) and `BotRoutine(routine, skill, seed)`.

## The server (`FestivalService.Fahnen.cs`, protocol 68)

- `ServeFahneStart(peer)`: a running Älplerfest, the game open, the body on the ground, an adventuring peer, 10 requests a second, not already swinging. Plans the routine from the slot
  and tells the peer `TellFahneStart(start, seed)`; the client plays the same routine from the shared clock (`ClockSync.ServerNow`).
- `ServeFahneRoutine(peer, stamps, sectors)`: refused when none was planned, when it is over by more than 6 s, malformed, **from the future** (a stamp later than the routine has run, 0.6 s of slack),
  or when the body left the ground; else graded with `FahnenRules.Grade` and queued.
- **Settling**: every 0.25 s the server settles each slot whose routines are due (the routine's end + 2.5 s): `SyncedOf` over the slot, `WithSync`, then `Award(peer, festival, Batzen, ..., "fahne", score)`
  (the framework's caps, the board and its milestones) and `TellFahneResult(score, Batzen, synced, hits, figures, dropped)`.
- **Bots** take the same two methods: `ServeFahneBot(peer, skill)` asks and hands in `BotRoutine` when it would have finished. A bot can be in step with a human (it makes up the field) and earns
  nothing (`Award` refuses a negative peer id). No brain walks a bot to the ground yet (a gap in `bots/bot-capability-map`).
- RPCs: client to server `RequestFahneStart`, `RequestFahneRoutine(int[], byte[])`; server to client `TellFahneStart`, `TellFahneResult`. Offline the local player is the authority and no RPC is used.

## The client (`FahnenGame`, a `CanvasLayer` child of the client world)

- E / Y at the ground (`InteractionFor` in the prompt chain, `TryInteract` in `FootPlayer.TryInteract`) asks; while it runs `FahnenGame.Swinging` takes the items (`ItemController.ActivePlayer`) and
  `FootPlayer.LookTaken` takes the look (mouse and right stick), so the camera stays and the mouse or stick is the brush.
- A drum keeps the beat (`FahnenSounds.Drum`); the HUD is bottom-centre like `PogoQte`: the beat ring closing on each beat, the figure's name and a glyph of its stroke, the words (cached per figure and device),
  a bar that runs out on the figure's last beat, the grade after each figure (PERFECT / GOOD / OFF BEAT / MISS), then a result panel for 6 s (points, figures on the beat, in step x1.3, Batzen).
- **Input**: gestures read the right stick (past a 0.45 dead zone) or the mouse's motion, one sector per 1/30 s; the throw is `use_item` held and released, pressed again to catch.
  Every figure has its own window (its start - 0.3 s to its end + 0.3 s), so a stroke belongs to one figure. The pad route is the right stick and the use button, which the VR controllers replay through `XrPad`.
- **The flag** (`FahnenFlag`): a staff and a cloth grid built once, `shaders/flag_cloth.gdshader` ripples the cloth from `swing` (written only when it moved) and paints the Swiss cross; the node is moved and
  turned by `FlagMotion`, in front of the player's right shoulder. Nothing is rebuilt per frame. Model viewer: Festivals › Fahnenschwinger flag.
- **Sounds** (`FahnenSounds`, static banks listed by the sound player): march drum, whoosh, snap, catch.

## Not built

- The **body pose** (`FootPlayer.FestivalPose`, the dance layer) and other players' flags: only the swinger sees the flag; the swing is not replicated.
- Swinging the flag **by hand in VR** (the plan's "gesture from the hand path"): the pad route first, hand ways after a headset playtest (`xr/vr-action-map`, status `new`, the hand way a `gap`).
- A bot brain that walks to the ground; the stand's Batzen shop; the quest step kind `FestivalGameStep`.

## How to check

- Tier 0: `FahnenRulesTests`, `FlagMotionTests`.
- Quick: `--fahnecheck --festival test --date 2027-08-16 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure`: day 3 of 11, the prompt on the ground, a perfect routine
  through the real game (stick and use key scripted from the shared clock), a bot in the same slot, x1.3, Batzen and board points, the bot earns nothing, a second routine alone, the daily cap, off the
  ground and on day 2 refused. Windowed it saves `test_output/fahne_swing.png`.
- Net: `tools/fahnenetcheck.sh`: two clients ask in the same slot, both get the start and seed over the wire, play, are told their result, in step, paid, the Batzen and points in their own section
  and board. Headless clients run in real time, so the probe asks for most figures on the beat, not all.
