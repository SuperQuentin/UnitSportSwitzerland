# Bots inside buildings (#1330)

A bot is a body the server runs (`architecture`), and a dedicated server built no interior: `InteriorManager` presents
nothing headless, and players' proxies collide with nothing. Phase A of #1330 lets a bot go through a street door,
walk the ground floor and come back out. Bowling's partners on real bodies are phase B.

## The server side (`InteriorManager.Bots.cs`)

- **`BotWantIn(bot, door)`**: plans the building (`GetOrCreate`, as for a player), opens the door (`BroadcastDoor`)
  and builds the interior's collision for that bot. It answers Pending, Ready, or Locked (no doorway behind the door:
  #1766, or no interior at all). Call it every thought while heading in: it keeps the interior.
- **The collision** comes from the plan the server already holds: `InteriorMeshBuilder.Build(layout).Collision` on a
  worker, then a `StaticBody3D` with a back-face `ConcavePolygonShape3D` (as `InteriorNode.AddBody`). Each region a bot
  wants it in gets its own body (#1456: its frame and physics space), placed with `PlacementFor(layout, region.Origin)`
  and moved when that placement changes (a re-anchor). Freed `BotRoomKeepSeconds` (20 s) after no bot is inside or
  asked for it (`TickBotRooms`). A house is 1,100-1,900 triangles.
- **Where a body can walk** (`BotPath`): the same faces baked into a `NavigationMesh` on the worker (a body 0.3 m round,
  1.7 m tall, steps 0.3 m, slopes 50°; cells 0.1 x 0.05 m, which the region's navigation map is set to), as a navigation
  region on that server region's map (`World3D.NavigationMap`), placed with the body. `NavigationServer3D.MapGetPath`
  then goes round furniture, over stairs and past holes, on every floor the stairs join. The first navigation use in the
  project: only the server's bots read those maps. A map knows a region a sync or two after it was placed, so a path
  asked at once comes back empty and is asked again.
  - Why not the room graph alone: the first version routed on `InteriorNav` with straight legs, and a bot walked
    over a stairwell's hole into the cellar (y −3003.9) and was lost; another one jumped onto a cupboard (MoveTo's
    mantle) and stayed on it.
- **`BotCross(bot, id, door)`**: within 1 m of the sill and the door's passable width, the bot goes through the door's
  own map (`DoorLink.Create` in the bot's frame: `ToInside`/`ToOutside`), position, heading and momentum carried, 0.4 m
  past the sill, and its space is broadcast as a player's crossing is (`Broadcast(id, plan)`), so every client knows
  where it is. Both frames' Z point out of the building: outside a bot stands at +Z, inside at −Z.
- **`BotDoorsNear(bot, radius)`**: the server's `DoorIndex` is empty (it builds no building meshes, and one static
  index cannot hold every region's frame), so the doors come from the tiles' own data, `ComputeDoors` on the terrain
  grid as a client's tile build makes them, once a tile, on a worker. Flat-world tiles only; building sites are left
  out (an office is containers up stairs).

## The bot side

- **`InteriorNav`**: one floor's rooms as a graph whose edges are the doorways and arches (≥ 0.6 m), to choose where to
  go. A room's neighbour through an opening is the room half a metre past its centre, so a cut whose `Other` was never
  set still links. Windows and street entries are no ways. `Reachable` (nearest first), `RoomAt`, `Middle`.
- **`BotVisit`** (a skill any brain can run): Find (the nearest door within 150 m not tried) → ToDoor (`MoveTo` the
  doorstep 1.2 m out, from the door's frame once Ready; arrive 0.9 m, a stoop stops a body short) → In (a plain wish
  at the sill: `MoveTo` takes the facade beside a doorway for a wall and follows it away) → Walk (to a room as far in as
  the doorways go that the surface reaches, along `BotPath`'s points straight, never `MoveTo`: its 2.2 m wall ray met a
  wall before nearly every corner of a path and followed it off the path, and its jump at a ledge mantled onto tables;
  no headway in 2 s asks the way again from where it stands) → Linger (4-10 s) → Back (`BotPath` to 0.8 m inside the
  door, from any floor) → Out. A phase over 45 s (the walk to the door gets its length at 1.2 m/s on top)
  gives up: the door is not tried again, and a bot inside goes back out. The give-up line prints the plan point, the
  room, the leg and what a knee-high ray meets.
- **`WanderBrain`**: one goal in six, after a pause, is a visit in play; every goal with `--botsindoors`; never in a
  test run otherwise, so `botcheck` and the others keep a stroller.

## Check

`tools/botindoorcheck.sh` (net): a dedicated server (`--generated-world`) with one bot and `--botsindoors` at a
generated village (2586561,1115543); two clients each see the bot's space become a house's, its body in the interiors'
space standing (not falling), walking at least 2 m inside, then the street again and standing on the ground.

## Not yet

- Upper floors and cellars as goals: the surface goes there by the stairs, but a visit picks ground-floor rooms. Lifts no.
- Inner doors: the server's interior has no leaves, so a bot walks through a shut flat or washroom door; nav takes
  every doorway as open.
- Earth leaves' buildings (stand-in tiles) are not found by `BotDoorsNear`.
- Chairs, loot, venues: the seams are there (`bot-capability-map`); no brain uses them yet.
