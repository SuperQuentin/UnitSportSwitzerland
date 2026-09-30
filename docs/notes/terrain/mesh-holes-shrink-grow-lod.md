# Mesh holes must shrink, not grow, with LOD

- **Mesh holes must shrink, not grow, with LOD.** Dropping a rendered quad when *any*
  full-res cell under it is carved inflates a 6 m portal into a 40 m gash at stride 20.
  Require *all* covered cells to be carved, and skip holes entirely past stride 4.
