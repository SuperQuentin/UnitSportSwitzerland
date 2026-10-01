# VR setup and launch (#186)

- **Opt-in at launch, never by project setting.** `xr/openxr/enabled` stays off, so flat clients,
  the server and the probes never load the runtime. `xr/shaders/enabled=true` is on: it builds the
  multiview shader variants that stereo needs.
- **Quest 2 over Link / Air Link.** Start the Meta Quest Link app, connect the headset, and make
  Meta the active OpenXR runtime (Link app → Settings → General → OpenXR runtime). Then run
  `<godot> --xr-mode on --rendering-driver vulkan --path . -- --vr [--connect host:port]`.
  Vulkan is the well-tested OpenXR path on Windows; the project default is d3d12.
- **What `XR/XrSession.TryStart` does.** It is called from `ClientWorld._Ready`, right after
  `PlayerInput.Install`. It turns the client into a VR client only if OpenXR actually came up:
  - `UseXR` on the root viewport
  - vsync off
  - render scale 1 (the runtime picks the eye resolution)
  - 90 Hz if the runtime offers it
  - foveation requested (only standalone runtimes honour it)

  `ApplyViewportSettings` leaves scale and vsync alone while `XrSession.Active`.
- **No headset connected.** The Oculus runtime answers `XR_ERROR_FORM_FACTOR_UNAVAILABLE`, and
  Godot shows a blocking alert, then starts flat. In that case `--vr` prints how to start it
  properly.
- **`--xrsim`** runs the whole VR path with no headset, on the monitor: the rig, the UI panel, the
  pad bridge, first-person forcing and the ski merge. The view is an untracked head placed at the
  anchor. Use it for checks:
  `<godot> --path . -- --xrsim --ride skis,10,<abs path>.png`. Give a Windows path, not `/c/...`,
  or the PNG fails to save.
