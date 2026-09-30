# Feel layer

- **Feel layer** (`Player/PlayerFeel`, child of the LOCAL `FootPlayer` only): sound, camera shake,
  speed lines, particles, pad rumble and a small HUD (km/h when mounted, "AIR x.x s" popup after
  >0.7 s airborne). It only **listens** — `FootPlayer` raises `Landed(fallSpeed)`, `Jumped`,
  `WallJumped`, `SlideStarted`, `Impacted(lostSpeed)` and exposes `GroundSpeed`, `Motion`,
  `LastRideInput`, `IsViewing` — so nothing in it can move the player, and it mutes and hides
  itself whenever another camera is on screen. Intensity is `Excitement`: speed against what is
  ordinary *for the current mount* (foot 4.8→9, bike 9→18, skis 9→22 m/s). **All audio is
  synthesised at startup** (`Audio/SfxSynth`: shaped noise → `AudioStreamWav`, loops crossfaded
  so the seam does not click) — the project has no audio files; replace any property with a
  sample to upgrade one sound. **No wind loop**: a synthesised one was tried and removed at the
  user's request — shaped noise reads as hiss, not air; wind needs a real recording. Shake goes through `Camera3D.HOffset/VOffset` (trauma², decaying),
  which no camera placement code writes, so it never fights the rigs. Speed lines are
  `shaders/speed_lines.gdshader` on a CanvasLayer at 4. Settings → Feel: volume, shake, speed lines.
