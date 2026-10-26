# Places: discovery, viewpoints and markers (#1224)

`src/Adventure/Places/`: `PlacesService` (Godot, `World/Adventure/Places`), the pure rules
`DiscoveryRules` / `PlacesRules`, the sections in `PlacesProgress.cs`, `PoiMarkers` and
`PoiMarkerMeshBuilder`, `StampToast`. Plan: `docs/plans/adventure-places.md` 3, 5, 9-11.

## Rules

- **The travel rule is one table**, `PlacesRules.Class(rideKind)`: on foot (walk, run, climb, swim, skis,
  snowboard, paraglider, wingsuit, parachute, pigeon) or a light aircraft (`Helicopter`, `Plane`) find places
  and clear fog. Every other ride kind (cars, motorbikes, bikes, trucks, buses, boats, the airliners, the UFO)
  and `PlacesRules.Aboard` (a deck, a ski lift, a seat in a vehicle that is not a light aircraft) reveal
  nothing. Change the rule there, nowhere else. `PlacesBodies.Of` turns a `FootPlayer` into the body the
  rules see (the host's ride for a passenger, `Down` as out).
- **Server only** (offline the client plays the server, as `AdventureService` does): a 1 Hz tick per peer
  with `Active(peer)` reads its proxy's `NetGlobal`, `PoiCatalogue.Near(200 m)` into a reused list, then
  `DiscoveryRules.Step`: the per-kind radius (the file's `DiscoverRadius`), `dz` for peaks, crosses and huts
  (25 m, on foot only) and passes (30 m), height above ground for the rest, a lake by its outline + 60 m.
  **Two consecutive ticks** in range discover.
- **Jumps**: a 1 s step beyond the ride's speed (`PlacesRules.MaxSpeed`: foot 40 m/s, wings 70, ground
  vehicles 90, flying 320) anchors the body: nothing is found and no fog cleared until it has moved 300 m
  from the landing by itself (`[places] … jumped` in the log). `PlacesService.Arrived(peer, e, n)` does the
  same for sanctioned moves (an accepted teleport invite today, navigation's travel later). A join is no jump.
- **On discovery**: `Found[id] = Stamp(day, mode)`, `Touch`, `PoiDiscovered`, the owner's `Stamped` (toast),
  the fog round it (`max(300 m, radius)`), then `AtlasProgress.Check` (milestones). Ids written are canonical.
- **Quests**: the service sets `QuestEngine.ResolvePoi` (the tracker locates a POI) and
  `PoisByDiscoveryOnly` (a POI reach step advances on `PoiDiscovered` only, so the travel rule holds for
  quests), and calls `Provides("places")` while the map has places (`Withdraws` when the catalogue is empty).
- **Markers** (client, `PoiMarkers`): POIs with `HasMarker` (a `Viewpoint` flag, or a landmark: tier 1 or a
  curated card) within 1.5 km: a cross on crossed peaks and standalone crosses, a telescope at the other
  viewpoints, a yellow stamp post at landmarks, a small Swiss flag over the ones done. One `MultiMesh` each,
  the meshes built once (`PoiMarkerMeshBuilder`, `[Showcase("Adventure")]`), the instances rewritten only when
  the set, its done state, the ground under it or the origin tile change (checked at 1 Hz). Shown while
  `AdventureLog.Adventuring`.
- **Interact**: E / Y within 3 m (`FootPlayer.TryInteract` -> `PoiMarkers.TryInteract`), a VR hand gripping
  there (`TryInteract(byHand)`); the prompt "[E] Look out from X" / "[E] Stamp: X" goes through the interiors'
  prompt line. `RequestInteract(id)` -> `ServerInteract(peer, id)`: active, has a marker, published position
  within 6 m; stamps if new; a viewpoint syncs (`Synced`, its reveal radius or 2 km, `ViewpointSynced`, the
  owner's `Synced` RPC); always `Interacted(peer, key, id)`.
- **Reward** `"reveal"`: `<id or alias>[@km]`, the POI's reveal radius or 2 km.
- **Toast** (`StampToast`): "New stamp: summit / Name · 1 797 m · VS" with a synthesised thump (static
  `Thump`: in the sound player by itself), 3 s, queued; minor places a short line, a burst folds into
  "+N places"; "View synced". Not during a match.
- **Bots** (#837): `Enroll(bot)` gives a server-run bot session progress (`SessionFor`) so its body finds
  places; `ServerInteract(bot, id)` is its viewpoint path; `PoiCatalogue.Near` its sense. No brain uses them
  yet, and the bot director does not enroll bots (`bots/bot-capability-map`).

## Wire (protocol 51)

Client -> server `RequestInteract(id)`. Server -> owner `Stamped(id)`, `Synced(id)`, `FogFull(bytes)`,
`FogDelta(int[] tiles, int[] masks)` (`fog`). Nothing is broadcast. `AssetKind.AtlasFacts = 18` (`atlas`).

## How to check

- `tools/test.sh unit`: `PlacesRulesTests`, `DiscoveryRulesTests` (radii, heights, two hits, jumps,
  arrivals, vehicles), in `tests/UnitSportSwitzerland.Tests/PlacesTests.cs`.
- Quick: `--placescheck --world fixture --systems ui,physics,adventure --pois fixture` (`PlacesCheck`): see
  `commands`. Progress in `test_output/placescheck`; windowed it saves `test_output/places_*.png`.
- Net: `tools/placesnetcheck.sh`: see `commands`.
