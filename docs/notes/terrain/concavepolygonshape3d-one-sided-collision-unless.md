# `ConcavePolygonShape3D` is one-sided for collision unless told otherwise, and geometry that "looks right" can still be on the wrong side of that test

- **`ConcavePolygonShape3D` is one-sided for collision unless told otherwise, and geometry that
  "looks right" can still be on the wrong side of that test.** The bridge-deck collision
  (`RoadMeshBuilder.BuildBridgeCollisionFaces`) shipped with the shape genuinely present at
  exactly the right position and height — confirmed by dumping its face vertices — and a
  straight-down `PhysicsRayQueryParameters3D` probe still passed clean through it to the terrain
  metres below, reproducing the exact fall-through the collision exists to prevent. Godot's
  `ConcavePolygonShape3D.BackfaceCollision` defaults to **false**, so a ray or a `MoveAndSlide`
  approaching from the "back" of the triangle winding is not stopped at all — not a near miss,
  a complete pass-through, and nothing about the visual mesh rendering correctly (or the face
  data looking sane on inspection) says anything about which side that is. Set
  `BackfaceCollision = true` on any collision shape a player approaches from a direction its
  winding was not deliberately authored for — a deck walked on from above is exactly that case.
  **This was caught only by actually raycasting the running game with the godot-ai MCP**
  (`game_eval` + `PhysicsRayQueryParameters3D.create`), not by reading the code, not by checking
  the shape's face data, and not by a visual `--shot` — the geometry inspection said everything
  was fine. `BuildingBody`'s collision shape has the same `BackfaceCollision: false` default and
  was not touched — its winding comes from the visual mesh, which had to be correct for
  rendering to look right, so there is no equivalent evidence it is broken, but it has not been
  verified with a raycast either.
