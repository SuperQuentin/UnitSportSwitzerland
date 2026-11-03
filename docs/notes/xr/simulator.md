# VR without a headset: Meta XR Simulator

- **What it is.** Meta's simulated Quest: an OpenXR runtime with a window of its own where the head
  and the Touch controllers move by mouse, keyboard or a gamepad. The game runs its real VR path on it
  (OpenXR, the refresh rate, the controllers' profile, multiview), which `--xrsim` skips.
- **Install.** Meta Quest Developer Hub, or the standalone *Meta XR Simulator* for Windows from
  developers.meta.com. Installs land in `%LOCALAPPDATA%\MetaXR\MetaXrSimulator\<version>` (or under
  `Program Files` for the standalone app), each with a `meta_openxr_simulator.json`.
- **Run.** `tools/vr-sim.ps1 <godot.exe> [game args]` finds the newest `meta_openxr_simulator.json`
  (`XRSIM_JSON` overrides it), sets `XR_RUNTIME_JSON` for that one process and starts
  `--xr-mode on --rendering-driver vulkan -- --vr [args]`. The system's active runtime (Meta Horizon
  Link) is left alone. The simulator app's own toggle would switch the whole machine to it and turn
  Link off until it is switched back.
- **`--xrsim` instead** (`setup`): no runtime at all, the rig on the monitor from an untracked head. Quick
  and scriptable for checks, but it never reaches OpenXR.
