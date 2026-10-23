# House layouts: claim your building, draw its floors, approved layouts in the game (#1186)

People want their own house accurate in the game. A page beside the status page and the live map lets
them find their building by address, claim it, draw its interior floor by floor over an uploaded plan and
submit it. A moderator approves it; only approved layouts reach the game, where they replace the
generated interior for that building (by GWR EGID).

## Decisions (asked, Oct 2026)

- **Backend:** a small ASP.NET minimal API on the host, `tools/LayoutService/`, behind Caddy at `/api/`,
  SQLite + uploads on disk. Published self-contained linux-x64 (the host has no .NET runtime), run by systemd.
- **Claim proof:** the moderator judges. Login by an emailed link; one open claim per building; an
  optional note for the reviewer. No postal codes.
- **Scope:** the interior per storey. The exterior stays swissBUILDINGS3D.
- **Import:** trace over an image. PNG/JPG, or a PDF page rendered to PNG in the browser (pdf.js), so
  the server stores images only. Scale it with two clicks and a known length, rotate and move it, then draw over it.

## The drawing model follows the game's

`InteriorLayout` (`src/Interiors/InteriorLayout.cs`) is axis-aligned rooms in a box frame, walls being
room edges, with openings cut in them. The editor draws exactly that, so no new mesher is needed:

- **Frame:** the building's main axis, from the minimum-area rectangle around its footprint (the footprint
  is read from our own `.bldg` tile on the host, so it is the game's outline). The plan view is turned so
  the building is square to the screen. x along the axis, z across it, metres, origin at the box centre (LV95).
- **Per storey** (cellars -2..-1, ground 0, up to the GWR floor count, editable): rooms (rectangle +
  type from the residential `RoomType`s), doors and windows (a segment on a room edge: a shared edge makes
  an inner door, an outside edge a facade door/window), stairs (a rectangle and the way it climbs).
  An L-shaped room is two rectangles joined by an open arch.
- **Checks in the browser and again on the server:** rooms inside the footprint (0.3 m slack), no
  overlaps, openings on an edge, a stair under a free spot of the storey above.

## Layout JSON (`HouseLayout`, plain C#, shared, unit tested)

```json
{ "egid": 954894, "version": 1, "e": 2594004.3, "n": 1120149.1, "yawDeg": 23.5,
  "storeyHeight": 2.7,
  "floors": [ { "level": 0,
    "rooms":    [ { "x0": -4, "z0": -3, "x1": 0, "z1": 3, "type": "Living" } ],
    "openings": [ { "kind": "door", "x0": 0, "z0": -1, "x1": 0, "z1": -0.1 } ],
    "stairs":   [ { "x0": 2, "z0": 1, "x1": 3, "z1": 4, "up": "+z" } ] } ] }
```

## Service (`tools/LayoutService`)

- `POST /api/login {email}` emails a one-time link (SMTP from env; none set = the link goes to the log,
  for local use). `GET /api/login/{token}` sets an HttpOnly, SameSite=Lax session cookie. Rate limited.
- `GET /api/building?egid=&e=&n=`: the footprint (LV95 rings), frame, floors, kind, year, from the `.bldg` tiles.
- `POST /api/claims {egid, note}`, `GET /api/me`, `PUT /api/claims/{id}/layout`,
  `POST /api/claims/{id}/plan` (image, 10 MB, PNG/JPEG by magic bytes), `POST /api/claims/{id}/submit`.
- Admin (`ADMIN_EMAILS`): `GET /api/admin/claims?status=pending`, `POST /api/admin/claims/{id}/approve|reject {reason}`.
- On approve/withdraw it rewrites `house-layouts.json` (all approved layouts by EGID) through a tmp
  file and a move. Mutating calls need a JSON body and the `X-Requested-With` header (CSRF, with SameSite).
- Plans and layouts are private: only their owner and admins read them. Nothing personal goes in the export.

## Page (`tools/deploy/web/houses.html`)

- Leaflet map (the live map's swisstopo look, an aerial toggle), address search (geo.admin.ch SearchServer,
  which gives the EGID), click a building; login; claim; the editor; status of my claims.
- For moderators, in the same page: the queue, the plan under the drawing, approve / reject with a reason.

## Game (phase 2)

- `--house-layouts <path>` (`UNITSPORT_HOUSE_LAYOUTS`), read on the server: interiors are planned on the server.
- In `InteriorGenerator.Generate`, before the room split: a building whose EGID has a layout gets its rooms,
  openings and flights from it, mapped into the footprint box (centre offset, the axis turned by the
  nearest quarter turn), then `Entrances` and `Furnish` as usual. The game's facade door joins whichever
  room it opens into. The stored-plan cache must not mask it (the layout's hash goes into the stored key).
- A layout that fails `InteriorValidator` is logged and ignored: the generated interior stays.

## Phases (status: all three built, #1187; the note `net/house-layouts` is the reference)

1. Done: shared `HouseLayout` + validation (reachability added: a front door, every room reached) + unit tests; the
   service, SQLite or PostgreSQL (asked for during the work); **one** page, `houses.html`, the review queue in it for moderators.
2. Done: the override in `InteriorGenerator.Generate`, `--houselayoutcheck` on a box house turned three ways.
3. Done: Caddy `/api/*` and `houses.html`; the service in a tmux loop (`start-layouts.sh`) like the game server
   rather than a systemd unit, so a deploy needs no sudo to restart it; `deploy-linux.sh` publish + upload. Not deployed yet.
4. Done (asked after the first review): rooms are polygons, with a ruler, wall lengths, typed lengths and two-point plan
   alignment; the game cuts polygons into joined strips around a core. Exact angled walls in the game: #1197.
