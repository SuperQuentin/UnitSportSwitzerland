# VR (`src/XR/`)

OpenXR VR mode (#186), Quest 2 over Link. It is opt-in at launch: one rig follows the game's own cameras, the controllers act as a virtual pad, and the UI is drawn on a panel in the world.

Index only: one line per note in `docs/notes/xr/<name>.md`. Read a note only when the task
touches its topic; search with `grep -ril <word> docs/notes/xr`.

- `setup` — VR mode from the menus (Settings → Video, title *Play in VR*: saved and applied by relaunching), launching by hand (`--xr-mode on --rendering-driver vulkan -- --vr`), Meta as the OpenXR runtime, fallback with no headset, `--xrsim` for checks without a headset
- `rig` — `XrRig`: anchor = whichever camera the game made current; calibration and recentre; VR-aware player eyes (no bob, pitch or roll); head written back for aiming; snap turn; vignette
- `controls` — `XrPad` controller-to-pad layout; triggers on foot vs mounted; the flight trigger gap; the `XrUi` panel and hand pointer; `XrHands` grabbing the steering wheel and doors
- `monitor` — What the PC screen shows in VR: first person, both eyes (headset frusta), third person (chase cam, own body on a spectator-only layer) or off; F8 (F7 is the flaps), `--vrmonitor`; headset-only / spectator-only render layers
- `skiing` — Body skiing: lean to steer, crouch to tuck, pole push; merged in `RidePhysics`
- `air-link` — Streaming to a Quest over Air Link / Link: why it smears (ASW on missed frames, encoder vs aliasing and dither), headset MSAA/scale/VRS settings, PS1 finish off in VR (`xr_smooth`), Link app and Oculus Debug Tool settings
- `vr-action-map` — Every player action with its keyboard / pad binding, its VR way today and the VR target; design rules R1-R6 (hands for physical, one meaning per control, right stick = action pad when mounted, body first, nothing unreachable, prompts per device); coherence findings
- `roadmap` — What phase 1 covers and what comes next (replicated head and hands, items, teleport, asymmetric roles)
