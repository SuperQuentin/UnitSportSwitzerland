# E-bike: the Moustache Samedi 28.4, its low frame, and choosing a bicycle (#1159)

- **Choosing**: every `BicycleCatalog` entry carries its own `RideKind` (`BicycleSpec.Kind`): the Scott is
  `RoadBike` 1, the Samedi `EBike` 203 (protocol 47). The choice replicates as the ride kind, as the
  motorbikes' does, and a parked e-bike stays one (`VehicleState` keeps the kind). The picker's
  **Bicycles** tab lists the catalogue; the road bike also stays among the Mounts. A new bicycle is a
  new `RideKind` and a catalogue entry (`Rideable.Create` builds any `BicycleCatalog.For` kind).
- **The low frame**: `MoustacheSamediOpen` (`RideKind.EBikeOpen` 204, Moustache's "Open"), the same spec `with`
  `FrameStyle = BikeFrameStyle.LowFrame`. The mesh drops the top tube for a low tube from the head tube
  to the seat tube over the motor. Its chart and weight are assumed to be the diamond's, so the fit is
  identical. The picker calls it "E-bike (low frame)". Say "low frame", never a gendered name.
- **"Is it a bicycle"**: `BicycleCatalog.IsBicycle(kind)` (= `For(kind) != null`), never `== RideKind.RoadBike` (feel, interest
  size, race pace): a new catalogue bike needs no edit there. `BicycleCatalog.For(kind)` gives the spec. `RideKind` is in its own file
  (`src/Player/RideKind.cs`) so the unit tests compile the catalogue.
- **The bike**: Samedi 28.4 (2025), M/47, metallic red: Bosch Performance Line Smart (75 N·m), 625 Wh,
  CUES 1×10 11-39 on a 38 t ring, 170 mm cranks, Big Apple 700×50C, NEX-E25 63 mm fork, suspension post,
  rack, fenders, lights, kickstand, 27.2 kg (dealer sheet). The **frame chart is assumed** (Moustache
  prints it only from the shop's script): stack 625, reach 400, head 69.5°, seat 73.5°, chainstay 475,
  BB drop 65, offset 50, giving a 1133 mm wheelbase and 82 mm trail. Measure a real one to replace it.
- **The motor** (`BikePhysics.AssistWatts`, pure, `EBikeTests`): Sport mode, 2.25 × the rider's watts,
  no more than torque × crank speed (a 30 rpm floor) nor 600 W, only while pedalling, fading over the
  last km/h to 25 km/h (`ElectricAssist.FadeBand`). Its push at the wheel is capped at torque through the
  gear (`Torque / (Ratio × r)`). One assist level, no key: a mode switch would be a new action for
  keyboard, pad and VR. The battery is not drained yet.
- **Riding sat up**: `BikeBar.Riser` makes `BikeFrame.Hood` the grip (swept back by `BarReach`, raised by
  `BarDrop`), `Upright` keeps the position on the hoods (no drops, no tuck), CdA 0.50 for the reference
  rider (`BicycleSpec.DragArea`). Balloon tyres: `TyreWidthFactor`, ×1.33 on smooth ground at 50 mm and
  ×√(28/width) off it.
- **Numbers**: in game, `--ride ebike,10 --world flat --profile sim` (180 W) reaches 25 km/h in 6 s with
  the motor at ~400 W, then the motor goes to 0 and the legs alone hold ~27 km/h. The road bike is at
  21 km/h after 10 s. 100 W settles at 24-25 km/h, and 120 W climbs 8% three times as fast as unaided.
  The trace prints `motor +N W`, and the HUD shows `+N W`.
- **Mesh** (`BikeMeshBuilder.TrekkingFrame` / `TrekkingFront`, drawn when a spec has `Assist` or `Kit`): the
  battery's fat down tube with the name band, the motor housing, the suspension fork, fenders (`Fender`:
  plates on an arc), the rack with the tail light, the headlight on the crown, the riser bar with the
  grips, levers, remote and display, the comfort saddle, the kickstand folded. `Cyclist` and the
  cycling rig use the bike's own `BikeFrame.For(spec)` (`HumanMeshBuilder.BuildCyclist`,
  `MountsForCyclist`), and the crank and leg step caches are keyed by frame. Model viewer: `--modelsonly e-bike`.
