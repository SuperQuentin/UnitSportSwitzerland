# Raised doors, and the site office's upper containers (#827)

Status: built (`feat/827-raised-doors`, PR #938). Built as planned, but: the walkway is 0.8 m
clear, not 1.0 (1.0 fitted far fewer offices: the zone sits 0.8 m off the hoarding); the stair keeps
0.15 m and the walkway 0.05 m inside the hoarding line, and both off the builder's board, which the
dressing stands after the planner; `DoorIndex.Entry` got a `Level` after all, for probes and a bot's
plans; a new `ShellPart.Grating`. Coverage 240 of 566 real offices (42 %), 7 of 13 generated. Part of #605, split out of #822 (PR #824).

A door's sill can stand above the ground: an entrance on floor k of a plan, its sill at that
floor's height on the facade. The site office uses it for its upper row of containers: an outside
steel stair, a walkway (the gallery) along the upper row's door end, and a door per upper container.

## Where we start from

The outside of a door is already 3D. `DoorSpot.Position.Y` is the real sill, `EntrancePlan.DoorY`
carries it, `DoorLink.Create` puts the facade frame on it (`DoorLink.cs:103`), `DoorIndex.Entry.World`
holds it, and the reach rules already measure height (`DoorIndex.MaxRise`, the VR hand's sill test).
Everything that works in a doorway's own frame (`DoorPortals`, `DoorwayGhosts`, `DoorLeaf`,
`InOutsideDoorway`, `AfterMove`, `Cross`, `ArmThroughDoor`) follows the frames and needs nothing.

The **inside** is pinned to the ground floor: the interior doorway is always at interior Y 0
(`FloorY(Below)`), and the floor's rooms, the opening, the exit test and the validator read
`GroundFloor`. That is the whole change.

### Every place that assumes a ground-floor sill (main at `a3643f4d`)

| Where | What it assumes | Change |
|---|---|---|
| `InteriorLayout.cs:361` | `EntrancePlan.X/Z` doc: "ground floor" | add `Level` (below) |
| `InteriorLayout.cs:494-515` `OpeningOf` | the doorway's cut is on `GroundFloor.Rooms` | rooms of `EntranceFloor(e)` |
| `DoorLink.cs:108` | inside frame at `placement * (e.X, 0, e.Z)` | Y = `EntranceY(e)` |
| `InteriorManager.cs:511` `InsideDoorAt` | Y 0 (server reach `NearDoor`, auto-close `TickDoors:532`) | Y = `EntranceY(e)` |
| `InteriorManager.cs:1380` (InteriorNode, the leaves and shutters) | leaf doorway at Y 0 | Y = `EntranceY(e)` |
| `InteriorManager.cs:1051` `StreetSource` | the street sound at Y 1.2 | `EntranceY(e) + 1.2` |
| `InteriorManager.cs:878` `ExitAt` | `local.Y` within the ground storey | within the entrance's storey |
| `InteriorManager.cs:357-358` `TryDoor` hint | "on the ground floor" | plain "the door(s)" when the plan has a raised entrance; text unchanged otherwise |
| `InteriorValidator.cs:29` | an entrance's doorway is on `GroundFloor` | on `EntranceFloor(e)`, which must exist |
| `InteriorValidator.cs:72` | a flight between every pair of floors | not required up to a floor that has an entrance of its own |
| `InteriorValidator.cs:110-113` | reachability starts from ground rooms only | starts from each entrance's room on its floor |
| `InteriorGenerator.Wings.cs:499` | copies entrances field by field | copy `Level` too |
| `InteriorGenerator.cs:164-230` `Entrances` | facade doors arrive on the ground floor | unchanged: ordinary buildings keep Level 0 (asserted) |
| `DoorCheck.cs:446` `RoomClearAt` | ground-floor rooms | `EntranceFloor(e)` |
| `SiteOffice.cs:185` | `if (level != 0) continue`: no door upstairs | an upper door where the gallery stands |
| `SiteOffice.cs:60-61` `ContainerDoors` = doors less cabins | every non-cabin door is a ground container's | explicit counts (below) |
| `SiteShellBuilder.cs:142` | hang `Inward` for `i < ContainerDoors`, else `Outward` | `Outward` for the cabins only |
| `InteriorGenerator.SiteOffice.cs:94-135` | one floor, doorways on floor 0 | a second floor (below) |
| `ConstructionCheck.cs:134-135, 174-177, 186` | one door per ground container, one floor, inside at Y 0 | upper doors, two floors, `EntranceY` |
| `InteriorProbe.cs:273, 302` | "standing on the ground floor" after walking in | the entered door's floor |
| `SiteOfficeNetProbe.cs:194` | the room is looked up on `GroundFloor` | the door's floor |
| `DoorIndex.cs:219` `MaxRise` 2.5 m | (not an assumption, a dependency) the ground and upper doors are 2.6 m apart, so from the gallery a ground door is out of reach and back | not relied on (decided, below): the pick prefers the door at the player's level |

Checked and unchanged: `DoorPortals` (frame-relative, `InView`, `KeepOutOfDoorways`, `OpenDoors`),
`DoorwayGhosts`, `DoorLeaf`, `DoorLink.InOutsideDoorway`, `BeforeMove`/`AfterMove`/`Cross`
(`InteriorManager.cs:741-817`), `RequestCross`, `ArmThroughDoor`, `SurfacePoint` (an upper-floor point
maps to its building plus its height, already right), `InteriorNode.Containing` (spans every floor).

## The data change

- **`EntrancePlan.Level`** (int, default 0): storeys above the ground floor, so the entrance is on
  plan floor `Below + Level`. Relative to `Below`, so 0 means the ground floor in every plan, cellar
  or not; a plain floor index would make 0 the cellar of a house that has one.
  `[JsonIgnore(Condition = WhenWritingDefault)]`: a ground entrance serialises **byte for byte as
  today**, and a stored plan without the field reads as 0.
- **`InteriorLayout.EntranceFloor(e)`** = `Below + e.Level`, **`EntranceY(e)`** = `FloorY(EntranceFloor(e))`
  = `Level * StoreyHeight`. Every row above goes through these two; for Level 0 `EntranceY` is exactly
  `0f`, the constant the code uses today.
- **`DoorSpot.Level`** (init, default 0, not in the positional record, not in the key): the fact
  about the door the generator reads (`Entrance(spot, …)` copies it). Equality changes only for a
  raised spot.
- **`DoorIndex.Entry`: no change.** `World` already holds the sill's height and every reach rule reads
  it. Nothing outside asks for a door's floor.
- **`SiteOfficeDoor.Level`** (pure, tier 0) carries it from the office layout.
- **Keys.** An upper door is the site building's next slot: ground containers `0..c-1`, cabins
  `c..c+t-1` (unchanged), upper containers `c+t..c+t+c-1`. `DoorKey` text, `BuildingKey`, the plan key
  and every existing slot stay as they are.
- **RPCs.** None added, none changed (`RequestDoor`, `SetDoor`, `RequestPlan`, `PlanFor`,
  `RequestCross`, `DoorRefused`). An upper door is a 4-part key like any extra slot.
- **`InteriorLayout.CurrentVersion` bump** (the next free number at merge, 28 today): required. A
  stored office plan is matched to the solid only, so without it the v27 plans keep no upper entrances
  and the upper doors read as locked forever (`construction-sites`, "The site office"). As with every
  bump, every stored plan regenerates and loot records stamped with the old version reset.
- **`Handshake.Protocol` bump** (the next free number, 38 today): the plan bytes on the wire now carry
  `Level`. An old client would drop the field and put an upper doorway on the ground floor of a plan
  it is standing in. `protocol-handshake`: bump on any wire change.

### Ground doors stay byte-identical: how it is proven

- Keys and RPCs: no code path changes them (above); `DoorKeyTests` unchanged.
- Plans: with Level 0, `EntranceY` is `0f` and `EntranceFloor` is `Below`, the values used today, and
  the JSON has no new field. **Golden digest in `--doorcheck`**: before phase 1, on `main`, write
  `tests/door-baseline.txt` (per synthetic building of `--doorcheck` and per door of a fixed generated
  village: key, `DoorSpot` fields, and its entrance's X/Z/In/Width/top, the inside frame's origin, and
  a hash of the plan JSON less `Version`). The check fails on any line that differs. Run on main and on
  the branch: identical.
- Site doors: `SiteOffices.Plan` appends the upper doors and the gallery after everything it makes
  today. Tier 0 asserts that the first `c+t` doors and the office's existing boxes are equal, field for
  field, to the same site planned with the gallery turned off, on every site of `SiteOfficeTests`.
- Loot: not identical across the bump, as with every bump before it (#822's 27 did the same). The
  site office's ground furniture keeps its indices: the upper floor is furnished after the cabins.

## The site office (in the site frame of `SiteOffices`: u along the containers, w across, doors at +u)

- **Gallery** (the walkway): along the upper row's door end, 1.0 m clear from the trim's face
  (`cu1 + Trim`), its deck's top **exactly the upper sill**, `g + ContainerHeight` (2.6 m): chequer
  plate 0.1 m thick, underside 2.5 m, so 0.4 m over the ground doors' heads. It spans the row and,
  on the stair's side, 1.0 m past it as the stair's top landing. Railing 1.1 m (top and mid rail, toe
  board) on the outer edge and the free end. Carried by two diagonal braces per container back to its
  face, drawn only, so it has no footprint on the ground. Parts: `Deck`, `Tube` (or one appended
  `ShellPart.Steel` if they read wrong).
- **Stair**: along the outer long face of the end container, rising from the back to the front
  corner onto the landing. 13 risers of 0.2 m, 12 goings of 0.24 m (2.88 m run, 42°; `FacetFloorDeg`
  is 66°), 0.9 m between stringers, 0.05 m off the container. Treads and stringers drawn, not solid;
  **one ramp quad to stand on** from one going before the foot to the landing, as `ShellPlans.Flight`
  does (a body catches on real risers). Handrail on the open side, the container on the other. Its
  bottom tread is at `g`; on lower ground a plinth box below it, on ground higher than `g + 0.15` the
  side is refused.
- **Where it goes** (open decision 1): `SiteOffices.Plan` tries the stair on the container side
  nearer the gate, then the other. A side is taken when the stair strip and the gallery strip, grown
  0.2 m, lie inside the site's area 0.3 m clear of the hoarding, and touch neither the building's
  keep-out (`Box` + `Scaffold`), the gate lane, another zone, a machine slot (`MachineSize`) nor a
  crane's mast. All of that is in `ConstructionSite` (the lane is recomputed from `Area`, `Front`,
  `Gate`, `GateWidth`; if it cannot be, the planner stores it as a new field that changes no placement).
  No side fits: no gallery, no upper doors, the upper row stays closed as today.
- **Upper doors**: where the gallery stands, each upper container gets the ground door's panel and
  doorway one storey up: sill `(cu1 + Trim, g + ContainerHeight, w0 + DoorFrom + DoorWidth/2)`, 0.9 x
  2.1 m, `Hang = Inward`, `Level = 1`, slot `c + t + i`. `SiteOffice` gets `GroundDoors` (c),
  `CabinDoors` (t), `UpperDoors` (0 or c) in place of `ContainerDoors`, and `Gallery` (null or its
  boxes, its ramp, and the points a walker needs: the stair's foot, its top, the doorstep of each
  upper door).
- **Boxes and collision**: the gallery's boxes and ramp are **yard** boxes, appended to
  `SiteDressingPlan.Boxes` after every zone (no existing index moves) and a new
  `SiteDressingPlan.Ramps`, built by `SiteShellBuilder.Build` into `Faces` (the road cells), **never
  into `OfficeFaces`**: `BeforeMove` lifts the building body for a body standing in an open office
  doorway, and a deck in that body would drop the player through it at the upper door. The upper
  containers' walls already collide with the building body.
- **Upper rooms** (open decision 3): `Floors[1]`, one room per upper container over the one below
  (same X ranges, `OfficeStorey` 2.6 m, so `FloorY(1)` is the sill's 2.6 m and the link stays a plain
  drop), each with its doorway on the front wall (`OpeningKind.Entry`) and its windows; the two upper
  rooms coupled at the back like the ground pair. No inner stair: each upper room is reached by its
  own door. Furnished after the cabins.

## Bots (`new-feature-bots`)

1. Intent: nothing new; climbing is walking (`WalkControls`), the door is E as today.
2. Server path: the door is `ServeDoor`, as every door.
3. Senses: the stair and gallery come from `SiteOffices.Plan(site, ground)`, pure, so a brain can
   read `Gallery.Foot`, `Top` and each upper doorstep from the site plan. (Doors themselves: the
   server's `DoorIndex` is empty, it commits no building meshes; already true of every door.)
4. Collision: **the stair, the ramp, the deck and the railing are site faces, which the server builds
   round a bot** (`ChunkManager` runs `SiteShellBuilder.Build` with `wantCollision`). A bot can climb.
   `MoveTo` walks straight, so it needs the three waypoints above.
5. Skill: none now. Going through any door needs interiors on the server (the existing gap in
   `bot-capability-map`, "doors, building interiors").
6. Map row: "site office stair and gallery (#827)": seam `WalkControls`, server path the body on the
   site collision, sense the office plan's gallery points, skill `MoveTo` by its waypoints, status
   **ok** to climb; the upper doors: gap, as every door.

## Tests

- **Tier 0** (`SiteOfficeTests`): the gallery and the upper doors are deterministic (planned twice,
  equal); each upper door stands on its container's door one `ContainerHeight` up, at the deck's top,
  facing the same way; slots `c+t..`; the ground and cabin doors and the office's existing boxes equal
  the gallery-off plan's; the stair rises `ContainerHeight` in 0.2 m risers, its ramp under 45°; the
  stair and gallery strips stay clear of zones, lane, keep-out, machines and the hoarding; a site with
  no room has no gallery and no upper doors. `DoorKeyTests`, `DoorBudgetTests` unchanged.
- **`--doorcheck`** (quick): the golden digest (every ground door unchanged across house, shed, barn,
  garage, block of flats, shop front, works hall, turned block, generated village); a hand-made plan
  with a Level 1 entrance: `DoorLink.Create` puts its inside frame at `FloorY(1)` and the map is the
  same drop as its ground door's, `OpeningOf` and `RoomClearAt` read floor 1, `ExitAt`'s storey test,
  the validator accepts it with no stair and fails it when the upper floor has neither a stair nor an
  entrance; from the gallery height a ground door is out of reach and from the ground an upper one.
- **`--constructioncheck`**: on every site with a gallery, upper doors among the tile's doors on the
  upper containers' doors, the deck's top at their sill (0 cm), nothing of the yard in the stair,
  gallery or in front of an upper door; the plan has two floors, valid, an entrance per upper door on
  floor 1 lined up (0.0 cm) with a 0.9 x 2.1 m opening. It prints the share of sites with a gallery
  (generated villages, and real tiles where they are loaded with `--chunks`).
- **`--shellwalkcheck`** (quick, physics): a body walks up each gallery's stair from its foot, along
  the deck to every upper doorstep and back down, and never drops under the deck. This is the bot's
  climb on the same collision the server builds.
- **`--interiorcheck --doorkind UnderConstruction --doorlevel 1`** (new flag: the nearest raised door
  of the kind): stand on the gallery, open, walk in onto floor 1, search, out onto the deck (not the
  ground), shut from inside, auto-close.
- **Model viewer**: `SiteShowcase` shows a site whose office has its gallery (`--models`).
- **`--zfightcheck`**: the deck against the containers' trims; new lines go in the baseline.
- **Tier 2** (`tools/siteuppernetcheck.sh`, new, in `tools/lib/checkmap.txt`; the #822 check stays as
  it is): A walks from the stair's foot up to the gallery (walking, no teleport onto it), opens an upper
  door, walks in, takes a stack on floor 1. B, on the deck from the start, sees the door open and A
  through the doorway, walks in and finds the stack gone. C joins late, finds the door open, walks in
  and out, and stands on the deck after. Plus the whole `tools/test.sh net`.

## Decisions (taken with the user, Oct 20)

1. **Gallery placement**: fitted after the site is planned, on a free side, on every site where it
   fits. Existing yards stay as they are; the coverage is measured and reported.
2. **Stair**: along the end container's outer long face.
3. **Upper rooms**: the site manager's office over the office, a drawing office over the mess room,
   joined by a doorway at the back.
4. **Both bumps**: `InteriorLayout.CurrentVersion` and `Handshake.Protocol`.
5. **Bots**: collision and waypoints now, no skill; the capability-map row.
6. **Tier 2**: a new `siteuppernetcheck`; `siteofficenetcheck` unchanged.
7. Raised doors on ordinary buildings stay out of scope.
8. **The door pick does not lean on `MaxRise`** (review): of the doors in reach, one whose sill is
   within `DoorIndex.LevelBand` (1.3 m, half a storey) of the feet is taken before one that is not,
   in `Nearest` and in `FacedEntrance` (faced first, then at the level, then the rest). A VR hand takes
   only the doors whose opening it is in. `--doorcheck` asserts it with the upper sill at 2.6 m and
   again at 2.0 m, inside `MaxRise`. One existing pick changes on purpose: on the Graubünden site the
   office's doorstep with no look (or looking up) now takes the office door at the player's level, not
   the cabin 2 m below; turned to the cabins, E still works a cabin's.

## Phases

1. **Raised entrances in the door system**, no site change: the baseline written on main first, then
   `Level`, `EntranceFloor`/`EntranceY`, every row of the table, the validator, `--doorcheck`'s cases.
   No version or protocol bump yet (no plan has a raised entrance). Quick tier.
2. **The gallery**: `SiteOffices` (fit, stair, deck, railing, upper panels and doors), `SiteDressings`
   and `SiteShellBuilder` (boxes, ramp, faces), tier 0, `--constructioncheck` geometry,
   `--shellwalkcheck`, model viewer, zfight.
3. **Upper doors and rooms**: `OfficeDoors`, the generator's second floor and its furniture, room types
   and loot pools, plan version and protocol bumps, `--constructioncheck` plan, `--interiorcheck
   --doorlevel 1`.
4. **Network and notes**: `siteuppernetcheck`, the net tier, `bot-capability-map`, the notes
   (`door-portals`, `several-doors`, `construction-sites`, a new `terrain/raised-doors`).

## Risks

- **`MaxRise` 2.5 m against a 2.6 m storey**: no longer what keeps a ground door and the upper door
  above it apart for E (decision 8).
- **The deck in the wrong body** drops a player through it in the doorway (above).
- **Coverage**: sites where neither side fits keep a closed upper row. Measured in phase 2; if the
  share is low, decision 1 is revisited.
- **The stair's foot on a slope**: the plinth and the 0.15 m refusal; checked on real sites.
- **Toilets near the door end**: the Graubünden site (LV95 2742681/1254064) has its cabins 1.4 m in
  front of the doors and 2 m lower; the 1.0 m gallery clears them, and the fit test refuses any overlap.
- **Third-person camera and portals by the railing**: `--doorcam` on an upper door in phase 3.
- **A crane's hook path** may cross the gallery as it crosses the containers today: not handled.
- **Stored plans and loot reset** with the version bump, everywhere, as every bump.

## Not in this feature

- Raised doors on ordinary buildings (a stoop, a hillside house's upper entrance): the core supports
  them, but only the site office makes one. `InteriorGenerator.Entrances` keeps Level 0, asserted.
- A bot skill that goes through a door: waits for interiors on the server.
- No new key or interaction: E, the pad and the VR hand work the upper door as any door.
