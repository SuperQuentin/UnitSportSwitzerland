# Engine synth idles stopped

## Rule
- `EngineSynth` stops its `AudioStreamPlayer(3D)` once its level target and its smoothed level
  are both under 1e-4 for 0.2 s (two buffer lengths), and plays it again as soon as the target
  rises. Callers keep calling `Set(...)` every frame as before; never `Stop()`/`Play()` the
  synth's player from outside, and never add a per-frame path that pushes samples while silent.
- Samples go through one reused `_push` array that only grows, pushed as
  `PushBuffer(new ReadOnlySpan<Vector2>(_push, 0, frames))`: never `new Vector2[frames]` per
  frame (the available count changes every frame, so a resize-on-change array is reallocated
  nearly every frame).

## Why
Every parked `VehicleBody` and the player's own idle synths (`PlayerFeel`'s rotor and plane
engine on foot) rendered ~735 silent samples a frame and allocated a new array for them.
#221: part of the gen1 drop in `perf-no-per-frame-allocations` (car HUD run, gen1 146 -> 9 per
minute, with the HUD and StringName fixes). `--exitcheck` logged each parked car's synth
stopping ~1 s after the engine went off, and the player's own synth playing again on the next
mount.

## Same logic, preserved
- No click at either end: the per-sample level ramp (25 ms) is untouched. The stop waits until
  the fade-out has played through the 0.1 s generator buffer; the restart begins from the
  smoothed level, which is ~0, and ramps up.
- After `Play()` the generator playback object is new: `_playback` is re-read from
  `GetStreamPlayback()`. Code that caches the playback elsewhere would push into a dead one.
- The model state (phases, filters) freezes while stopped; at level ~0 that is inaudible.
- `Render` (offline, `--soundcheck`) is unaffected.

## Migrating old code / open branches
- PR #229 deletes `EngineSynth.Player3D`: no conflict beyond the adjacent lines; keep both.
- A branch that changes `_Process` in `EngineSynth`: keep the quiet/stop/play block before
  `GetFramesAvailable()`, and push through the span. Grep:
  `grep -n "new Vector2\[frames\]\|PushBuffer(_push)" src/Audio/EngineSynth.cs` must find nothing.

## How to check
`--exitcheck` (offline) or any ride: engine off, get out, and the synth of the parked car should
stop (temporarily add a `GD.Print` at the stop/play lines, as #221 did, and read the
transitions). By ear: no click when the engine is switched off or on.
