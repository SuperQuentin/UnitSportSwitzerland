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
