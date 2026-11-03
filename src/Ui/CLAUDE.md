# UI: menus, loading screen, theme (`src/Ui/`)

The title screen and its pages, the pause menu, the loading screen, the shared theme and kit. The app
flow around them is `Core/GameShell`. In-game HUDs (chat, inventory, ride picker...) live with their
systems; new or reworked ones adopt the menu look by following `style-guide`.

Index only: one line per note in `docs/notes/ui/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/ui`.

## Architecture

- `screens` — Title -> Loading -> InWorld -> Leave: `GameShell` page stack, pause menu, command-line runs skip the title (`UseTitle` whitelist), --menucheck
- `theme` — `UiTheme` (code-built Theme, glass panels, amber, SystemFont), `UiKit` rows/buttons with the hover slide, `Modal`, `Icons` drawn at startup
- `menu-skins` — Menu pages wear a skin from `assets/skins/*.json` (#1135): Low-poly Sport by default (slanted controls, sky blue, Saira), Classic; the HUDs keep `UiTheme.Get()`; shared variations (`DimLabel`, `MenuEntryButton`, `TabButton`...) so `UiKit` builds for both; Settings › Video › Menus, `--menuskin`
- `style-guide` — How any HUD/panel adopts the menu look: theme the root Control, glass panel sizes, text colours/sizes, selection rows, outlined floating text, PR checklist (example: `Core/ChatUi`)
- `ux-guidelines` — **Read before any UI work** (menus, HUDs, popups, VR panels): placement conventions, PC/touch/gamepad/VR rules, spacing and size tokens, states, navigation, safety, accessibility, self-review checklist
- `ui-theme-panels` — HUD panels via `UiTheme.Flat`/`GlassPanel`, titles `UiTheme.Title`, "press E" lines `UiTheme.Prompt`, amber `UiTheme.Amber`: no hand-built `StyleBoxFlat`, no amber literal
- `loading` — Loading screen: real stages from `ClientWorld.Stage`, `ChunkManager.ProgressNear`, joke lines, Cancel; failure goes back with the reason
- `map-screen` — The map of Switzerland (#515, #799): download terrain (numbered steps), where to start (solo) and where a hosted server's players start; one marker said in words, Move tool by default (drag pans, click places), draw tools under Advanced; three cached draw layers, redraws only on change, --mapcheck
- `welcome` — First-run welcome (#1131, `Ui/WelcomeScreen`): name, figure, account over the title, once on a fresh install (`FirstRunDone`, false only without a settings.json), never with command-line args (`--welcome` forces it), Settings › Gameplay › Show again; `--welcomecheck`
- `tutorial` — First-run tutorial (#517, `Core/Tutorial`, steps in `Core/TutorialSteps`): a corner card, each step ends when the player does it; skip in the pause menu, replay in Settings › Gameplay, `TutorialDone`, `--tutorial`; a mini tutorial per kind of ride the first time you drive it (`VehicleIntroCard`, `VehicleIntrosSeen`)
- `update-check` — Title screen asks GitHub for the releases once per session; Update (delta chain, staged, swapped on quit/restart) or Download (full archive) or Later; `--fakeversion`, `--updatefeed`, `--updateaccept` (#532)
- `whats-new` — Release notes in a modal once after an update (`LastSeenVersion`) and from the title entry; reuses the update check's release list (#1413)
- `quick-menu` — Hold F2 / pad Back (#840): `Ui/QuickMenu` ring (Activities, Invites, Inventory, Vehicles, Map, Phone, Emotes, Journal, Character, Controls) drawn with the shared `Ui/Radial`; a tap of F2 accepts an invite, a tap of pad Back is still the inventory (on release); `--quickmenu`
- `adventure-log` — The Journal on J (#1223, `Ui/AdventureLog`): tabs Quests / Atlas / Collection / Travel / Birds / Fish through `RegisterTab` (adventure tabs hidden in a BR round), `QuestsPage` (Track, Start, Hand over, Abandon), the `QuestTracker` HUD and `QuestToasts`; devices table and UX self-review
- `activities` — `Ui/ActivitiesScreen` (#840): tabs Invites / Battle Royale / Race, every button sends the console's command, admin BR settings and open/start/cancel, race list (`RaceManager.RequestRows`), party and teleport requests
- `chat-bubbles` — Comic speech bubbles over players who talk near you (#862, `Ui/Bubbles/ChatBubbles`, the films' `SpeechBubble`): `ChatManager.Say`/`Spoken` name the speaker, 36 m and in view, capitals shout, (parentheses) think, VR as world billboards, Settings › Gameplay, `--bubblecheck`
- `phone` — Foldable phone (#889, `src/Phone/`): Up / quick menu › Phone; cover quick actions, open = camera onto the inner screen then the same `SubViewport` full screen; Map / Contacts / Activities; `Teleporter.Curtain` holds a jump until the screen covers the view, `Teleporter.Arrived` folds it away there; one replicated byte `PhoneState`; the camera app walks once open (#1149); `--phonecheck`
  - Training app (#901): see `player/home-trainer-training`
- `travel-menu` — R travel menu (tabs, card grid, brand/model folders (#410), pre-rendered thumbnails cached in user://thumbs, live stage: doors open + lamps on while pointed) and the F1 controls screen, both sized to the window (#210)
- `stamina-ring` — The stamina ring (#1225): beside the shoulder or the crosshair, only while not full, one shader (outer segments past 100, the next climb-jump's band, winded orange + blink + panting), VR wrist copy, `--stamina-shot`
- `follow-cam` — `Ui/FollowCam` (#1268): the spectator camera taken out of BR, candidates set on a state change, left/right cycling, no per-frame LINQ

## Gotchas

- `teardown` — Leaving a world in place: static events, Multiplayer-signal lambdas, singletons, deferred calls; `WorldStatics.Reset`, --leavecheck
