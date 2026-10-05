# Farm machines (#494)

A tractor, a combine and four implements, built and driven by the trucks' rules
(`player/trucks-buses`): `HeavyCatalog` entries, a `HeavyTrain`, the heavy cockpit, parked trains,
coupling, replication through `Anim`, `TrainPose` and `TrailerCode`.

## Roster

| What | Where | Key figures | Source / assumed |
|---|---|---|---|
| **Fendt 724 Vario** | `HeavyCatalog` 102, `HeavyClass.FarmTractor` | 181 kW (246 hp) max, 1,072 N·m at 1,500 rpm, 9,250 kg, wheelbase 2.90 m, 540/65R30 front, 650/65R42 rear, 4WD, 40 km/h | Fendt Gen6 data sheet. Assumed: length 5.05 m, cab 3.1 m, 45/55 split, CG, torque curve between points, hitch 0.85 m behind the rear axle at 0.55 m, hydraulic brakes 4.5 m/s², 55° lock. The Vario CVT is **84 close ratios** (`HeavyCatalog.TractorRatios`, `SteplessRatios`: 26.8 down to 1, log-spaced) walked through with no pause (`HeavySpec.Stepless`: automatic only, `HeavyDriveline.HoldScale` 0.15: ~0.29 s a ratio) — no CVT model. Their number is the ratio's sweep rate: a Vario on its default acceleration stage takes ~15-20 s to 40 km/h, which its 181 kW alone (~4 s on 9 t) never would. |
| **Claas Lexion 6800** | `HeavyCatalog` 103, `HeavyClass.Combine` | 340 kW (462 hp), 6.0 m cut, 18 t with the header, 800/65R32 driven front, 600/70R28 **steered rear** (`Steer: -1`), 25 km/h, 10 km/h threshing (`WorkKmh`, `HeavyDriveline.LimitKmh`), tank 140 sacks (7 t) | Claas figures. Assumed: dimensions (10.4 m with the header, 3.3 m body), split, CG, torque curve, the **hydrostat as a stepless box of 64 close ratios** (`CombineRatios`, 12 down to 1; a converter with three ranges did 0-24 in 3.9 s), the tank (the brochure's ~11,000 l would be ~170 sacks). |
| Reversible plough | `TrailerCatalog` 6, `TrailerBody.Plough`, `Coupling.ThreePoint` | 4 furrows, 1.8 m, 25 cm, 1,250 kg | Lemken Juwel class, assumed |
| Seed drill 3 m | 7, `SeedDrill` | 850 kg, 24 coulters | Amazone D9 3000 class, assumed |
| Disc mower 3 m | 8, `Mower` | 750 kg, the bar **out to the right** (`TrailerSpec.WorkOffset` 1.9 m: 0.4-3.4 m from the tractor's centre, its rear wheel's outside at 1.3 m) | Novacat / GMD class, assumed |
| Tipping trailer | 9, `Tipper`, `Coupling.Drawbar` (dolly + turntable body) | 200 sacks (10 t), 4 t empty | Swiss two-axle turntable tipper, assumed |

Colours only, no logos (`HeavyLook`). Meshes: `Avatar/FarmMeshBuilder` (tractor, combine, implements,
tipper body; the tipper's dolly is `TrailerMeshBuilder`'s), rig parts in `HeavyRig` (`HeavyParts.Lift`
raised `LiftRaise` m, `Reel`, `Heap` scaled to the fill, `Auger` on its hinge; wheels spin by their own
radius, `SpinRadius`). Preview: `--avatars 3 out.png --cockpit --heavy 6|7 --side|--outside`.

## Physics

- `Coupling.ThreePoint`: **rigid** — `HeavyTrain.SolvePins` welds the implement's yaw to the
  tractor's (no articulation, `MaxArticulation` 0). No axles: **raised** its whole weight hangs on
  the tractor's hitch (`ComputeLoads`, the front axle unloads: rear 5.1 -> 6.7 t with the plough),
  **lowered** (`Body.Grounded`) it stands on the soil.
- **Draft** (`Body.Draft`, a force against the motion, `Truck.DraftOf` from `MachineLoad`, ASABE
  D497): plough ~28 kN at 7 km/h (tractor tops out at ~12 km/h ploughing), drill ~10 kN, mower
  1 kN + PTO. A raised implement pulls nothing.
- **Soil only** (`FootPlayer.FarmSoil.cs`): before each step the driver's peer looks under the
  lowered implement's bar (`Truck.WorkBarNode`): a field cell (`FarmWork.CellAt`) or grass / woodland
  floor (`Audio.Surfaces.At`, cached per truck: a road wins over the cover) is soil. Over tarmac,
  paving, gravel, rock, water or a deck `Truck.OnSoil` is false: no draft, `WorkTool` None (nothing
  swept), and one toast per stretch, "Raise the plough on the road" (`FarmToast`). Not replicated:
  only the driver's peer steps the train and sweeps.
- **Acceleration** (Sim, flat): the tractor alone 0-39 km/h 17.2 s, top 39.2; with the tipper and
  10 t of grain (23 t) 0-24 11.8 s, 12 % from rest 18.7 km/h after 40 s; the combine 0-24 22.3 s.
- One implement at a time: the tractor has one `Trailer`, the implement or the tipper.

## State and what others see

- **Lowered** = the bus's kneel bit (`Truck.Lowered`, pose flag 8): replicated and parked.
- **Combine tank** in the pose flags (`MachineLoad.TankFlags`: sacks bits 8-15 where a bus has its
  destination, crop 19-23; `FlagMask`), parked in `VehicleState.Flags`; its weight is `Truck.Load`.
- **Tipper's harvest** in its trailer code (`MachineLoad.FarmBits`, `TrailerCatalog.WithTank/TankOf`;
  `Clean` keeps it), so the driven train, the parked train and a dropped trailer carry it.
