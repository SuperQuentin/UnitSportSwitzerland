# OpenStreetMap licence (ODbL) decision

- The optional OSM overlay (`osm-overlay`) brings OpenStreetMap data into the build, which is
  © OpenStreetMap contributors under the Open Database License 1.0
  (https://www.openstreetmap.org/copyright, https://opendatacommons.org/licenses/odbl/1-0/).
- **Attribution**: "© OpenStreetMap contributors" with the copyright link, in the README data
  table and on the in-game Settings > About tab. The overlay file carries it in its header.
- **Share-alike**: road tiles (`.road`) built with the overlay are a derived database. If built
  `terrain_chunks` are ever distributed (a download, a release asset), they must be offered under
  the ODbL, with the attribution. A region built without `--layers osm` holds no OSM data and is
  unaffected. **Decided (owner, 2026-09-30): the ODbL is accepted.** Since #115 the overlay's
  attributes are written into `.road` v3 and each such tile has the `Osm` header flag
  (`road-format-v3`). Road tiles built with the overlay are ODbL and are provided on request
  (or the way to rebuild them: this repo plus the named PBF), which covers a dedicated server
  streaming them to players (ODbL 4.6). The Licenses page says so.
- **Checked 2026-09-30**: `tools/release.sh` ships only the Godot Windows export
  (`build/windows/*` zipped). `terrain_chunks/` has a `.gdignore`, so it is never packed into
  the `.pck`, and the release does not include any tiles. Nothing built with OSM is
  distributed today.
- The layer is therefore off unless named: `--layers osm` or ticking it in MapSetup; `all` does
  not include it.
