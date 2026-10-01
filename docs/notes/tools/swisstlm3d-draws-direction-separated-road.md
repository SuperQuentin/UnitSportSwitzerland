# swissTLM3D draws a direction-separated road as TWO centrelines, one per carriageway

- **swissTLM3D draws a direction-separated road as TWO centrelines, one per carriageway** — so
  `DefaultWidth`, which describes the whole road, must not be applied to each line. Measured on
  the A9 and its neighbours: the two motorway centrelines run a median **8.1 m** apart while each
  was drawn 11 m wide, a 3 m overlap for the length of every motorway in the country. Fixed by
  `RoadFormat.WidthFor`, which applies `DividedCarriagewayFactor` (0.55) to any `Divided` line;
  `motorway+motorway` overlap went **15,778 -> 6,279 m²**. The residual is real: at an interchange
  the carriageways genuinely converge (25th percentile separation 3.8 m). `railway+railway`
  (~5,100 m²) is untouched, because TLM does not flag parallel tracks as direction-separated.
- **Re-measured for #117** (TLM 2026, Martigny-Sion, every 5 m, lateral distance only): the A9
  carriageways are a median **2.3 m** apart (p90 4.1), not 8.1, and OSM puts each real
  carriageway 3.8 m further out. The network stage now shifts them outward and draws them at
  their real width: `road-widths-lanes-oneway`.
