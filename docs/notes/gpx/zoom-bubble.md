# Zoom bubble

- **Zoom bubble** (`ZoomBubble`, driven from `PlaybackCamera.Step`): whenever the ACTIVE camera
  is far enough from the runner that they are a few pixels (a wide Locked-off tripod, a Free
  camera flown across the valley), a comic speech bubble pops up holding a **live close-up** of the
  runner, its tail pointing at where they are in the main picture. It replaced a red "HERE" arrow,
  which said where the runner was but still left them too small to see. The inset is a 256² 
  `SubViewport` with `OwnWorld3D = false`, so it renders the same streamed world with no extra
  loading (the runner is already an anchor), from a chase camera 4.5 m behind along `Heading`,
  eased and clamped above the ground; its update mode is `Disabled` whenever the bubble is hidden,
  so it costs nothing up close. `CanvasLayer` 6: above `LensLayer` (5) so the barrel distortion
  does not bend it, below the HUD (10). A runner off screen or behind the lens pins the bubble to
  the nearest edge, tail pointing outward. Trigger is pure distance with hysteresis (shows past
  35 m, hides under 25 m). Exported videos include it — the layer draws into the root viewport.
  **HUD Bubble button / `--bubble off`** (`--arrow off` still accepted) turns it off.
