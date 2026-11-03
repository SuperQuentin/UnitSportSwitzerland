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
  Mail goes through MailKit (`SMTP_PORT` 465 = implicit TLS, otherwise STARTTLS). Rate limits are per IP (the login, writes) and per email (three links an hour). With no `SMTP_HOST` the link goes to the log.
- **Privacy:** a plan and a layout are seen only by their owner and the moderators. The export holds walls and rooms and
  nothing personal. The page redraws an uploaded photo on a canvas before sending it, which drops EXIF (GPS) data and caps it
  at 4000 px. A PDF page is rendered to PNG in the browser (pdf.js), so the server only ever stores PNG or JPEG files, which
  it checks by their first bytes.
- **The footprint is the game's:** `/api/building` reads our own `.bldg` tile (`LAYOUTS_CHUNKS`). It finds the solid that
  carries the address's EGID, else the solid under the point, else the nearest one within 15 m (address points often lie
  on the street side). The claim is keyed by **the tile's EGID**, the one the game looks up, which can differ from the
  address's (the GWR join is spatial, `tools/buildings`). A building with EGID 0 cannot be claimed.
- **Model (`tools/TerrainFormat/HouseLayout.cs`, version 2, unit tested):** drawn in `BuildingOutline`'s frame: x along the
  long side of the footprint's minimum-area rectangle, z across it, metres from its centre (LV95 `e`/`n`, `yawDeg`
  counter-clockwise from east). The base outline comes from the floor triangles' boundary, else a hull. Per storey: rooms
  are **polygons** (`points`, 3 to 64 corners, a `RoomType` name) whose edges are the walls. Doors, windows and arches are
  segments on a wall, at any angle. Stairs are a rectangle square to the frame and the direction they climb.
  `Validate` is shared by the page (through save), the service (save, submit, approve) and the game:
  - rooms: no crossing walls, at least 1 m², no wall under 10 cm, inside the footprint with 0.3 m of slack;
  - no two rooms overlapping by more than 0.1 m² (`Poly.Overlap` samples a 10 cm grid, so walls drawn a few cm off pass);
  - openings on a wall (within 3 cm);
  - stairs inside one room and arriving inside a room of the floor above;
  - a front door on an outer wall of the ground floor, and every room reachable from it through doors, arches and stairs.
- **Editor (`houses.html`):**
  - Tools: Select (V), Room (R) corner by corner, Box room (B), Door (D), Window (W), Arch (A), Stair (S), Ruler (M), Move plan (P).
  - **Room:** click the corners, close on the first corner, by Enter or by a double-click. While drawing, a typed number + Enter puts the next corner exactly that far toward the cursor. Backspace removes a corner, Esc cancels.
  - **Snapping, in order:** corners (this floor, the floors above and below, the outline), walls, 15° steps from the last corner, the 5 cm grid. Alt places freely. A mark shows what it snapped to.
  - **Select:** drag a room to move it (the corner nearest a snap point decides), drag a corner. Double-click a wall to add a corner; Delete removes the selected corner or the item. The selected room shows its wall lengths, "Show every wall's length" all of them.
  - **Ruler:** two clicks per measure, kept with the plan settings (`plan.measures`, up to 40), listed and removable in the panel.
  - **Plan:**
    - Align to building (offered right after an upload): two points on the plan, then the same two on the outline, which snap to its corners. That sets scale, turn and position in one go.
    - Set scale: two points and their real length.
    - A turn field with a quarter-turn button, and opacity.
- **Game (`src/Interiors/InteriorGenerator.House.cs`):** `HouseLayouts` (`--house-layouts <path>` or
  `UNITSPORT_HOUSE_LAYOUTS`, set in `InteriorManager`'s static constructor) is re-read when the file changes, checked at most
  every 5 s. `InteriorGenerator.Generate(fp, b, ...)` asks it for `b.Egid` and goes through the following steps.
  - **Fit:** the layout is turned onto the footprint box by the nearest quarter turn; more than 20° off is refused.
  - **Pieces:** the interior builds rectangles, so `RoomStrips.Cut` cuts each floor's polygons into pieces.
    - Strip lines at every corner's z, shared by all rooms, so two rooms meet after the cut where they met on the drawing.
    - Bands crossed by an angled wall are cut every 0.5 m, which turns that wall into steps. A band where an opening sits on an angled wall is left whole, so the opening fits on one step.
    - Each angled room keeps a **core**, its biggest rectangle, as one piece.
    - A drawing's slop (an overlap, or a gap of up to 15 cm) is split or closed at the middle.
    - A stair's lane is carved out as a piece of its own, so its flight stands in one room.
  - **Joining:** pieces of one room are joined by openings as tall as the room. They are flagged `RoomPlan.Piece`, which exempts them from the 1 m minimum, and only the biggest one is furnished.
  - **Box:** outer walls within 0.6 m of the box move onto it, and pieces are trimmed to it.
  - **Openings:** a drawn door or arch goes on the stretch where pieces of its two rooms meet, and a window on a free side of its room. Each goes nearest to where it was drawn, never more than 0.75 m from it. A floor with no window drawn gets the generator's.
  - **Stairs:** each one becomes a flight, plus a hole in the floor above with rails on its open sides.
  - **Entry:** it goes on a ground-floor room on the front wall: at the drawn front door if that is on the front wall, else as near the real door as the room allows. If no room reaches the front wall, the nearest one grows to it (up to 1.5 m) or gets an entrance passage.
  - **Furnishing:** `Entrances` (extra doors) and `Furnish` run as usual.
  - **Fallback:** the result must pass `InteriorValidator`. If it fails or does not fit, the generated interior stays and the reason is logged (`[interior] house layout ... not used`).
  - **Freshness:** interiors live underground behind the door portal, so the drawing never has to meet the shell's walls. `InteriorLayout.House` holds the layout's hash and `Matches` compares it. An approval, a new version or a withdrawal therefore makes the stored plan stale. A plan already in memory stays until the server restarts. `House` and `Piece` are left out of a generated plan's JSON, which is byte for byte what it was (the door baseline hashes it).
  - **Exact angled walls** (#1197): `MergePieces` makes each room cut into pieces one shaped room again, its walls as drawn (`terrain/interior-outlines`). A room holding a stair keeps its strips.
- **Checks:**
  - `HouseLayoutTests` (unit tier): the frame, the rules on polygons, reachability, the export file, the cut (an L exact, a stair carved, rooms sharing an angled wall still meeting, a drawing a few cm off evened out).
  - `--houselayoutcheck` (quick tier):
    - a box house square, turned 30° and turned 90°;
    - an angled-wall living room and kitchen with a door in the wall: furnished once each, the door found;
    - in every case the validator passes and the mesh builds;
    - a layout 45° off falls back with a warning, and a withdrawal makes the plan stale.
    - `--houselayoutcheck <export> --chunks <dir>` plans an export on its real buildings. The plans are written as SVG to `test_output/houselayout/`.
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
  - **Check the mail settings:** `tools/deploy-linux.sh --smtp-check you@example.ch` runs `LayoutService --smtp-check` on the host with `layouts.env` loaded (connect, log in, send a test mail; port 465 = implicit TLS, 587 or any other port = STARTTLS; prints the exact SMTP failure, never the password; exit 1 on failure).
