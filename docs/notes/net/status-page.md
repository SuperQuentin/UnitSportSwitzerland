# Status page: live players and downloads beside a deployed server (#740)

- **Server:** `--status-file <path>` (or `UNITSPORT_STATUS_FILE`) adds `Net/StatusFile`. Every 5 s it builds
  `StatusFileData` on the main thread: name, version, world, port, players, max, sorted player names. It writes
  camelCase JSON when that changed, or every 20 s as a heartbeat, plus `online`, `started`, `updated` (Unix s).
  The write runs on a worker, through `<path>.tmp` and a move, so a reader never sees half a file; a slow disk
  skips a round. A clean exit writes `online: false`.
- **Page:** `tools/deploy/web/index.html`, one static file, no build, no deps, dark "alpenglow" look: a full-screen
  muted hero loop, canvas snow, ridge parallax, glass status card; feature rows (picture + clip that plays only
  while on screen), the story trailer (loaded on click, music credited CC BY), download + join cards. Reduced motion:
  no snow, no autoplay. Polls `status.json` every 10 s; a file older than 60 s reads "Offline" (a killed server
  writes nothing). Downloads come from the GitHub API (`releases/latest`) in the visitor's browser: the visitor's
  OS first, `.delta` assets left out, the releases page as fallback (60 unauthenticated calls an hour per visitor IP).
  The join address is the page's host name, plus `:port` when not 7777.
- **Media** (`tools/web-media.sh [trailer dir] [out dir]`, default `test_output/web-media`, ~38 MB, 34 of them the
  trailer): cut from the trailer renders (`trailer/director`), shot picks hard-coded in the script (re-pick after a
  re-shoot; showcase shots carry burned-in captions, some story shots chat lines). **Never committed** (the trailer's
  music, `royalty-free-music`). Without `media/` the hero shows its drawn sky and ridges, the rows lose their pictures.
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
