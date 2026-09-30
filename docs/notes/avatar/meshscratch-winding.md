# MeshScratch winding: every face clockwise from outside

- **Godot's front face is clockwise** as the viewer sees it; with the default back-face culling a
  face wound the other way is only seen from inside. A primitive wound inside out does not look
  broken from outside (you see its far inner walls, which pass for a solid), but **nothing tucked
  into it is hidden**: a head showed through a full-face helmet, a car's grille through its bumper
  (#54). `Box` was fixed in `effdbbb` (#38); the `Tube` end caps were still facing into the tube
  and were fixed for #54. Tube sides and `Ring` were always right.
- Check: `<godot> --headless --path . -- --meshcheck`. Each primitive's **signed volume** must be
  minus its exact volume (a regular polygon, not a circle, for tubes and rings): one reversed face
  takes its share off twice, so a lone inverted cap fails it, which a mere sign test does not
  (the sides outweigh the caps).
- The looks of figures and vehicles were tuned while boxes were inverted; workarounds from then
  (e.g. `CarRig` squashing the pop-up pods into lids instead of sinking them into the nose) are
  no longer needed but are still in place.
