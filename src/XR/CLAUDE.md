# VR (`src/XR/`)

OpenXR VR mode (#186), Quest 2 over Link. It is opt-in at launch: one rig follows the game's own cameras, the controllers act as a virtual pad, and the UI is drawn on a panel in the world.

Index only: one line per note in `docs/notes/xr/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/xr`.

- `setup` — VR mode from the menus (Settings → Video, title *Play in VR*: saved and applied by relaunching), launching by hand (`--xr-mode on --rendering-driver vulkan -- --vr`), Meta as the OpenXR runtime, fallback with no headset, `--xrsim` for checks without a headset
- `rig` — `XrRig`: anchor = whichever camera the game made current; calibration and recentre; VR-aware player eyes (no bob, pitch or roll); head written back for aiming; snap turn; vignette
- `controls` — `XrPad` controller-to-pad layout; triggers on foot vs mounted; the flight trigger gap; the `XrUi` panel and hand pointer; `XrHands` grabbing the steering wheel and doors
- `monitor` — What the PC screen shows in VR: first person, both eyes (headset frusta), third person (chase cam, own body on a spectator-only layer) or off; F7, `--vrmonitor`; headset-only / spectator-only render layers
- `skiing` — Body skiing: lean to steer, crouch to tuck, pole push; merged in `RidePhysics`
- `roadmap` — What phase 1 covers and what comes next (replicated head and hands, items, teleport, asymmetric roles)
