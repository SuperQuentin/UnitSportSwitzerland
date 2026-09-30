# Polaroid camera: printed photos (issue #101)

Every camera shot prints a physical photo: an inventory item, an album entry, something to stick on
walls or the ground that other players see.

## Print and store — `src/Items/PhotoStore.cs`

- `TakePhoto` still saves the full frame to `user://photos/*.png` (the viewfinder's shots counter),
  then `PhotoStore.Save` makes the print: centre square of the frame, 224 px, on a 256 x 304 white
  card (Polaroid 88 x 107 mm proportions; image area = `PhotoStore.ImageRect` in UV), JPEG q 0.85
  (5-10 KB). **Photo id = first 16 hex chars of the JPEG's SHA-256** (`IdOf`, `IsValidId`).
- Folders: `user://photos/polaroid/<id>.jpg` + `<id>.json` sidecar (`PhotoMeta`: LV95 E/N,
  altitude, local ISO date-time, focal mm, source png; System.Text.Json = culture-invariant) is the
  archive of photos taken here; `user://photo_cache/<id>.jpg` holds prints fetched from the server
  (no sidecar); the server keeps uploads in `user://placed/photos/<id>.jpg`.
  `--photo-dir <abs>` moves the first two (loopback clients must not share a disk).
- `Texture(id)` / `Thumbnail(id)` (40 x 48, slots draw it instead of the icon) are cached;
  `Caption(id)`: no place-name lookup exists, so "E … N … · alt · date · focal".

## Item — `ItemId.Photo = 50`, `ItemUse.Print`, MaxStack 1

- Which print = `ItemStack.Data` (see `inventory`). Film is unlimited.
- Print animation (`ItemController.StartDevelop`, 2 frames after the capture so the camera
  hidden during it is back): in first person at rest `HeldItemVisual.ShowPrint(material)` slides a
  card out of the bottom of the camera viewmodel; at the eye (viewmodel hidden) or third person,
  `PhotoUi` raises a card at the bottom right of the screen. Both use the develop shader
  (`PhotoVisuals.Developing3D/2D`, `develop` 0..1: dark grey-blue → cold faint image → colour) over
  `ItemController.DevelopSeconds` (3 s), then the item is added (`Printed` event; pack full = toast,
  it stays in the album). A second shot during developing finishes the first at once.
- Held mesh: `ItemDefs.HandMaterial(id, data)` is the per-item textured-material path (the shared
  hand material is vertex-colour only); `HeldItemVisual.HeldData` (set by the local controller only)
  picks the print. Remote peers see the blank card: which photo is held is not replicated.
- Use = inspect: `PlayOneShot(Inspect)` + `PhotoUi.Inspect` (large, caption; any key/click closes).
  Aim + Use = stick / take back. Empty hand + Use on your stuck photo = take back.

## Album — `src/Items/PhotoUi.cs` (CanvasLayer 13, over the inventory)

"Photo album" button in the inventory panel. Pack photos first (● marked), then the archive, newest
first; click = viewer; the viewer of an archived photo offers "Print a copy". While the album or
viewer is open `PhotoUi.Blocking` makes `InventoryUi` ignore input; both register with `UiFocus`.

## Sticking — `PhotoVisuals.StickTransform`, `PlacedKind.Photo`

Ray from the camera (reach like the flag). Wall: flush, top edge up; ground/ceiling (|normal.Y| >
0.7): laid flat, top edge away from you. Ghost = translucent unshaded card while Aim is held.
`RequestPlace(Photo, transform, payload = id)` after `PhotoTransfer.Upload(id)`; refusal gives the
print back. Factory `PhotoVisuals.Placed`: card 1 mm proud of the surface + thin collider, blank card
until the image is here.

## Sharing — `src/Items/PhotoTransfer.cs`, `World/PhotoTransfer`

Owner uploads the JPEG in 16 KB reliable RPC chunks (cap 64 KB), server reassembles per (peer, id),
checks the hash = id, stores it and answers peers waiting for it. A peer drawing a photo it lacks
calls `Ensure(id)` once; the server sends the chunks (or queues the request until the upload lands).
Client checks the hash, stores in the cache, raises `PhotoTransfer.Arrived` → the factory swaps the
material. Late joiners: the join snapshot draws → `Ensure`. Not done: the server never deletes
uploaded prints; no per-peer upload quota beyond the size cap.

## Checks

- `<godot> --path . -- --ride foot,120 --view first --photocheck --photo-dir <abs>/test_output/photo_probe`:
  offline, real item paths, screenshots `test_output/photocheck_*.png` (develop_3d, develop_ui,
  album, inspect, ghost, stuck).
- `tools/placedcheck.sh`: A takes and sticks a Polaroid; B (joining later, own photo dir) fetches
  it by hash, checks the bytes and that the card is textured (`placedcheck_b_photo.png`).
- `--invcheck` has the `ItemStack.Data` cases.
