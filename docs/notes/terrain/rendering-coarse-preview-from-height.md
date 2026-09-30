# Rendering a coarse preview from the height grid alone is WORSE, despite sounding better

- **Rendering a coarse preview from the height grid alone is WORSE, despite sounding better.**
  Tried it: a separate pass that builds a stride-10 mesh from just the `.terr`. It adds a second
  serialised stage per tile and both stages compete for the same six streaming slots, so the
  full builds starve. Measured 5,746 prims at 6 s where the baseline had finished at 3 s —
  roughly three times slower to a complete world. Removed.
