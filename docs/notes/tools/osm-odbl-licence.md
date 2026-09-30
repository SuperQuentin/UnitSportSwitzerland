# OpenStreetMap licence (ODbL) decision

- The optional OSM overlay (`osm-overlay`) brings OpenStreetMap data into the build, which is
  © OpenStreetMap contributors under the Open Database License 1.0
  (https://www.openstreetmap.org/copyright, https://opendatacommons.org/licenses/odbl/1-0/).
- **Attribution**: "© OpenStreetMap contributors" with the copyright link, in the README data
  table and on the in-game Settings > Licenses page. The overlay file carries it in its header.
- **Share-alike**: road tiles (`.road`) built with the overlay are a derived database. If built
  `terrain_chunks` are ever distributed (a download, a release asset), they must be offered under
  the ODbL, with the attribution. A region built without `--layers osm` holds no OSM data and is
  unaffected. **Open point**: the dedicated server streams tiles to clients (`ChunkStreamer`).
  Once #115 writes overlay attributes into `.road`, a public server built with OSM arguably
  "publicly uses" the derived database, and ODbL 4.6 then asks us to offer that database (or the
  way to rebuild it: this repo plus the named PBF) to its players. The owner decides before a
  public server runs OSM-built tiles.
- **Checked 2026-09-30**: `tools/release.sh` ships only the Godot Windows export
  (`build/windows/*` zipped). `terrain_chunks/` has a `.gdignore`, so it is never packed into
  the `.pck`, and the release does not include any tiles. Nothing built with OSM is
  distributed today.
- The layer is therefore off unless named: `--layers osm` or ticking it in MapSetup; `all` does
  not include it.
