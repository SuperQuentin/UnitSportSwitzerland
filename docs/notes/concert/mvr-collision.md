# MVR scene objects collide (#974)

- **What.** An imported rig's trusses, decks, stairs and set pieces (`mvr-scene-objects`) are one
  `StaticBody3D` "MvrObjectsBody" beside the stage's "StageBody", built once in `_Ready` from the
  meshes `BuildObjects` decoded (`ConcertRig.Collision.cs`). Players stand on a deck, walk up its stairs,
  bump into a truss tower; beams stop on the set; the crowd is not placed in it.
- **Judged per piece** by its oriented box (its mesh's box through its transform), in
  `Mvr/MvrCollision.cs` (pure, xunit-tested):
  - **nothing** under 0.3 m across (clamps, lamps, bolts) or thinner than 8 cm on two axes (cables,
    pipes, rods): nobody stands on them, and a beam passes;
  - **a convex hull** for a low walkable piece: both footprint sides from 0.8 m, its thinnest side
    from 12 cm, at most 2.1 m tall (a mantle's reach), standing below 3 m. A deck is its block and a
    **flight of stairs its ramp**: the player has no step-up (`FootPlayer.MantleMin`: under 0.45 m
    only floor snapping, which does not climb a riser), and the generated stage's stairs are ramps
    too. The hull is the vertices' extremes along 98 directions (every (i, j, k) in -2..2), so it never
    reaches Jolt's hull vertex limit and costs one pass over the mesh;
  - **its own triangles** (one `ConcavePolygonShape3D`, back faces on, as the stage's) for a tall
    walkable piece, at most 4000 triangles: a set with an arch or a doorway a hull would fill;
  - **its oriented box** (`BoxShape3D`, at least 10 cm thick so a screen's plane stops a walker) for
    the rest: trusses, screens, towers, walls, anything out of reach overhead.
- **Caps**, the biggest pieces (bounds volume) judged first: 1500 boxes, 300 hulls, 30000 trimesh
  triangles. A trimesh or hull past its cap is a box; a box past its cap nothing. Why these: a box or a
  hull is a convex shape Jolt tests in microseconds behind one broad phase, and a static body's shapes
  are one compound; 30000 triangles in one BVH-searched trimesh. Capture's demo show (2251
  pieces, 100918 triangles) comes to 1232 nothing, 999 boxes, 2 hulls (18 points), 18 trimesh pieces of
  9509 triangles, judged in 13 ms (`MVR_SAMPLES=<folder> dotnet test --filter MvrCollision`).
- **Beams.** `ConcertRig.BeamReach` (the per-frame ray of a quarter of the heads, then `VenueReach`):
  the ray still excepts the stage's own body, not the imported one, so a beam stops on a deck, a screen
  or a truss. A ray starting inside a box or hull (a head clamped in a truss's box) ignores it
  (`HitFromInside` off; they are convex), so heads hung in a truss still shine out of it.
- **Crowd.** `StagePlan.UseRig` keeps `ObjectFootprints`: the x/z bounds of every colliding piece
  standing below 2.2 m and rising over 15 cm (a floor plate is stood on, a truss overhead walked
  under); `StagePlan.Blocked` (the crowd's placement, 1.2 m margin) tests them with the stage's boxes.
- **Multiplayer.** Like the stage: client only. The concert is client-side tile dressing
  (`ConcertDirector`), player movement is resolved by each client on its own physics, and the server
  builds no concert, so it has no stage collision and none for imported objects (bots would walk
  through both; `concerts` says so for the stage).
- **Check:** `--concertwalkcheck --world flat` (`ConcertWalkCheck`, quick tier): a hand-made set in
  front of Geneva's stage built by a real `ConcertRig`; a capsule the player's size walks up five
  0.2 m steps onto a 1 m deck and stands on it, another walks into a truss tower and is stopped; a beam
  down on the deck stops at its top; the crowd's plan keeps off the deck.
