# First-run tutorial (#517)

- **What**: a glass card in the top-left corner (`Core/Tutorial`, CanvasLayer 11 like the prompt bar)
  the first time a world is entered from the menus (Explore or Multiplayer, never a replay). Steps,
  in `Core/TutorialSteps` (plain C#, tier-0 tested in `TutorialStepsTests`): look around, walk, run
  and jump, the travel menu (get on something), the camera toggle, the map (M), the fly camera, and
  a last card naming F1 and Esc. "n / N" in the corner, an amber bar for the step's progress; a met
  step goes green for 0.9 s, then the next fades in.
- **A step ends when the player does it**, read from the world, never from a button on the card:
  view angle turned (any camera; > 30° in one frame is a camera switch and ignored), metres walked on
  foot (> 3 m in a frame is a teleport or an origin shift), `Input.IsActionJustPressed` of jump /
  sprint / camera_toggle / help (not while typing), mounted (`FootPlayer.Vehicle`), place search open,
  fly camera current. Only the map (a pad cannot type a place) and the last card move on by time
  (`OptionalSeconds`, `DoneSeconds`).
- **No new action, so all three devices for free**: texts are `{action}` placeholders through
  `InputHints.Format`, with pad and VR wordings where a stick or the head replaces a key
  (`TutorialStep.Pad` / `Vr`), rebuilt on `PlayerInput.DeviceChanged`. The headset draws every
  CanvasLayer, so the card is in VR too.
- **Steps that do not apply are left out** (`TutorialSteps.For`): the travel menu when
  `Permissions.CanSpawnVehicles` is false (online without admin, a match), map and fly camera in a
  Battle Royale match.
- **Hidden, not advanced,** while something covers the screen: a menu, the travel menu, the map, a
  replay (`ClientWorld.StartTutorial`'s `covered`).
- **Skip**: the pause menu has "Skip tutorial" while it runs (the card's footer says so). Done or
  skipped saves `GameSettings.TutorialDone` with `SaveOnly` (one key: a command-line `--rings` must
  not become the saved value). Settings › Gameplay › Tutorial "Play again" clears it, and starts it at
  once over a running world (it shows when the menus close). Leaving mid-way starts it over next time.
- **Who starts it**: `GameShell.FinishLoading` (`Tutorial.Wanted(fromMenus: true)`). Command-line
  runs never call that, so probes are untouched; `--autostart` screenshots skip it unless
  `--tutorial` (whitelisted in `UseTitle`), which forces it: `--autostart --tutorial --uishot out.png 40`.
- **Starts on foot** (same issue): Explore from the menus begins on foot on open ground, see
  `core/ground-start`.

## A mini tutorial per kind of ride

- **What**: the first time the player **drives** each kind (`VehicleIntroKind`: road bike, skis,
  car, motorbike, truck or bus, boat, paddle steamer, helicopter, plane, paraglider or parachute,
  wingsuit, pigeon, airliner), `Core/VehicleIntroCard` shows its 3-4 essential controls in the same
  corner, "New ride". Each row ticks (○ → green ✓) once any of its actions is held
  (`Input.IsActionPressed`; keys already held in the first 0.6 s do not count, so W from walking up
  to a car does not tick "Accelerate"); all ticked, the title goes green, 1.2 s later it is gone and
  the kind is saved in `GameSettings.VehicleIntrosSeen` (`SaveOnly`). Getting off first: shown again
  next time. The footer names the rest (lights, doors, get out) and F1.
- **Rows** are data in `Core/VehicleIntros` (plain C#, `VehicleIntrosTests`): `IntroRow(Keys,
  Actions, Pad)`, keyboard and pad words like the tutorial's (`{action}` placeholders only, a test
  checks), action names as strings (no Godot). `KindOf` maps the `Rideable` type (`Airliner` before
  `Plane`, `Steamer` before `Boat`: subclasses first); airstairs and parked trailers get none.
- **The driver only**: `SeatIndex == 0` and no `Host` (a passenger learns nothing here). The body is
  `ClientWorld.Viewer` (the local player, or a probe's body that owns the camera, as the prompt bar).
- While a card is up the first-run tutorial is covered (it waits under it): mounting ends its travel
  step, the car's card comes, then the camera step.
- Started by `GameShell.FinishLoading` in every session from the menus (`StartVehicleIntros`), and in
  a command-line run with `--tutorial`: `--tutorial --ride car:0,8,out.png` screenshots the car's
  card (`--ride` takes car:N, truck:N, moto:N, bike, skis). Settings › Gameplay › Tutorial "Play
  again" clears the seen list too.
- **The trailer's card** (`VehicleIntroKind.Trailer`, `VehicleIntro.Near`) is the one met on foot:
  "Close by" once a lone trailer stands within 18 m (`VehicleManager.LoneTrailerNear`, flat to the
  body: the kingpin is the far end walking up from behind; checked twice a second), with rows that
  only tick in a truck (`IntroRow.While`; a row with no action ticks by being in one): get into a
  truck or a pickup, back up with the brake, couple. A pickup (the F-150's tow ball, #463 / PR #470)
  is a `Truck`, and a boat trailer a lone trailer, so both are covered; the footer names the boat
  trailer's launch / winch (`{car_door}`, stopped). While it is up the truck's own card waits; it closes
  unseen when no lone trailer is within 60 m and the player is not in a truck.
- Screenshots: `--tutorial --flycheck heli|plane,out.png` (the sortie presses the real actions,
  so the rows tick: climb and throttle do), `--tutorial --ride foot,10,out.png --trailer 0
  --brake-at 0` (the trailer parked 14 m ahead).
