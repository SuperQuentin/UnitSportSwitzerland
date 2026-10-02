# Ground vehicles sink in deep water (#299)

- **A car, motorbike or truck whose base is deeper under the water surface than its wading depth**
  (`FootPlayer.WadingDepth`: car 0.55 m, motorbike 0.45 m, truck 1.0 m; `WaterField.TryLevelAt`
  at the vehicle) is taken by the water (`FootPlayer.Water.cs`, `WaterPhysics`, before the ride
  step in `RidePhysics`): no drive, the speed bleeds away, it floats 2.5 s low in the water riding
  the waves (draft 55 % of the body height), goes down at 0.9 m/s for 4 s, then `Drowned`: wrecked
  where it went down (`CaptureVehicle(wrecked)` + `Park`), "SUNK!", the driver and everyone aboard
  out at the surface, swimming (#301, `swimming`).
- **No blast and no fire under water**: `VehicleBody` skips `Char`/`Detonate` when it is under the
  water (`Drowned`, every peer asks its own `WaterField`); the wreck settles on the bed.
- **Keyed on the real depth**: a legacy tile's lake is 0.12 m deep, so cars still drive across it on
  low grip as before; only a lake with a bed (the fixture lake, #298's bathymetry) takes a car.
  Low grip in water stays (`Surfaces`: standing more than 5 cm under the still level is `Water`).
- Replicated as any driven vehicle: the owner simulates, others see the pose, then the wreck.
- Trucks: the trailer sections are not stepped while the water has the cab.
- Check: `--watercheck --chunks fixture:lake` (a car wading 0.4 m stays a car; one dropped past the
  drop-off floats, sinks, is wrecked, the driver on foot); `--swimcheck` checks the driver swims.
