# State derived from the course must be re-derived on EVERY path that changes it

- **State derived from the course must be re-derived on EVERY path that changes it.**
  `RacePlayback.SetSnapToRoads` raised `SnapChanged` only from the end of a matching pass, so the
  two paths that return early - turning the toggle off, and turning it back on when everything is
  already matched - left the ribbon drawn from one variant while the avatar ran the other. It does
  not read as a stale ribbon; it reads as **the body being rotated off the path**, which is how it
  was reported. The event is now raised from a `finally`, and `EnsureCinemaPlan` is subscribed to
  it too, or the director keeps cutting to corners belonging to the other variant. Same class of
  bug in the HUD: the camera button's shot name was recomputed only inside `Refresh()`, which
  nothing called on a cut, so it showed whichever shot was running the last time any control was
  pressed. `_camera.CinemaCuts` is now in the change-detection string - the count, not the name,
  because two consecutive cuts can land on shots of the same name.
