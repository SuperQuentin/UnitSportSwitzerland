# Live map: players on a swisstopo map, 4G coverage by operator (#896)

- **Server:** `--livemap-file <path>` (or `UNITSPORT_LIVEMAP_FILE`) adds `Net/LiveMapFile`. Every 2 s it builds the list
  on the main thread (`ServerWorld.LiveMapPlayers`: every body under `Players` but race NPCs and stowed bodies;
  name from the chat registry, a bot's `BotName`; LV95 `Global`; the ride's label, cached per `RideKind` because
  catalogue rides are numbers). It writes camelCase `{online, updated, players: [{name, e, n, alt, ride, bot}]}`
  when the list changed or every 20 s, on a worker through `<path>.tmp` and a move. A clean exit writes
  `online: false` with no players. The same pattern as `status-page` (`StatusFile`).
- **Page:** `tools/deploy/web/map.html`, static, Leaflet 1.9.4 (unpkg), swisstopo's grey national map in Web
  Mercator (`wmts.geo.admin.ch/.../ch.swisstopo.pixelkarte-grau/default/current/3857/{z}/{x}/{y}.jpeg`) turned
  dark with a CSS filter on the tile pane, the status page's colours. Polls `players.json` every 2 s; a file
  older than 60 s reads "Server offline". Players are named dots (bots grey and smaller) plus a list that flies
  to each. LV95 to WGS84 with swisstopo's approximate formulas, in the page and in the antenna script.
- **4G coverage:** a toggle per operator (Swisscom / Sunrise / Salt), remembered per browser; a link can name
  them: `map.html#4g=Swisscom,Salt`. Each outdoor 4G site is a soft disc of its simulated 2-bar reach by power
  class (600 m / 2 / 3.8 / 4.6 km: 122 + 36·log10(km) path loss from the class's ERP, no terrain), drawn on one
  Leaflet canvas renderer.
- **Antennas:** `tools/deploy/antennas.py [out]` downloads OFCOM's "Locations of mobile phone base stations"
  (`ch.bakom.standorte-mobilfunkanlagen`, data.geo.admin.ch, 27 MB GeoJSON, opendata.swiss terms: attribution,
  credited on the map) and writes `antennas-4g.json` (~350 KB: outdoor sites with 4G, `[lat, lon, power class]`
  per operator; Oct 2026: Swisscom 6,976, Sunrise 4,110, Salt 5,912). It falls back to `curl` when Python's TLS
  fails (a broken Windows certificate store gave `ASN1: NOT_ENOUGH_DATA` here).
- **Deploy** (`general/linux-deploy`): `start-server.sh` gets `--livemap-file $WEB/players.json` beside the status
  file; every install uploads `map.html` and `WEB_ANTENNAS` (default `test_output/web-antennas/antennas-4g.json`,
  fetched again when a week old). The Caddyfile serves `/map.html`, `/players.json` (`no-store`) and
  `/antennas-4g.json` (one day). A host set up before #896 needs `tools/deploy-linux.sh --setup` once for the new
  Caddyfile. The status page's nav links to the map.
- **Privacy:** positions of everyone online are public on the page, as the names already are on the status page.
- **Check:** `tools/livemapcheck.sh` (net tier): a fixture server with `--livemap-file` and `--swarm 2`; the file must
  list both swarm players online inside Switzerland with a fresh time, then nobody once they left.
- **Local look:** a fixture server with `--livemap-file test_output/webmap/players.json`, `--swarm 3` against it,
  `map.html` and the antennas copied beside the JSON, `python -m http.server` in that folder.
