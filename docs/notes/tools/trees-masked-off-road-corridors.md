# Trees must be masked off road corridors

- **Trees must be masked off road corridors.** TLM forest polygons cover the whole wood
  including the road cut through it, so scattered trees grow in the carriageway and
  completely hide tunnel portals. `CoverStage.BuildRoadMask` stamps corridors (wider at
  tunnels/bridges) before the scatter. Symptom to recognise: a screenshot that looks like
  "camera inside terrain" is often camera inside a tree canopy.
