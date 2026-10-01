# Railways

- **Railways**: `tlm_oev_eisenbahn` carries gauge (`objektart` Normalspur/Schmalspur),
  `anzahl_spuren`, `zahnradbahn` (rack), `standseilbahn` (funicular), `ausser_betrieb`
  and `auf_strasse`. Rendered as a ballast ribbon plus real rail geometry
  (`RoadMeshBuilder.AppendRails`) at 1.435 m / 1.0 m gauge, doubled for two-track lines.
  Sleepers are a shader stripe, not geometry — at 0.65 m spacing they would cost tens of
  thousands of triangles per km for a few pixels.
- **Rails embedded in the road (#124)**: the network stage (`tools/RoadGen/Network/RailRoadOverlap.cs`)
  cuts every ground-level rail (not bridge, tunnel or funicular) where its centreline runs inside a
  carriageway (class Lane or wider, or Platz; raw untrimmed lines, so junction areas count). That
  piece (+1 m past the centreline's exit) is flagged ATTR `Embedded`: no ballast, no raised rails
  (`RoadMeshBuilder` skips it), `RailGroove` paint per rail instead (dark 0.16 m line, gauge and
  track offsets), at road height; the rail blends back to its own height over 8 m. The road's own
  paint is cut out of the track zone (outer rail + 0.5 m; a dashed line resumes on its next dash).
  Collision is unchanged in kind: the embedded rail sits at the road's height, so the road blend
  sees no step. Trains: `LaneGraph` lowers embedded edges by 0.16 m (raised-rail height less the
  paint lift), so a train (lift 0.2) rolls on the grooves; it steps 16 cm at the road edge, where
  the raised rails end. Rails have their own graph layers (`TileRewriter.RailLayer`): before, a
  level crossing was a road junction that trimmed both lines, leaving the track a gap
  (Martigny-Sion: 46 fewer junctions). TLM `auf_strasse` -> ATTR `OnStreet` (1.5 m wider
  tolerance); a run on such a line or longer than 25 m counts as street running, else a level
  crossing. Self-check: `RoadGen --format-check`.
  - Martigny-Sion (513 tiles): 31 level crossings (255 m), 14 street-running runs (247 m),
    92 groove lines, 25 road lines cut. TLM `auf_strasse` there: 1,797 m, almost all industrial
    sidings (`anschlussgleis`) in Sion yards 10-70 m from any TLM road, so they keep ballast; only
    178 m lie in a carriageway, among them the metre-gauge Martigny line (~2572200/1106080).
    No OSM `railway=tram` in the region. One height blend is cut by a tile seam (the neighbour's
    piece of the line does not see the run; a small step there).
