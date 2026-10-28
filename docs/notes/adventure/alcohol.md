# Alcohol (#1271)

Plan: `docs/plans/adventure-alcohol.md`. Code: `src/Adventure/Alcohol/`. A real blood alcohol level, its effects on walking and
driving, an electronic and a single-use breathalyzer. Nothing in the game rewards drinking.

## The model (`Bac`, `AlcoholRules`: plain C#, tier 0 in `BacTests`)

- Grams of ethanol = ml x ABV x 0.789. Beer 33 cl 5 % = 13.0 g, wine 1 dl 12 % = 9.5 g, kirsch 2 cl 40 % = 6.3 g, Gluehwein 2 dl
  10 % = 15.8 g (`AlcoholRules.GluehweinGrams`, the festivals plan hooks its item to `AlcoholService.ServeDrink`), alcohol-free beer
  33 cl 0.5 % = 1.3 g. `ItemDef.DrinkMl` / `Abv` carry the first four and the beer; the fondue pot adds 4 g a wine pour and 3 g with
  kirsch (`AlcoholRules.FondueWineGrams`, `FondueKirschGrams`).
- Absorption: unabsorbed grams halve every 15 min (30 min when `StaminaBuff.Fed`, recorded per drink). Distribution: grams / (kg x
  0.6). Elimination: 0.15 per mille an hour, stops at 0. Stepped over the log (30 s steps, 2 880 at most), because the clipped
  elimination has no closed form; asked at most 4 Hz.
- Body mass from the figure's build (`AlcoholService.MassOfBits`: 62 to 90 kg, 75 unknown), shown nowhere.
- Clock: real time, `AlcoholClock` (`--alcohol-clock N` speeds it for checks, `Skip` jumps it). A player who logs off drunk comes back
  sober.
- Mouth alcohol: 15 min from the last drink (`Bac.MouthLeft`).

## Where it lives

- The `alcohol` progress section (`AlcoholProgress`, the last 24 h of `Shot`s, 200 at most) is the server's. The section reaches its
  owner through the progress sync, so the owner computes its own level from the same log (no `Level` message, unlike the plan).
  Nobody else's level is sent anywhere.
- `AlcoholService` at World/Alcohol on the server and every client. RPCs: `ReportDrank(item, fed)`, `RequestSelfTest(single)`,
  `RequestOffer(target)` (client to server); `TellResult(tester, subject, centi, waitSeconds, kind)` (server to client). Protocol 61.
- `--alcohol off|mild|real` (default real) rides `Arrival.Rules` to `Permissions.ServerAlcohol`; offline it is
  `GameSettings.AlcoholMode` (Settings > Gameplay > Alcohol). Off: drinks log nothing, readings are 0.00. Mild: effects feel the half
  of the level and nobody passes out.
- Chat: `/alcohol` (the mode), `/alcohol test drink <beers> <minutes ago>` (checks; on a server it needs `--alcohol-test`).

## Effects (`AlcoholBody` on `FootPlayer.Drunk`, set on the local body only)

| Level | Effect |
|---|---|
| 0.3 to 0.5 | camera sway 0.3 deg at 0.2 Hz (`PlayerFeel`, a frustum offset); the glass icon by the stamina ring from 0.3 |
| 0.5 to 0.8 | sway 0.6; vehicle and walk input lag 100 ms |
| 0.8 to 1.5 | lag 200 ms, 0.05 steering noise, a slow walk drift (8 deg), edge blur (`shaders/alcohol_edge.gdshader`) |
| 1.5 to 2.5 | lag 300 ms, a stumble every 20 to 40 s (`FootPlayer.Fall`, the stun pose, replicated), free climbing refused ("Not in this state") |
| 2.5 and over | passes out for 60 s (`FootPlayer.PassOut`: `Down` = 1, still), wakes under 2.4 |

- The filter acts on the intents: `RideInput` in `RidePhysics` and `BoatPhysics`, the walk wish after `WalkControls` and before the
  tether. Keyboard, pad, wheel and VR all end there. Flying is not filtered (a plan gap).
- Comfort: `GameSettings.AlcoholViewEffects` drops sway and blur, keeps the lag. VR never sways: a vignette through `XrRig`
  (`AlcoholService.VrVignette`).
- No mesh is rebuilt: a camera offset, one shader uniform set on change, input filters on fixed ring buffers.

## The devices

- Electronic (`ItemId.Breathalyzer` 486, `ItemUse.Breath`, kept): hold Use 5 s with it in the hand (`ItemController.UseBreath`, the
  `Mouth` view pose), then the server's reading on the device screen (`AlcoholHud`): `0.62 ‰ · 0.31 mg/L` and the limit line, or
  `WAIT 12 min` inside the mouth-alcohol window. E at a player with it in hand offers them a test (`breath` invite kind, they accept,
  both screens read it; a server-run bot accepts at once and reads 0.00).
- Single-use (`BreathTestSingle` 487, packs of two, used up at the end of the blow, even an early one): the tube turns green (under 0.5)
  or amber, no number; an early reading says it is unreliable.
- Shops: pharmacy (both), petrol station kiosk (tests, alcohol-free beer), grocery (alcohol-free beer); lines appended to the catalogues.
- Models: `AlcoholMeshes` (device, tube clear / green / amber) shown by the viewer (category Adventure); sounds: `AlcoholSounds` (blow,
  beep, hiss) shown by the sound player.

## Checks

- `BacTests` (tier 0), `--alcoholcheck --chunks fixture:crag --systems ui,physics,adventure` (quick), `tools/alcoholnetcheck.sh` (net).
