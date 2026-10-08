# Status page: live players and downloads beside a deployed server (#740)

- **Server:** `--status-file <path>` (or `UNITSPORT_STATUS_FILE`) adds `Net/StatusFile`. Every 5 s it builds
  `StatusFileData` on the main thread: name, version, world, port, players, max, sorted player names. It writes
  camelCase JSON when that changed, or every 20 s as a heartbeat, plus `online`, `started`, `updated` (Unix s).
  The write runs on a worker, through `<path>.tmp` and a move, so a reader never sees half a file; a slow disk
  skips a round. A clean exit writes `online: false`.
- **Page:** `tools/deploy/web/index.html`, one static file, no build, no deps. Polls `status.json` every 10 s; a
  file older than 60 s reads "Offline" (a killed server writes nothing). Downloads come from the GitHub API
  (`releases/latest`) in the visitor's browser: the visitor's OS first, `.delta` assets left out, the releases page
  as fallback (the API allows 60 unauthenticated calls an hour per visitor IP). The join address is the page's
  host name, plus `:port` when not 7777. Light and dark follow the system.
- **Deploy** (`general/linux-deploy`): it rides the tiles' Caddy (`TILES=1`). `tools/deploy/Caddyfile` serves
  `$DEPLOY_DIR/web` at `/` (only `/`, `/index.html`, `/status.json`; `status.json` is `no-store`), next to `/tiles/`.
  Setup makes the dir with a caddy ACL; every start install uploads `index.html` and `start-server.sh` passes
  `--status-file $DEPLOY_DIR/web/status.json`. A host set up before #740 needs `tools/deploy-linux.sh --setup` once
  for the new Caddyfile.
- **Local check:** a `--generated-world` server with `--status-file test_output/web/status.json`, `--swarm 3` to
  fill it, `index.html` copied beside the JSON and `python -m http.server` in that folder.
