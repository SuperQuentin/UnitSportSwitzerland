# Protective works and walls

- **Protective works and walls**: `tlm_bauten_verbauung` -> `AvalancheBarrier`/`TorrentWorks`/
  `DryStoneWall` and `tlm_bauten_mauer` -> `Wall`, carried in the `.road` file and extruded
  upward by `RoadMeshBuilder.AppendWall` rather than laid flat. They keep their surveyed Z
  because for a `Schutzverbauung` that Z is the **top** of the structure — measured a median
  2.80 m above our heightfield with a p90 of 5.81 m, the real height range of snow bridges — so
  each is grown from the terrain up to it. Walls and torrent works are digitised much closer to
  the ground (+0.15 to +0.95 m) and are clamped to a per-class minimum, or they would render as
  kerbstones. Region: 5.8k barriers (166 km), 3.7k torrent works, 3.7k dry-stone walls (657 km).
