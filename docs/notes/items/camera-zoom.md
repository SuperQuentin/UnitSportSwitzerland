# Camera zoom

- While aiming the camera, wheel / D-pad change a 35 mm-equivalent focal length (24-200, default 35,
  x1.12 per notch, kept in `ItemController._focalMm`) instead of cycling the hotbar. Wheel up = in,
  wheel down = out; the pad's only bound key (Next, D-pad right) zooms in and wraps to 24.
- `FovFromFocal(f) = 2 atan(12/f)` (vertical FOV; 35 mm = 38 deg). `LookScale = fov/76` (0.5 at 35 mm).
  `FootPlayer` already eases the FOV toward `FovOverride`.
- `InventoryUi.PhotoFocalMm` feeds `ViewfinderView`: focal + "xN" readout, log zoom scale with a
  marker, autofocus brace that hunts 0.35 s after every change then turns green, shots counter
  (files in `user://photos`), clock, battery. Redraws every frame while visible.
- Screenshot: `--ride foot,6,out.png --hold camera --view first --aim --zoom 135`.
