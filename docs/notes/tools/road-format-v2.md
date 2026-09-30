# `.road` format v2

- **`.road` format v2** adds junction polygons after the segments, counted in the header word
  v1 left reserved, so every v1 offset is unchanged and v1 files still decode. A region built
  before the rewrite renders exactly as it did. `RoadMeshBuilder.AppendJunction` draws the caps
  with no lane markings — painting them would put back the crossing lines the junction exists to
  remove. **The rewrite is not idempotent and refuses to run twice**: the second pass trims
  already-trimmed roads and replaces the full-size caps with near-zero ones, leaving a hole at
  every junction.
