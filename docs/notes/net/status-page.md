# Status page: live players and downloads beside a deployed server (#740)

- **Server:** `--status-file <path>` (or `UNITSPORT_STATUS_FILE`) adds `Net/StatusFile`. Every 5 s it builds
  `StatusFileData` on the main thread: name, version, world, port, players, max, sorted player names. It writes
  camelCase JSON when that changed, or every 20 s as a heartbeat, plus `online`, `started`, `updated` (Unix s).
  The write runs on a worker, through `<path>.tmp` and a move, so a reader never sees half a file; a slow disk
  skips a round. A clean exit writes `online: false`.
- **Page:** `tools/deploy/web/index.html`, one static file, no build, no deps, dark "alpenglow" look: a full-screen
  muted hero loop, canvas snow, ridge parallax, glass status card; ten feature rows (picture + clip that plays only
  while on screen; a row's key is its `media/<key>.mp4|jpg`), a people lineup (#998), the trailers (story and
  showcase, tabs, each loaded on click, music credited CC BY), download + join cards. Reduced motion:
  no snow, no autoplay, no idle bob or parallax. Polls `status.json` every 10 s; a file older than 60 s reads "Offline" (a killed server
  writes nothing). Downloads come from the GitHub API (`releases/latest`) in the visitor's browser: the visitor's
  OS first, `.delta` assets left out, the releases page as fallback (60 unauthenticated calls an hour per visitor IP).
  The join address is the page's host name, plus `:port` when not 7777.
- **People** (#998): `media/people-<n>.webp`, 16 figures from the model viewer, four per visual style (PS1, Cartoon,
  Realistic, Realistic+), cut out on a transparent background by `tools/web-people.sh` (`--modelsclear`, one short
  windowed launch each). The page's `PEOPLE` list is in the script's order (style, look, height factor: a crouching
  skier is cropped to the same height as a standing figure). Shown twice: the "Be anyone" lineup with a style filter
  (the section stays hidden until one loads), and a dim crowd in the margins of the other sections, drifting slower
  than the page (hidden under 760 px).
- **Media** (`tools/web-media.sh [trailer dir] [out dir]`, default `test_output/web-media`, ~75 MB, most of it the
  two trailers): cut from the newest render of each film (`trailer/director`; `VERSION_story=v2` pins one), shot
  picks hard-coded in the script (re-pick after a re-shoot; showcase shots carry burned-in captions, some story shots
  chat lines). A row with no trailer shot takes `test_output/clips/web-<key>.mp4` (`general/feature-clips`) when it
  is there (`concert`). **Never committed** (the trailers' music, `royalty-free-music`). Without `media/` the hero
  shows its drawn sky and ridges, the rows lose their pictures, the people section stays hidden.
- **v2 renders** (Oct 2026, #998): pinned to 15 July (`trailer/render-pins`), so no Halloween sun (18:30 sunset) as v1
  had: the story's evening shots from 18 on are night. Kept as is for the page; the hero and rows use its day shots.
- **Deploy** (`general/linux-deploy`): it rides the tiles' Caddy (`TILES=1`). `tools/deploy/Caddyfile` serves
  `$DEPLOY_DIR/web` at `/` (only `/`, `/index.html`, `/status.json`; `status.json` is `no-store`), next to `/tiles/`.
  Setup makes the dir with a caddy ACL; every start install uploads `index.html`, and `WEB_MEDIA` (default
  `test_output/web-media`) to `web/media/` when its listing differs and `start-server.sh` passes
  `--status-file $DEPLOY_DIR/web/status.json`. A host set up before #740 needs `tools/deploy-linux.sh --setup` once
  for the new Caddyfile.
- **Local check:** a `--generated-world` server with `--status-file test_output/web/status.json`, `--swarm 3` to
  fill it, `index.html` copied beside the JSON and `python -m http.server` in that folder.
- **Live** (Oct 2026): `https://unitsport.infrack.ch/`. TLS ends on the infrack edge proxy (195.48.14.221), which
  redirects http to https and passes everything to Caddy `:80` on the host; UDP 7777/7778 are forwarded too. The tiles
  go through the same name (`TILES_URL=https://unitsport.infrack.ch/tiles/` in `tools/deploy.env`), since
  `unit-tiles.infrack.ch` stopped answering from outside. Caddy itself stays plain HTTP (`TILES_DOMAIN` empty).
- **Live map** beside it (#896): `net/live-map` (`map.html`, `players.json`, `antennas-4g.json`), linked from the nav.
