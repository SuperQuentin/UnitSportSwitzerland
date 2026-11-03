# Raised doors (#827)

A door's sill can stand above its building's ground floor: an entrance on floor k of the plan, its
sill at that floor's height on the facade. Only a building site's office makes one (its upper
containers, off the gallery); the door code takes any.

## The data

- **`EntrancePlan.Level`**: storeys above the ground floor (`Below + Level` is the plan floor, so 0
  is the ground floor in a house with a cellar too). `[JsonIgnore(WhenWritingDefault)]`: a ground
  entrance's JSON is byte for byte what it was, a stored plan without it reads 0.
- **`InteriorLayout.EntranceFloor(e)` / `EntranceY(e)` / `EntranceRooms(e)`**: every place that used
  the ground floor for an entrance goes through these. For Level 0 `EntranceY` is exactly `0f`.
- **`DoorSpot.Level`** (init, not in the key) and **`DoorIndex.Entry.Level`** (for probes and a bot's
  plans; reach reads `World`, which already held the sill's height).
- Plan version 28, protocol 38 (the plan bytes on the wire carry the level).

## What takes the entrance's floor

`DoorLink.Create` (the inside frame), `InsideDoorAt` (server reach, auto-close), the interior's
leaves and shutters, `StreetSource`, `OpeningOf`, `ExitAt` (E inside works a door on the floor the
player stands on), the `TryDoor` hint (no "on the ground floor" in a plan with a raised door),
`InteriorGenerator.Wings` (copies it), `DoorCheck.RoomClearAt`, `InteriorProbe`.
**The validator**: an entrance's doorway on its own floor (a floor the plan has not got fails), no
stair needed up to a floor with an entrance of its own, reachability from every entrance's room.

Frame-relative code needed nothing: `DoorPortals`, `DoorwayGhosts`, `DoorLeaf`, `InOutsideDoorway`,
`BeforeMove`/`AfterMove`/`Cross`, `ArmThroughDoor`, `SurfacePoint`.

## E at stacked doors

Of the doors in reach, one whose sill is within `DoorIndex.LevelBand` (1.3 m, half a storey) of the
feet is taken before one that is not (`Nearest`; `FacedEntrance`: faced first, then at the level,
then the rest). A VR hand takes only the doors whose opening it is in. So the ground and the upper
door of a container stack are told apart by the floor the player stands on, never by `MaxRise`
(2.5 m) happening to be shorter than the 2.6 m storey. `--doorcheck` asserts it with the upper sill at
2.6 m and again at 2.0 m. One older pick changed with it: on the Graubünden site (`door-portals`, "The
door faced") the office's doorstep with no look now takes the office door, at the player's level,
rather than the cabin 2 m below; turned to the cabins, E still works a cabin's.

## Proving ground doors unchanged

`tests/door-baseline.txt` (`--doorcheck`: every door of its synthetic buildings and road cases, its
spot, entrance, opening, both `DoorLink` frames and its plan's hash less the version) and
`tests/site-door-baseline.txt` (`--constructioncheck`: every ground and cabin door of the generated
sites, no plan hash). Both are written on `main`'s code with `--door-baseline write` and compared on
every run. The plan hash caught a read-only property (`HasRaisedEntrance`) that System.Text.Json
wrote into every plan: keep plan helpers methods, not properties. When `main` changes the sites
themselves, rewrite the site baseline on `main`'s code (a detached worktree of `origin/main` plus the
digest), never on the branch.

## Checks

`--doorcheck` (raised and stacked cases), `--constructioncheck` (the gallery, the upper doors at the
deck's sill, the 0.0 cm drop, two floors; `--galleryscan <chunks dir>` measures real tiles),
`--shellwalkcheck` (the stair and walkway walked), `--interiorcheck --doorlevel 1`,
`tools/siteuppernetcheck.sh` (net). The site office's side of it: `construction-sites`, "The gallery".
