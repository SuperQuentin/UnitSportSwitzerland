# Farm machines (#494)

A tractor, a combine and four implements, built and driven by the trucks' rules
(`player/trucks-buses`): `HeavyCatalog` entries, a `HeavyTrain`, the heavy cockpit, parked trains,
coupling, replication through `Anim`, `TrainPose` and `TrailerCode`.

## Roster

| What | Where | Key figures | Source / assumed |
|---|---|---|---|
| **Fendt 724 Vario** | `HeavyCatalog` 102, `HeavyClass.FarmTractor` | 181 kW (246 hp) max, 1,072 N·m at 1,500 rpm, 9,250 kg, wheelbase 2.90 m, 540/65R30 front, 650/65R42 rear, 4WD, 40 km/h | Fendt Gen6 data sheet. Assumed: length 5.05 m, cab 3.1 m, 45/55 split, CG, torque curve between points, hitch 0.85 m behind the rear axle at 0.55 m, hydraulic brakes 4.5 m/s², 55° lock. The Vario CVT is **sixteen close ratios** walked through with no pause (`HeavySpec.Stepless`: automatic only, `HeavyDriveline.HoldScale` 0.15) — no CVT model. |
| **Claas Lexion 6800** | `HeavyCatalog` 103, `HeavyClass.Combine` | 340 kW (462 hp), 6.0 m cut, 18 t with the header, 800/65R32 driven front, 600/70R28 **steered rear** (`Steer: -1`), 25 km/h, 10 km/h threshing (`WorkKmh`, `HeavyDriveline.LimitKmh`), tank 140 sacks (7 t) | Claas figures. Assumed: dimensions (10.4 m with the header, 3.3 m body), split, CG, torque curve, the **hydrostat as a converter box with three ranges**, the tank (the brochure's ~11,000 l would be ~170 sacks). |
| Reversible plough | `TrailerCatalog` 6, `TrailerBody.Plough`, `Coupling.ThreePoint` | 4 furrows, 1.8 m, 25 cm, 1,250 kg | Lemken Juwel class, assumed |
| Seed drill 3 m | 7, `SeedDrill` | 850 kg, 24 coulters | Amazone D9 3000 class, assumed |
| Disc mower 3 m | 8, `Mower` | 750 kg | Novacat / GMD class, drawn centred, assumed |
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
- One implement at a time: the tractor has one `Trailer`, the implement or the tipper.

## State and what others see

- **Lowered** = the bus's kneel bit (`Truck.Lowered`, pose flag 8): replicated and parked.
- **Combine tank** in the pose flags (`MachineLoad.TankFlags`: sacks bits 8-15 where a bus has its
  destination, crop 19-23; `FlagMask`), parked in `VehicleState.Flags`; its weight is `Truck.Load`.
- **Tipper's harvest** in its trailer code (`MachineLoad.FarmBits`, `TrailerCatalog.WithTank/TankOf`;
  `Clean` keeps it), so the driven train, the parked train and a dropped trailer carry it.
- **Auger out** = door bit 8 (`Truck.AugerBit`, no cab door uses it).
- **Tipper's bin tipped** = the same door bit on the tractor (`Truck.Tipping`: a tractor has no auger),
  so it rides in the pose and parks with the train; the rig turns the bin (`HeavyParts.Tip`, its own
  mesh built about its back edge, the heap inside it) up to `FarmMeshBuilder.TipAngle` (50°) in 3 s.
  A rig rebuilt for a new load shows the first value it is given at once (`HeavyRig.Tipped`).
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
- **Auger** ({destination} / X): out, every 2 s it moves 8 sacks into a tipping trailer whose bin is
  under the spout (`FarmMeshBuilder.AugerSpout`):
  - **parked**: by claim + park (count even);
  - **driven by another player** alongside (`DrivenTipperAt`, `TipperBinHas` on the sections as drawn
    here), both under 3 m/s (`AugerDrivenSpeed`): the trailer's load is its driver's trailer code, so
    the combine's owner offers the batch through the server (`PassengerService.OfferAuger`, in
    `PassengerService.Auger.cs`, like a passenger's door button). The server checks both players'
    copies (a combine, a tractor with a tipper, slow, within 25 m) and passes it to the driver; the
    driver's peer checks the spout over its own bin as it sees the combine (1.5 m margin for the
    lag), takes what fits (`MachineLoad.Transfer`, one crop) into its code and answers; the server
    passes the answer back clamped to the offer, and only then do the sacks leave the tank
    (`AugerTaken`). One offer at a time (5 s timeout); a lost answer leaves them in the tank.
