# Getting in by the door, not by the radius (#261)

- **Before**: E got into any parked vehicle within 3.5 m of the player (`Nearest(pos, EnterReach)`),
  measured to its middle: standing at the boot of one car got you into the one beside it.
- **Now** (`src/Vehicles/VehicleReach.cs`): `Find(player)` returns a `VehicleAim(vehicle, door)`.
  First the view ray (camera, 3.2 m past the chest, excluding the player): if it hits a parked
  vehicle (`VehicleManager.Enterable`: not wrecked, not a lone trailer, not being claimed), the
  door nearest the hit point (`CarRig.NearestDoor`) if the player stands within `DoorReach`
  (1.35 m flat, `CarRig.DoorCentre`) of it, else the door the player stands at. With the view
  elsewhere, the vehicle whose hull (`HullDistance` to `ParkedBox`) is nearest the chest, same door
  rule. No hinged doors (bikes, planes, trucks and buses whose rig is not a `CarRig`): within 1.6 m
  flat of `Ride.EntryPoint` (a cab or bus door), else within `HullReach` (1.3 m) of the hull.
- **E** (`FootPlayer.TryVehicleAt`): a shut door opens (`VehicleManager.ToggleDoor`, the server
  relay checks 3 m from the side as before); an open door, or a door-less machine, is claimed and
  entered (`EnterVehicle` opens the driver's door and shuts them all once seated, as before). While
  a claim is in flight (`VehicleManager.Claiming`) E does nothing more. **G** works the aimed door
  either way. Probes that "get back in" call `FootPlayer.TryGetIn` (E, and E again if it only opened
  the door of an own car).
- **Order of E on foot outdoors**: a pointed dropped item, a pointed radio (panel), the vehicle/door
  at hand, a seat in a driven vehicle (passengers: now within 1.2 m of its hull or 3.5 m of a bus
  door), a radio at the feet, an interior door, the dance.
- **Border**: `ItemController` calls `VehicleReach.Point(Find(...))` every frame when no world item
  is pointed; the `Highlight` border (stencil silhouette, #401) goes on the door's hinge node (`CarRig.DoorPivot`: panel and
  glass) or on the whole drawn machine (`VehicleBody.Visual`). The prompt bar says "Open the door" /
  "Get in the X", plus "Close the door" (G) at an open one.
