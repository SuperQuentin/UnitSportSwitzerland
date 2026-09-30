# A mode that owns the screen must drop the anchors of the mode it replaced

- **A mode that owns the screen must drop the anchors of the mode it replaced.** `ClientWorld`
  registers the spectator camera as a streaming anchor at boot and never removed it on entering
  GPX replay, so a run at Veigy streamed a second full 361-tile box around Riddes, 100 km away and
  permanently off camera — and the video exporter waited for it before every frame.
  `ParkExploreAnchor` takes it off for the duration; `ToggleMode` (T) is now refused during replay,
  since swapping underneath it would both steal the camera and put the box back.
