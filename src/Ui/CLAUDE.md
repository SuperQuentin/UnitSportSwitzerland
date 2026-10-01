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
- `loading` — Loading screen: real stages from `ClientWorld.Stage`, `ChunkManager.ProgressNear`, joke lines, Cancel; failure goes back with the reason

## Gotchas

- `teardown` — Leaving a world in place: static events, Multiplayer-signal lambdas, singletons, deferred calls; `WorldStatics.Reset`, --leavecheck
