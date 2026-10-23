# UI: menus, loading screen, theme (`src/Ui/`)

The title screen and its pages, the pause menu, the loading screen, the shared theme and kit. The app
flow around them is `Core/GameShell`. In-game HUDs (chat, inventory, ride picker...) live with their
systems; new or reworked ones adopt the menu look by following `style-guide`.

Index only: one line per note in `docs/notes/ui/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/ui`.

## Architecture

- `screens` — Title -> Loading -> InWorld -> Leave: `GameShell` page stack, pause menu, command-line runs skip the title (`UseTitle` whitelist), --menucheck
- `theme` — `UiTheme` (code-built Theme, glass panels, amber, SystemFont), `UiKit` rows/buttons with the hover slide, `Modal`, `Icons` drawn at startup
- `style-guide` — How any HUD/panel adopts the menu look: theme the root Control, glass panel sizes, text colours/sizes, selection rows, outlined floating text, PR checklist (example: `Core/ChatUi`)
- `ui-theme-panels` — HUD panels via `UiTheme.Flat`/`GlassPanel`, titles `UiTheme.Title`, "press E" lines `UiTheme.Prompt`, amber `UiTheme.Amber`: no hand-built `StyleBoxFlat`, no amber literal
- `loading` — Loading screen: real stages from `ClientWorld.Stage`, `ChunkManager.ProgressNear`, joke lines, Cancel; failure goes back with the reason
- `map-screen` — The map of Switzerland (#515, #799): download terrain (numbered steps), where to start (solo) and where a hosted server's players start; one marker said in words, Move tool by default (drag pans, click places), draw tools under Advanced; three cached draw layers, redraws only on change, --mapcheck
- `tutorial` — First-run tutorial (#517, `Core/Tutorial`, steps in `Core/TutorialSteps`): a corner card, each step ends when the player does it; skip in the pause menu, replay in Settings › Gameplay, `TutorialDone`, `--tutorial`; a mini tutorial per kind of ride the first time you drive it (`VehicleIntroCard`, `VehicleIntrosSeen`)
- `update-check` — Title screen asks GitHub for the releases once per session; Update (delta chain, staged, swapped on quit/restart) or Download (full archive) or Later; `--fakeversion`, `--updatefeed`, `--updateaccept` (#532)
- `quick-menu` — Hold F2 / pad Back (#840): `Ui/QuickMenu` ring (Activities, Invites, Inventory, Vehicles, Map, Phone, Emotes, Bird journal, Character, Controls) drawn with the shared `Ui/Radial`; a tap of F2 accepts an invite, a tap of pad Back is still the inventory (on release); `--quickmenu`
- `activities` — `Ui/ActivitiesScreen` (#840): tabs Invites / Battle Royale / Race, every button sends the console's command, admin BR settings and open/start/cancel, race list (`RaceManager.RequestRows`), party and teleport requests
- `chat-bubbles` — Comic speech bubbles over players who talk near you (#862, `Ui/Bubbles/ChatBubbles`, the films' `SpeechBubble`): `ChatManager.Say`/`Spoken` name the speaker, 36 m and in view, capitals shout, (parentheses) think, VR as world billboards, Settings › Gameplay, `--bubblecheck`
- `phone` — Foldable phone (#889, `src/Phone/`): Up / quick menu › Phone; cover quick actions, open = camera onto the inner screen then the same `SubViewport` full screen; Map / Contacts / Activities; `Teleporter.Curtain` holds a jump until the screen covers the view, `Teleporter.Arrived` folds it away there; one replicated byte `PhoneState`; the camera app walks once open (#1149); `--phonecheck`
  - Training app (#901): see `player/home-trainer-training`
- `travel-menu` — R travel menu (tabs, card grid, brand/model folders (#410), pre-rendered thumbnails cached in user://thumbs, live stage: doors open + lamps on while pointed) and the F1 controls screen, both sized to the window (#210)

## Gotchas

- `teardown` — Leaving a world in place: static events, Multiplayer-signal lambdas, singletons, deferred calls; `WorldStatics.Reset`, --leavecheck