- **Co-op**: stopped by a co-op (`FarmMarket.NearCoop`) with a load, the same key delivers it
  (`FarmMarket.Deliver`; the tank empties when francs come back). One delivery at a time
  (`_deliverWait`, 10 s): a second press never sells the load twice.
- **Tipping** ({destination} / X with a tipping trailer coupled, so no new key): stopped, the bin tips
  up for 7 s (down again early if the tractor drives off). By a co-op that is the delivery: the load is
  sold and the heap empties as the francs come back. Anywhere else nothing is poured (a toast: the
  load stays in the trailer); empty, a toast. The hint at a co-op reads "TIP the trailer"; HUD `TIPPED`.
- **On foot**: E at a parked tipper's body or the combine's tank side takes a sack
  (`ItemController.Give`), Shift+E or E held 0.6 s ten (claim + park the vehicle back).
- Picker (admin online, `admin-only-spawning`): the tipper comes with `load` × 200 sacks of wheat.

## Controls

| Action | Keyboard | Pad | VR |
|---|---|---|---|
| couple implement / tipper | H | D-pad ← | R stick ← |
| lower / raise | K | L3 (new pad binding of `kneel`) | the kneel dash poke, L3 |
| auger / deliver (combine), tip the bin / deliver (tractor with a tipper) | N (or G) | X | the destination dash poke, X |
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

- `--tractorcheck [shots] [trace] --chunks fixture:flat --traffic 0`: numbers (0-39 km/h ~7 s, top
  39 km/h; plough down 12 km/h vs 39 raised; drill 36; combine 24 / 9.5 threshing; who takes what;
  codes and flags), each implement up and down driven ahead, full lock and reversed with the
  tractor's height within 8 cm, backed into each dropped one (stops, no climb), sweeping, seed,
  hay, the tank from a fake field, the auger into a parked tipper, a sack on foot, the parked
  train. Then at a stand-in co-op (`FarmMarket.StandIn`): the server refuses a load 200 m away, seed
  and flour; the tipper tipped on the field (bin drawn up 50°, the pose bit, nothing poured, back
  down); driven to the co-op and tipped: 60 sacks × 25 = 1,500 CHF into the pocket, emptied, a second
  press sells nothing; the combine's 40 sacks the same (1,000 CHF). Shots: `test_output/494-*.png`.
- `--truckcheck` includes both machines (0-24 km/h, brakes, swept width).
- `tools/tractornetcheck.sh` (tier 2): B sees A's plough down (flags and rig), the parked tractor
  keep it, 77 sacks in A's tipper (code and heap), the parked train keep them, A's combine's 50 sacks; then B tows an empty tipper, A's combine alongside augers
  30 sacks of wheat into it (A's tank 0, B's trailer 30 and its heap, each seen by the other), A is
  refused a delivery claimed at the co-op's door from afar, B is refused seed, B drives to the
  stand-in co-op (`--farmcoop E,N` given to the server and both clients) and tips: the server logs
  750 CHF paid, B's pocket +750, A sees B's bin up and the trailer empty.
- Unit: `MachineLoadTests`.

## Not done

- Unloading on the move is allowed (both under 3 m/s) but only checked standing; a header trailer; the mower drawn offset; a real CVT or hydrostat model; PTO.
- Seed, hay and grain items have no `ItemDefs` yet (the farming core adds them): until then the pack
  stays empty and the check counts what the machines handed over.
- A lowered implement drags on any ground, tarmac too; no slope check of the height regression.
