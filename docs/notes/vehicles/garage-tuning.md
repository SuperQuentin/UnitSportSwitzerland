# Garage tuning and car doors

- **Tuning** (`Player/CarTuning`, `Vehicles/GarageUi`; #56). A car's garage parts are 15
  independent slots (`TuneSlot`: tyres, wing, front/rear bumper, skirts, scoop, bonnet, paint,
  two-tone lower, rim colour, rim size, ride height, tint, underglow, door style), 4 bits each in
  one `long`. 0 in a slot is **Stock = the catalog look**, so 0 overall is the car as it came;
  every car offers every slot. `CarTuning.Unpack` is the trust boundary: a slot past its options
  decodes to Stock (`VehicleState.FromDict` and `CarTuning.Ride` both go through it).
  `Car(spec, tuning)` keeps `Spec` = the tuned spec (`CarTuning.Apply`: same numbers, new
  `CarBody`) and `Tyres` = the `TyreModel` that replaced the old `HandbrakeGrip`/tyre-C/sustain
  consts. Free; the parts belong to the **car instance**: `FootPlayer.TuningBits` (replicated OnChange
  beside `RideKindId`, so relays and late joiners get it) is reset by `ApplyRide` for any new car
  (the picker, `SetRide`), kept by `EnterVehicle` from `VehicleState.Tuning`, and written back by
  `CaptureVehicle` when parking. A wreck keeps its parts on the burnt shell but can never be
  claimed again, so they are gone.
- **Tyres**: Stock / Drift / Rally / Race / F1 slicks. Cars now get surface grip like the
  motorbikes (`Motorbike.SurfaceGrip`; stock is a road tyre: 0.65 on gravel, 0.5 on grass) —
  except race NPC cars, whose racing line assumes tarmac. `--tuningcheck` (pure numbers, Coupe 86,
  Sim): tarmac peak 0.76 g Drift < 0.84 Rally < 0.92 Stock < 1.03 Race < 1.22 F1; gravel Rally
  0.84 g vs ~0.6 for the rest; a Game drift held on 40% throttle averages 30° on Drift tyres, 20°
  stock, 13° on slicks. Plus the wire form round-trip and clamp. `--driftcheck` is unchanged.
- **Garage menu**: T / D-pad down in a stopped car (< 1 m/s) where `GarageUi.GarageNear(pos)`
  says there is a garage opens it; `GarageUi.Open(player)` is the direct entry point.
  `--tuning` allows it anywhere (tests, screenshots). Live preview (`FootPlayer.SetTuning`
  rebuilds the car and its visual), Done / Revert / All stock, right-drag or right stick orbits
  the chase camera all the way round (`FootPlayer.ShowroomYaw`). T keeps dropping to the fly
  camera everywhere else.
- **Doors** (`CarMeshBuilder.BuildDoor`, `CarRig.DoorsOpen`): the door panels and their windows
  are their own meshes on hinge pivots `DoorL`/`DoorR` (+ `DoorRL`/`DoorRR` on a Sedan), with the
  body behind them set in and dark. Styles Conventional (catalog default), Suicide, Scissor,
  Butterfly, Gull-wing; a four-door's rear doors only swing out. Each eases 0.4 s to its pose.
  Bits: 1 left, 2 right (**the driver's**: right-hand drive), 4/8 rear. G / pad X (`car_door`): on
  foot, the nearest door of a parked car within 3 m; in a stopped car, the driver's own. Doors
  shut themselves above 20 km/h. Getting in, the driver's door opens and shuts behind; getting
  out, the car is parked with it open plus `VehicleState.DriverDoorShuts` (16), and its authority
  shuts it 1 s later — unless the driver had left it open.
- **Network**: `VehicleBody.DoorsOpen` is OnChange in its replication config; its authority (the
  parker) flips it, anyone else asks `VehicleManager.RequestDoor`, which checks the asker's server
  copy is within 3 m of the car's side and forwards to the authority (`DoorToggled`). **Gotcha**:
  a value set in `_Ready` is taken by the synchronizer as the spawn state and only later CHANGES
  are sent — a door opened in `_Ready` and shut a second later was never seen open by anyone.
  Hence the door comes open in the spawn data instead.
- Check (loopback, windowed so the rigs exist): dedicated server + `--garagecheck a` (owner: tune,
  doors at a stop, park, G on foot, re-enter, drive off, change car, wreck), `b` (watches, works a
  door of a's parked car through the server, screenshots) and `c` (late joiner), each
  `--connect 127.0.0.1:<port> --cache <own dir> --at 2518038,1167321`; read the `[garage]` lines.
