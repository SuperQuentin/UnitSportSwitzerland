# VR over Air Link / Quest Link (#244, #1036)

Over Link the PC renders both eyes, **video-encodes** them (H.264/HEVC) and streams them to the
Quest. The two ways the picture breaks:

- **A missed frame.** Under the refresh budget (72 Hz 13.9 ms, 80 Hz 12.5 ms, 90 Hz 11.1 ms,
  120 Hz 8.3 ms; about 2 ms of it goes to the runtime), the runtime drops the game to half rate and
  invents every other frame (ASW). Invented frames warp and smear, worst with big parallax: the
  open landscape, not a room. So "fine in a house, glitchy outside" is a frame-time problem first.
- **Pixels the encoder cannot hold.** Detail that changes every frame (aliased edges crawling
  with the head, dither noise, per-eye vertex snap jitter) blows the bitrate and comes out as
  blocks.
- **Judder at full frame rate (#1036).** Physics ticked at 60 under a 72 Hz headset: the body,
  and the eye on it, moved on five frames in six (two in three at 90), so the whole view stepped
  while walking or driving, ASW or not. Physics now ticks at the headset's rate.

## What limits a Quest 2 on Air Link (#1036)

- **Budget:** 72 Hz 13.9 ms, 90 Hz 11.1 ms, about 2 ms of it the runtime's and the encoder's.
- **Pixels:** Link's default eye buffers are about 1832x1920 each, ~7 MP for both (3.4x a 1080p
  screen), 4x MSAA on top. Not the limit on a 2080 Ti once the triangles are down.
- **Triangles:** the real limit. A headset draws the scene for each eye, and the Medium table
  puts 1 m quads on the tile underfoot and 2 m on the eight round it: ~19 M primitives a view on a
  Geneva street, against ~10 M with Low.
- **The monitor:** a second view of the world costs its triangles again, whatever its resolution.
- **Encoder:** H.264/HEVC at ~100-200 Mbps over Wi-Fi; at 72 Hz each frame gets 25 % more bits than at 90.

Measured with `--xrsim --xrload` (below), RTX 2080 Ti + Ryzen 5600X, a car on a Geneva street,
12 rings, 150 km horizon, steady state:

| | frame | headset GPU | primitives |
|---|---|---|---|
| before: monitor first person on the window, Medium | 20.5 ms | 9.8 ms | 40.5 M |
| monitor at half size, every other frame | 17.5 ms | 11.0 ms | 32.5 M |
| monitor off | 11.9 ms | 11.0 ms | 19.1 M |
| VR detail Low + the monitor as above, physics 72 Hz | **10.4 ms** | 5.5 ms | 16.6 M (11.5 M on frames without the monitor) |
| the same at 90 Hz | 10.7 ms | 6.1 ms | |

The cockpit mirrors cost ~2 ms more (three small renders in turn, `--mirrors off`); a 40 km
horizon instead of 150 km takes ~1.5 ms off the headset's GPU. `--xrload` draws one mono picture of
both eyes' pixels, not multiview stereo: on the headset expect more vertex work, not less.

## What the game does in VR

- **Refresh rate and physics** (`XrSession.ApplyRefresh`): `VrRefreshHz` (Settings -> Video,
  *VR refresh rate*, 72 default, 80 / 90 / 120) asks the runtime for the nearest rate it offers at
  or under it (`VrRefresh.Pick`, tier 0), live. Physics ticks at the rate the runtime reports
  (`OpenXRInterface.RefreshRateChanged`), through `GameClock.SetTickRate`, which keeps game time
  continuous. Before, the game forced 90 Hz over the Link app's choice.
- **Terrain detail** (`GameSettings.VrDetail`, *VR terrain detail*, Low default): the preset
  `LodPolicy.FromSettings` takes in VR in place of `Detail`.
- **Headset viewport** (`XrRig.ApplyQuality`, its own SubViewport; the project's render settings
  are the window's): MSAA (`VrMsaa`, default 4x), no TAA/FXAA/debanding, `VrRenderScale` as its
  `Scaling3DScale`, VRS foveation (`VrFoveation`, `VrsMode.XR`; GPUs with variable rate shading,
  else ignored). Settings → Video → Virtual reality. OpenXR's own `FoveationLevel` is for the
  Compatibility renderer only, so it is not used.
- **No `xr/openxr/submit_depth_buffer`** (left off): with it, the headset's depth target is the
  OpenXR swapchain image, created without the storage flag. MSAA plus any shader reading depth
  (`hint_depth_texture`: the water, underwater) makes Forward+ resolve depth into it with a compute
  shader: `Image ... needs the TEXTURE_USAGE_STORAGE_BIT`, a null uniform set, and the game dies
  silently once in the world (the title, without water, still works).
- **PS1 finish off** (`xr_smooth` global, set by `XrSession`): no vertex snap, no colour dither,
  colour steps kept (`common/retro.gdshaderinc`, which every PS1 body uses). The dissolve dithers
  (sightline, tree LOD) stay: they are functional.
- **Chunk commits** capped at 2 ms per frame in VR (`ChunkManager.VrCommitBudgetMs`).
- **Realistic+ effects off** (`StyleKit.EffectsOn`): SSAO, SSR and volumetric fog per eye at a
  headset's resolution. A VR relaunch is on the Mobile renderer anyway; this covers a hand launch
  with `--rendering-method forward_plus`.
- **Stereo door portals:** see `docs/notes/terrain/door-portals.md`.
- **Physics at the headset's rate**, not 60: the vehicles step with `delta`; the few fixed
  `1f / 60f` left are predictions (`ThrowAim`, `AutoPilot`, `CarSetup`) and the airliner's
  overspeed timer. Godot's 3D physics interpolation would be the other way, but the game moves
  much of what it draws in `_Process` (the look, the cameras), which interpolation would lag.
- **The monitor** draws every view in its own SubViewport at half the window's size, every other
  frame (`monitor`); *Off* leaves everything to the headset.

## Checking frame time without a headset

`--xrsim --xrload`: the simulated headset is drawn at 3664x1920 (both eyes' pixels) with the VR
settings, the monitor as with a headset, vsync off, and every 5 s
`[xr] load: frame ... ms, headset gpu ... ms, ... M prims, physics ... Hz`. Add `--perflog 45` for
the full CSV (`core/session-perf-log`), `--vrhz` / `--vrdetail` / `--vrmonitor` / `--mirrors` to
compare: `--xrsim --xrload --ride car:4,50 --at 2500300,1118450 --heading 207 --perflog 45`.

## Settings outside the game

- **Meta Quest Link app** → Devices → Graphics preferences: refresh rate 72 Hz (the in-game
  *VR refresh rate* asks for its own, among the rates enabled there), render resolution at
  default. Stay at 72 Hz before lowering anything else.
- **Oculus Debug Tool** (`C:\Program Files\Meta Horizon\Support\oculus-diagnostics\OculusDebugTool.exe`):
  - *Asynchronous Spacewarp: Disabled* to diagnose: if the outdoor smearing goes, it was missed frames.
  - *Encode Bitrate (Mbps)* 0 (auto) with *Encode Dynamic Bitrate* on, or a fixed 150-200.
  - If HEVC flickers, try H.264. Leave *Encode Resolution Width* at default.
- **Network:** 5 GHz / 6 GHz Wi-Fi, the PC on cable to the router, the headset in the same room.
- Alternative: Virtual Desktop (AV1 up to ~400 Mbps on Quest 3) with the same in-game settings.

Sources: Meta's ASW and PC performance docs; Godot's OpenXR settings, VRS and OpenXRInterface docs.
