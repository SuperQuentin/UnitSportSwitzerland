# BD TOPO's GeoJSON types are not consistent

- **BD TOPO's GeoJSON types are not consistent.** `hauteur` and the altitudes come back as JSON
  numbers; `position_par_rapport_au_sol` — the bridge/tunnel level — comes back as the *string*
  `"1"`. Reading only `JsonValueKind.Number` found **zero bridges**, which is indistinguishable
  from a region that has none. `BdFeature.Number` accepts both.
