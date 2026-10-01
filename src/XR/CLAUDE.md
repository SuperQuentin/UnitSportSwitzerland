# VR (`src/XR/`)

OpenXR VR mode (#186), Quest 2 over Link. It is opt-in at launch: one rig follows the game's own cameras, the controllers act as a virtual pad, and the UI is drawn on a panel in the world.

Index only: one line per note in `docs/notes/xr/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/xr`.

- `setup` — Launching (`--xr-mode on --rendering-driver vulkan -- --vr`), Meta as the OpenXR runtime, fallback with no headset, `--xrsim` for checks without a headset
- `rig` — `XrRig`: anchor = whichever camera the game made current; calibration and recentre; VR-aware player eyes (no bob, pitch or roll); head written back for aiming; snap turn; vignette
- `controls` — `XrPad` controller-to-pad layout; triggers on foot vs mounted; the flight trigger gap; the `XrUi` panel and hand pointer
- `skiing` — Body skiing: lean to steer, crouch to tuck, pole push; merged in `RidePhysics`
- `roadmap` — What phase 1 covers and what comes next (replicated head and hands, items, teleport, asymmetric roles)