- **Auger out** = door bit 8 (`Truck.AugerBit`, no cab door uses it).
- Tanks hold one crop; the fraction toward the next sack is the owner's only (`Tank.Partial`).

## Working (local driver's peer only, `FootPlayer.Farm.cs`)

- Lowered and moving: `StepFarm` once per physics tick calls `MachineWork.Sweep` (=
  `FarmWork.Sweep`, or the check's `FakeSweep`) with the bar's middle from last tick to this one
  (`Truck.WorkBarNode`), the width, the tool (plough Plough, drill Sow, mower Mow, header Harvest).
- **Drill**: the first seed in the pack (`FarmTables.CropOf`, hotbar first); `MachineLoad.SeedDue`
  takes whole bags as `Units` add up. No seed: nothing swept, a toast.
- **Mower**: hay bales straight into the pack — **only what fits**: `Give` drops the rest at the
  player's feet, which in a cab is inside the tractor (the bales threw it into the air); the rest
  is left on the field with a toast.
- **Combine**: `MachineLoad.Add` into the tank; a field of another crop is not cut (peeked with
  `FarmWork.CellAt`); full, not cut.
- **Auger** ({destination} / X): out, every 2 s it moves 8 sacks into a **parked** tipping trailer
  whose body is under the spout (`FarmMeshBuilder.AugerSpout`), by claim + park (count even).
- **Co-op**: stopped by a co-op (`FarmMarket.NearCoop`) with a load, the same key delivers it
  (`FarmMarket.Deliver`; the tank empties when francs come back). Stub: never near one yet.
- **On foot**: E at a parked tipper's body or the combine's tank side takes a sack
  (`ItemController.Give`), Shift+E or E held 0.6 s ten (claim + park the vehicle back).
- Picker (admin online, `admin-only-spawning`): the tipper comes with `load` × 200 sacks of wheat.

## Controls

| Action | Keyboard | Pad | VR |
|---|---|---|---|
| couple implement / tipper | H | D-pad ← | R stick ← |
| lower / raise | K | L3 (new pad binding of `kneel`) | the kneel dash poke, L3 |
| auger / deliver | N (or G) | X | the destination dash poke, X |
| take a sack (on foot) | E, Shift+E / hold: 10 | Y, hold: 10 | Y, hold |

Hints: `PlayerFeel` (deliver, take a sack), HUD `DOWN/UP`, `n/cap crop`, `AUGER`. Rows in
`Core/ControlsHelp` ("Farm machines") and `xr/vr-action-map`.

## Trap: the tractor flew

A driven train's ground rays (`FootPlayer.GroundUnder` for the cab's pitch and the sections) read a
**dropped implement** under an axle as the road: the cab pitched 20° and its hull, dug into the
ground, threw the tractor up. They now look past players and every vehicle
(`GroundQuery.Under(pastVehicles)`). A just-dropped trailer still ignores the truck that left it
until it is 22 m away (#70): it can be driven through until then.

## Checks

- `--tractorcheck [shots] [trace] --chunks fixture:flat --traffic 0`: numbers (0-39 km/h 17.2 s, top
  39.2 km/h; plough down 12.0 km/h vs 39.2 raised; lowered on tarmac 39.2; drill 36; mower 38, its
  bar 0.4-3.4 m right; combine 24 / 9.5 threshing; who takes what; codes and flags), each implement
  up and down driven ahead, full lock and reversed with the tractor's height within 8 cm, backed
  into each dropped one (stops, no climb), sweeping, seed, hay, the tank from a fake field, the
  auger into a parked tipper, a sack on foot, the parked train; then the lowered plough across the
  flat fixture's **paved strip** (x 130-142 m: no draft, no strokes on it, 17.6 km/h there against
  8.6 ploughing, one toast, works again past it). ~7 min. Shots: `test_output/494-*.png`.
- `--tractorcheck slope --chunks fixture:flat --traffic 0` (its own checkmap row: together they
  outran the quick tier's 600 s): the numbers, then the flat fixture's **ridge**
  (`FixtureCourse.RidgeHeight`: from x 170 m, 50 m up at 15 %, 30 m level, 50 m down, each change of
  grade eased over 10 m): plough, drill and mower, raised and lowered, driven up from the flat, down
  onto it and 40 m across mid-slope at ~15 km/h (braking downhill). Measured: worst 3 cm up (the
  eased foot), 2 cm down, 1 cm across, never off the floor, no section hits, not wrecked. Each run
  takes a fresh tractor (`SetRide` OnFoot, then the tractor: nothing left in the way). ~6 min.
- `--truckcheck` includes both machines (0-24 km/h loaded, alone to just under the top: tractor
  12-24 s, combine 18-40 s; brakes, swept width).
- `tools/tractornetcheck.sh` (tier 2): B sees A's plough down (flags and rig), the parked tractor
  keep it, 77 sacks in A's tipper (code and heap), the parked train keep them, A's combine's 50 sacks.
- Unit: `MachineLoadTests`.

## Not done

- Unloading into a trailer **another player is driving** (only parked trailers); tipping the
  trailer; a header trailer; a real CVT or hydrostat model (no acceleration-stage setting); PTO;
  the mower's offset weight (its CG is still on the centreline for the physics).
- Seed, hay and grain items have no `ItemDefs` yet (the farming core adds them): until then the pack
  stays empty and the check counts what the machines handed over.
