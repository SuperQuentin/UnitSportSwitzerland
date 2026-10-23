# House layouts: owners draw their house on the web, approved layouts are its interior (#1186)

- **Flow:** `houses.html` (beside the status page and the live map) finds a building by address (geo.admin.ch
  SearchServer, which gives the address's EGID and LV95 point) or by a click on the map. The person logs in by an emailed
  link, claims the building, draws its floors over an uploaded plan and submits. A moderator (`ADMIN_EMAILS`) approves or
  rejects it with a reason, on the same page. Approved layouts go to `house-layouts.json`; the game plans that building's
  interior from it.
- **Service:** `tools/LayoutService` (ASP.NET minimal API, net8, loopback, behind Caddy at `/api/`). SQLite by default,
  PostgreSQL when `LAYOUTS_DB` is a Npgsql connection string or a `postgres://` URL: the same queries for both (@params,
  RETURNING, ON CONFLICT, a partial unique index); only the schema's id and float words differ. Settings are env vars, listed at
  the top of `Program.cs`. One open claim (draft or pending) per building, five per person. Login tokens and sessions are stored hashed. The cookie is
  HttpOnly, SameSite=Strict and Secure (`COOKIE_SECURE=0` locally). Every state change needs `X-Requested-With: houses`.
  Rate limits are per IP (the login, writes) and per email (three links an hour). With no `SMTP_HOST` the link goes to the log.
- **Privacy:** a plan and a layout are seen only by their owner and the moderators. The export holds walls and rooms and
  nothing personal. The page redraws an uploaded photo on a canvas before sending it, which drops EXIF (GPS) data and caps it
  at 4000 px. A PDF page is rendered to PNG in the browser (pdf.js), so the server only ever stores PNG or JPEG files, which
  it checks by their first bytes.
- **The footprint is the game's:** `/api/building` reads our own `.bldg` tile (`LAYOUTS_CHUNKS`). It finds the solid that
  carries the address's EGID, else the solid under the point, else the nearest one within 15 m (address points often lie
  on the street side). The claim is keyed by **the tile's EGID**, the one the game looks up, which can differ from the
  address's (the GWR join is spatial, `tools/buildings`). A building with EGID 0 cannot be claimed.
- **Model (`tools/TerrainFormat/HouseLayout.cs`, unit tested):** drawn in `BuildingOutline`'s frame: x along the long side of
  the footprint's minimum-area rectangle, z across it, metres from its centre (LV95 `e`/`n`, `yawDeg` counter-clockwise from
  east). The base outline comes from the floor triangles' boundary, else a hull. Per storey: rooms are axis-aligned rectangles
  with a `RoomType` name, and their edges are the walls. Doors, windows and arches are segments on a room edge. Stairs are a
  rectangle and the direction they climb. These are the game's own interior shapes (`InteriorLayout`), so no new mesher was needed. An L-shaped room is two rooms and an arch.
  `Validate` is shared by the page (through save), the service (save, submit, approve) and the game:
  - rooms inside the footprint with 0.3 m of slack, and no overlaps;
  - openings on a wall;
  - stairs inside one room and arriving inside a room of the floor above;
  - a front door on an outer wall of the ground floor, and every room reachable from it through doors, arches and stairs.
- **Game (`src/Interiors/InteriorGenerator.House.cs`):** `HouseLayouts` (`--house-layouts <path>` or
  `UNITSPORT_HOUSE_LAYOUTS`, set in `InteriorManager`'s static constructor) is re-read when the file changes, checked at most
  every 5 s. `InteriorGenerator.Generate(fp, b, ...)` asks it for `b.Egid` and goes through the following steps.
  - **Fit:** the layout is turned onto the footprint box by the nearest quarter turn; more than 20° off is refused.
  - **Box:** outer walls within 0.6 m of the box move onto it, and rooms are trimmed to it.
  - **Openings:** an inner door or arch is cut on both rooms. A window becomes a window opening; a floor with none drawn gets the generator's.
  - **Stairs:** each one becomes a flight, plus a hole in the floor above with rails on its open sides.
  - **Entry:** it goes on a ground-floor room on the front wall: at the drawn front door if that is on the front wall, else as near the real door as the room allows.
  - **Furnishing:** `Entrances` (extra doors) and `Furnish` run as usual.
  - **Fallback:** the result must pass `InteriorValidator`. If it fails or does not fit, the generated interior stays and the reason is logged (`[interior] house layout ... not used`).
  - **Freshness:** interiors live underground behind the door portal, so the drawing never has to meet the shell's walls. `InteriorLayout.House` holds the layout's hash and `Matches` compares it. An approval, a new version or a withdrawal therefore makes the stored plan stale. A plan already in memory stays until the server restarts.
- **Checks:**
  - `HouseLayoutTests` (unit tier): the frame, the rules, reachability, the export file.
  - `--houselayoutcheck` (quick tier): a box house square, turned 30° and turned 90°. The real generator plans the drawn layout, the validator passes it and the mesh builds. A layout 45° off falls back with a warning. A withdrawal makes the plan stale.
  - `tools/layoutservicecheck.sh [chunks] [db]`: login by the logged link, lookup, claim, a second claim refused, save, plan upload (HTML refused, private), submit, the owner cannot approve, the admin approves, the export, withdraw. It ran on SQLite and on a `postgres:16-alpine` container (`postgres://postgres:test@127.0.0.1:55432/layouts`).
- **Local look:** the service with `LAYOUTS_WEB=tools/deploy/web` serves the page too. For example: `LAYOUTS_CHUNKS=<main>/terrain_chunks ADMIN_EMAILS=a@b.ch COOKIE_SECURE=0
  LAYOUTS_WEB=tools/deploy/web dotnet run --project tools/LayoutService`, then open `http://127.0.0.1:5080/houses.html`; the login
  link is in the console. The local tiles are not the nationwide build: the Rue du Rhône 10 (Sion) address lands on a 55 m block.
- **Deploy** (`general/linux-deploy`, `LAYOUTS=1` by default when `TILES=1`):
  - `--build` publishes the service self-contained for linux-x64 (~100 MB) and uploads it to `$DEPLOY_DIR/layouts/app`.
  - Every install writes `houses.html`, `layouts/layouts.env` (0600: SMTP, `LAYOUTS_DB`, admins, public URL) and `start-layouts.sh`, which runs the service in tmux `unitsport-layouts` and restarts it if it stops (log `layouts/layouts.log`).
  - `start-server.sh` starts it, so cron @reboot does too, and gives the game server `--house-layouts $DEPLOY_DIR/layouts/house-layouts.json`.
  - Caddy proxies `/api/*` (body up to 12 MB) and serves `/houses.html`. Set `LAYOUTS_TRUSTED_PROXIES` to the edge proxy (195.48.14.221 for unitsport.infrack.ch) so rate limits see visitors. Without it, Caddy drops their X-Forwarded-For and everyone shares the proxy's limit.
  - A host set up before #1186 needs `tools/deploy-linux.sh --setup` once for the new Caddyfile.
  - **Not deployed yet:** set `LAYOUTS_ADMINS` and the SMTP account in `tools/deploy.env` first.
