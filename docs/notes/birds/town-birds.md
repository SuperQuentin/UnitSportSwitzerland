# Town birds, landing and droppings (#143)

- **Perches from `.bldg`** (`TownPerches.Build`, on a worker, once per tile, through the chunk source
  on both the server and an offline client; forgotten with the trees past 2 km): ridges and flat-roof
  rims (top edges of roof faces, normal |y| > 0.45, one every 2.5 m), eaves (the low horizontal edges of
  sloping roof faces, facing out), window ledges (each 3 m storey of a wall face, 0.18 m out) and
  street spots (3–5 m out from the foot of a wall, never inside a building's flat box). At most 10
  ridge, 10 eave, 8 ledge and 6 street perches a building. Bucketed in 16 m cells like tree tops.
  **No spawn on a tile whose buildings are still being read** (`TrySpawn`): before that, Sion filled
  its whole budget with farm birds first (2 town birds of 32, and they stayed).
- **The town test**: the cover raster reads a village as Farm, so a spawn point is a town when the
  3×3 cells of 64 m around it (a 192 m square) hold ≥ `TownBuildings` (14) real buildings (no barns,
  annexes, garages or sites). There, 75 % of spawns are town flocks, the rest `Pick(Habitat.Town)`
  (garden birds on trees and lawns). Not in water or forest cover.
- **Who** (`BirdLife.TownBirds`, decided, Vogelwarte atlas): feral pigeon (Rock Dove) 40, house sparrow
  22, swift 14 (summer only, by `PresenceIn`), carrion crow 7, black redstart 7 (on ridges), jackdaw 6,
  collared dove 5, blackbird 6, house martin 4, wagtail 3, starling 3, magpie 2, Alpine swift 2, and
  yellow-legged and black-headed gulls (6 each) only if water cover lies within 250 m. Each row says
  how it splits over roof / ledge / street / air. At night nothing flies (swifts out, the rest roost).
- **Tame**: a town bird (`Bird.Town`, the top bit of the snapshot's state byte) flushes at
  `2 + 4·√length` m (a pigeon 4.3 m) instead of `5 + 18·√length` (14.6 m).
- **Landing** (all birds that perch or walk, not swifts, soarers, hoverers or water birds): 6–18 s
  after a flush (5–20 s after spawning in the air) `FindLanding` gives a spot. A flock lands together:
  its first bird picks a point 30–80 m ahead (kept 20 s in `_flockSpots`), the others perch around it.
  Town birds take a roof, ledge or street perch there, others a tree top, else the ground (not water
  or forest). They glide in, braking over the last metres. Birds still leave for good past 240 m.
- **Droppings** (`MaybeDrop`, authority; `Droppings` draws them): a pigeon perched or flying within 80 m
  of someone drops about once in 150 s, and at 0.25/s when a person stands within 1.5 m under it
  (2.5–40 m below): that person is the victim. Online the server sends `Dropping(from, vel, victim)`
  (reliable, rare) to peers within 150 m and to the victim. A client draws a falling speck; with a
  victim it homes on their head and leaves a white blob on them for 25 s (everyone sees it), and
  the victim gets a screen splat, a small camera punch and a toast. Without one the physics ray finds
  the ground, a roof or a car (the splat rides on it); a player it was not meant for is passed
  through. Splats are an alpha-scissor quad (works in the Compatibility renderer, unlike `Decal`) that
  shrinks away after 40 s.
  A player playing the pigeon (#217) drops through the same broadcast, server-checked, with its name (`player/pigeon`).
- **Snapshot rates**: flying every tick (8 Hz), walking and swimming every 4th, perched and dead every
  8th (1 Hz); a puppet is dropped after 4.5 s unheard.
- **Check**: `TOWN=1 [WINDOWED=1] SERVER_ARGS= UNITSPORT_CHUNKS=<dir> tools/birdnetcheck.sh E,N` —
  town birds come, A stands under a pigeon until it drops on A (A's screen), B sees it land on A,
  a tame street bird lets A within its flush distance + 3 m, takes off nearer, and lands again on
  B's screen. Pictures `test_output/birdnet_{A,B}_town_*.png`, `_splat_*` (taken from a clear spot near
  the bird when a wall is in the way: in a town's streets it nearly always is). Passed at Riddes
  (2583250,1113250) and Sion old town (2593900,1120250), also with `SWARM=3`: at Sion 10–12 town
  birds (pigeons, sparrows, crows, a wagtail), a pigeon 7–16 m up on an eave let go on A and B saw it
  land on A, a tame sparrow/crow let A within 7–8 m, took off at 1.4–1.9 m and landed again.
