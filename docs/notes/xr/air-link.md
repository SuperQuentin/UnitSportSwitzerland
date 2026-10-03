# VR over Air Link / Quest Link (#244)

Over Link the PC renders both eyes, **video-encodes** them (H.264/HEVC) and streams them to the
Quest. The two ways the picture breaks:

- **A missed frame.** Under the refresh budget (72 Hz 13.9 ms, 80 Hz 12.5 ms, 90 Hz 11.1 ms,
  120 Hz 8.3 ms; about 2 ms of it goes to the runtime), the runtime drops the game to half rate and
  invents every other frame (ASW). Invented frames warp and smear, worst with big parallax: the
  open landscape, not a room. So "fine in a house, glitchy outside" is a frame-time problem first.
- **Pixels the encoder cannot hold.** Detail that changes every frame (aliased edges crawling
  with the head, dither noise, per-eye vertex snap jitter) blows the bitrate and comes out as
  blocks.

## What the game does in VR

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
- **Stereo door portals:** see `docs/notes/terrain/door-portals.md`.
- **Physics stays at 60 ticks** while the headset runs 72-90 Hz: vehicles and their checks are
  tuned at 1/60 s. Vehicle motion can judder slightly; physics interpolation would be the fix.
- The monitor's *Both eyes* and *Third person* views cost one or two extra world renders: *Off*
  or *First person* leaves the GPU to the headset (`monitor`).

## Settings outside the game

- **Meta Quest Link app** → Devices → Graphics preferences: refresh rate 72 or 90 Hz, render
  resolution at default. Drop to 72 Hz before lowering anything else.
- **Oculus Debug Tool** (`C:\Program Files\Meta Horizon\Support\oculus-diagnostics\OculusDebugTool.exe`):
  - *Asynchronous Spacewarp: Disabled* to diagnose: if the outdoor smearing goes, it was missed frames.
  - *Encode Bitrate (Mbps)* 0 (auto) with *Encode Dynamic Bitrate* on, or a fixed 150-200.
  - If HEVC flickers, try H.264. Leave *Encode Resolution Width* at default.
- **Network:** 5 GHz / 6 GHz Wi-Fi, the PC on cable to the router, the headset in the same room.
- Alternative: Virtual Desktop (AV1 up to ~400 Mbps on Quest 3) with the same in-game settings.

Sources: Meta's ASW and PC performance docs; Godot's OpenXR settings, VRS and OpenXRInterface docs.
