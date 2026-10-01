# VR setup and launch (#186)

- **Opt-in at launch, never by project setting.** `xr/openxr/enabled` stays off, so flat clients,
  the server and the probes never load the runtime. `xr/shaders/enabled=true` is on: it builds the
  multiview shader variants that stereo needs.
- **Quest 2 over Link / Air Link.** Start the Meta Quest Link app, connect the headset, and make
  Meta the active OpenXR runtime (Link app → Settings → General → OpenXR runtime). Then run
  `<godot> --xr-mode on --rendering-driver vulkan --path . -- --vr [--connect host:port]`.
  Vulkan is the well-tested OpenXR path on Windows; the project default is d3d12.
- **From the menus (the normal way).** Use Settings → Video → *VR mode*, or *Play in VR* / *Leave VR*
  on the title screen. Both ask first, save `GameSettings.VrMode`, and restart the game through
  `XrSession.Relaunch`, because OpenXR only comes up with the engine:
  - Going into VR relaunches with `--xr-mode on --rendering-driver vulkan -- <same user args> --vr`.
  - Leaving VR relaunches with `--xr-mode off`.
  - Run from the editor binary (`godot --path .`), the relaunch passes `--path` too.

  A title launch with `VrMode` saved on relaunches itself into VR once. The relaunch carries
  `--vr`, and a run with `--vr` never relaunches, so it cannot loop. If `--vr` was given but no
  headset answered, the title shows "No VR headset" and turns `VrMode` off. `--vr` and `--xrsim`
  are on `GameShell.UseTitle`'s list of harmless flags, so a VR launch lands on the title, which is
  in the headset too.
- **What `XR/XrSession.TryStart` does.** It is called from `GameShell._Ready`, right after
  `PlayerInput.Install` and before any menu or camera exists, so the title is in VR. The rig is
  added to `/root/Main` deferred. It turns the client into a VR client only if OpenXR actually came up:
  - `UseXR` on the root viewport
  - vsync off
  - render scale 1 (the runtime picks the eye resolution)
  - 90 Hz if the runtime offers it
  - foveation requested (only standalone runtimes honour it)

  `DisplaySettings` leaves scale and vsync alone while `XrSession.Active`.
- **No headset connected.** The Oculus runtime answers `XR_ERROR_FORM_FACTOR_UNAVAILABLE`, and
  Godot shows a blocking alert, then starts flat. In that case `--vr` prints how to start it
  properly.
- **`--xrsim`** runs the whole VR path with no headset, on the monitor: the rig, the UI panel, the
  pad bridge, first-person forcing and the ski merge. The view is an untracked head placed at the
  anchor. Use it for checks:
  `<godot> --path . -- --xrsim --ride skis,10,<abs path>.png`. Give a Windows path, not `/c/...`,
  or the PNG fails to save.
