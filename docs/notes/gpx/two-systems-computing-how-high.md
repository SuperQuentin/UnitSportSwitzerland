# Two systems computing "how high is this road" independently will not agree to the centimetre, and a lift sized for one will not clear the other

- **Two systems computing "how high is this road" independently will not agree to the
  centimetre, and a lift sized for one will not clear the other.** The GPX ribbon's tread sits
  `TreadLift` above the height `TrackMatcher` interpolated along the `.road` polyline; the
  rendered road tread `RoadMeshBuilder` draws from the SAME polyline adds its own render-time
  offsets on top for reasons that have nothing to do with the recording — `BridgeLift` (0.15 m,
  purely to stop a deck z-fighting the terrain), and a little more at junctions and type-change
  joins where width and height are blended across the seam. None of that is visible to
  `TrackMatcher`, so the base 0.28 m tread lift — sized to clear terrain noise on a DRAPED course
  — was not always enough to clear the render-time offsets on a SNAPPED one, and the ribbon sank
  under the road it was following rather than the ground beneath it. Fixed with a second,
  larger `RoadClearance` margin applied only when `ElevationIsSurface` is true.
