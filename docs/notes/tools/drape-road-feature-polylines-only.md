# Drape road/feature polylines only after densifying them

- **Drape road/feature polylines only after densifying them.** TLM3D emits vertices only
  where a line changes direction, so straight runs span 50 m+ and the ribbon cuts through
  terrain bumps between drape samples, appearing dashed.
