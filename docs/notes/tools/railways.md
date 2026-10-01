# Railways

- **Railways**: `tlm_oev_eisenbahn` carries gauge (`objektart` Normalspur/Schmalspur),
  `anzahl_spuren`, `zahnradbahn` (rack), `standseilbahn` (funicular), `ausser_betrieb`
  and `auf_strasse`. Rendered as a ballast ribbon plus real rail geometry
  (`RoadMeshBuilder.AppendRails`) at 1.435 m / 1.0 m gauge, doubled for two-track lines.
  Sleepers are a shader stripe, not geometry — at 0.65 m spacing they would cost tens of
  thousands of triangles per km for a few pixels.
- **Rails embedded in the road (#124)**: the network stage (`tools/RoadGen/Network/RailRoadOverlap.cs`)
  cuts every ground-level rail (not bridge, tunnel or funicular) where its centreline runs inside a
  carriageway (class Lane or wider, or Platz; untrimmed lines at the #117 planned width and shifted plan, so junction areas count and a
  rail meets the road where it is drawn). That
  piece is flagged ATTR `Embedded`: no ballast, no raised rails
  (`RoadMeshBuilder` skips it), `RailGroove` paint per rail instead (light steel 0.2 m line, the rail head: a dark one vanished in the asphalt; gauge and
  track offsets). The road's own
  paint is cut out of the track zone (outer rail + 0.5 m; a dashed line resumes on its next dash).
  The piece runs on past the road until the ballast's collision core (half its width + 0.3 m)
  clears every carriageway (at most 15 m more): at an oblique crossing a shorter piece let the
  ballast line pull the road edge 13 cm down (seen from a remote car in MP; now smooth). So the
  grooves cross a few metres of flattened verge. Heights: the embedded piece is at the road's height in the file (the collision road blend reads
  it: no bump); the ballast line meets it `RailTop` (0.16 m) lower and blends back to its own
  height over 8 m, so the raised rails (line + 0.18) end flush with the grooves (road + 0.02).
  `LaneGraph` sinks embedded edges by the same 0.16, so a train (lift 0.2) rolls on the grooves.
  Check: `<godot> --path . -- --chunks <dir> --at E,N --trafficcheck[,out.png] --crossing` spawns
  a train on the rail at E,N and fails unless every unit over it has its wheels within 5 cm of the
  groove paint (Martigny 2573090.4,1106156.2: worst 2.2 cm). Rails have their own graph layers
  (`TileRewriter.RailLayer`): before, a level crossing was a road junction that trimmed both
  lines, leaving the track a gap (Martigny-Sion: 46 fewer junctions). TLM `auf_strasse` -> ATTR `OnStreet` (1.5 m wider
  tolerance); a run on such a line or longer than 25 m counts as street running, else a level
  crossing. Self-check: `RoadGen --format-check`.
  - Martigny-Sion (513 tiles): 28 level crossings (360 m), 16 street-running runs (425 m),
    90 groove lines, 25 road lines cut (#114 integration, on the planned widths and Swiss
    paint: 28 crossings, 354 m, 8 road lines cut). TLM `auf_strasse` there: 1,797 m, almost all industrial
    sidings (`anschlussgleis`) in Sion yards 10-70 m from any TLM road, so they keep ballast; only
    235 m lie in a carriageway, among them the metre-gauge Martigny line (~2572200/1106080).
    No OSM `railway=tram` in the region. One height blend is cut by a tile seam (the neighbour's
    piece of the line does not see the run; a small step there).
