# Playing the pigeon (#217)

## Rule

- **The pigeon is a `Flyer` worn like the paraglider** (`RideKind.Pigeon` = 124, `Player/Pigeon.cs`), not a
  vehicle: nothing is left in the world when you change back. Its motion is `Player/PigeonFlight.cs`, pure
  maths linked into the tier-0 tests (`PigeonFlightTests`); keep it free of nodes. Three modes travel in
  `FlightMotion` (`Spool` = wing beat 0..1, `Control` = `PigeonFlight.Mode`), which `Flyer.WritePose` already
  replicates, so a remote copy flaps and folds its wings with no new field.
  - Air: Jump / A flaps (16 m/s, +6 m/s climb; Shift 21 m/s, +9). Forward speeds it up and takes only
    1.5 m/s of the climb (#519: it took 4, so W + flap sank and felt like a height cap; there is none);
    let go glides (11 m/s, ~6:1), Crouch / B
    dives (−14 m/s), stick turns (2.4 rad/s) and the free look banks (`LookBank` 0.8), stick back slows.
  - Ground: walks 1.1 m/s (Shift 2.4), held onto the ground by a steady −1.5 m/s (gravity accumulating each
    step was the canopy's trap: the vertical speed grew until a 2 m/s correction). Jump takes off.
  - Perched: a bird in the air, not flapping, under 9 m/s and within 1.3 m of a roof or ledge perch of
    `TownPerches` (#143; `BirdLife.NearestPerch`, streets excluded: that is the ground) is put on it, still.
    Jump or stick forward leaves. The perch query only runs for a slow, gliding bird.
- **Crashes over 20 m/s** (`CrashSpeed`, #519): cruising (16) bounces off walls, a boosted flap or a dive
  into a wall or the ground does not. `Pigeon.Fly` catches the ground hit itself (the player's wall check
  only counts a floor hit's flat part). The crash is `BirdLife.PlayerSplat`: feathers, a white splash
  (`Explosion.Burst`), a thud, "SPLAT!", the player on foot and dazed, **no damage** to it or anyone
  (no `Explosion` blast). Online `BirdNet.SendSplat` -> server (body within 8 m, one a second) -> every peer
  within 150 m (`BirdLife.CrashSplats` counts them). The capsule is 0.12 × 0.3 m.
- **Drop** = `PlayerInput.Fire` (LMB / RB; the right trigger in VR), one per `BirdNet.DropInterval` (0.4 s).
  Online the client sends `BirdNet.SendDrop` → the server checks (the sender is a pigeon, within 4 m of its
  body, rate) and picks the victim: a person on foot within 1.3 m flat and 1.7–60 m below (`BirdNet.Victim`),
  then the #143 `Dropping` broadcast, now with `by` and the dropper's name. The victim's toast names the
  dropper; the dropper's client counts `BirdLife.PigeonHits` ("Got one! (n)"). Offline it just falls.
- **No inventory as a pigeon** comes for free: `ItemController.UsablePlayer` is on foot only, so the hotbar,
  the inventory screen, the held item, pick-ups and item use are all off while mounted, and the pack is
  untouched. Do not add a pigeon special case there.
- **Camera**: third person is a 1.9 m chase cam; first person (V) and always in VR is the bird's eye
  (`FootPlayer.PigeonEye`): level, turned with the heading, no bob, roll or pitch on a headset.
- **VR** (`XrPad`, `XrRig`): the triggers act as shoulders (right = drop), A flaps, B dives; snap turn
  works perched and walking (`FootPlayer.PigeonSnap`); the comfort vignette is not halved (no cockpit frame).
  Body flapping (arms as wings) is not done: optional in #217.

## How to check

- `tools/test.sh unit` (`PigeonFlightTests`).
- `<godot> --headless --path . -- --flycheck pigeon --world flat`: walk, flap up, glide, dive, land walking
  (measured: walk 1.10 m/s, cruise 16.0 m/s climbing 6 m/s, glide 11.0 / −1.8 = 6.1, peak 28 m).
- `<godot> --headless --path . -- --flycheck pigeoncrash --systems birds` (needs birds, so not `--world flat`):
  W + flap climbs 4.5 m/s, a boosted dive hits the ground at 23 m/s: on foot, health 100, one splat.
- `tools/pigeonnetcheck.sh`: server + two clients; items off and kept, B sees a bird, A drops on B (B told by
  whom, A scores), A crashes beside B and B sees the splat, items back on foot.
