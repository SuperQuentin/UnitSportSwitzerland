# A body's air (#1853)

Every body's air is data: `Core/BodyAir` on `CelestialBody.Air` (null for the airless: the Moon). Plan:
`docs/plans/planets.md` sections 6 and 7.

- **What it holds**: the shell's single scattering in two parts, as `shaders/atmosphere.gdshader` draws it: a fine
  part (Rayleigh's phase: the Earth's molecules, Mars' fine dust) and a coarse one (forward-peaked: the Earth's
  aerosols, Mars' dust round the sun). Each has a colour (scattering per channel at the ground, per metre) and a scale
  height. Also the fine part's absorption, the coarse part's g and extinction, the shell's top, the sky's space band
  (the stars by day above it), the sun's power in the sum, and the gas's density for drag.
- **The Earth's is the shader's old constants, exactly**: they are the uniforms' defaults, and `BodyAirTests` reads
  them back from the shader. Drag on the Earth stays the 1976 standard atmosphere (`CelestialBody.AirDensity`).
- **Mars'**: CO2 at 0.020 kg/m3 and 11.1 km scale height (NASA's fact sheet). Dust well mixed to the same height:
  - the fine part scatters red over blue and absorbs blue: the butterscotch day, a redder horizon;
  - the coarse part is forward (g 0.85) and bluer: the blue round the setting sun.
  - Vertical optical depth ~0.33 in red. Colours tuned by eye against the rovers' sky; sun power 7.
- **Venus'** (#1854): CO2 at 65 kg/m3 and 15.9 km scale height; the opaque deck cannot be drawn by single scattering,
  so a thick bright yellow haze that still lets the radar-mapped ground read from orbit: a dim amber sky from the
  ground, as Venera's pictures. Sun power 9.
- **`World/Globe/Atmosphere`**: the shell round the anchor's body when it has air.
  - The Earth's fades in over the style's sky from 15 km, as before.
  - Another body's paints its whole sky from the ground up (the style's sky is the Earth's: dimmed to black there).
  - Its sun comes from that body's own offset to the Sun (`Frames.Offset` turned by `Frames.Orientation`, as
    `BodyGlobe` does). The Earth's direction turned into Mars' axes was tens of degrees off.
- **Drag**: `SpaceOrbit` reads `CelestialBody.AirDensity` (Mars' at 100 km: a few 1e-6 kg/m3).
- **Pictures**: `--bodyshot mars,sun,-20,10,0,0` (noon from 10 km), `mars,sun,-84,10,-3,90` (toward the low sun),
  `mars,sun,-40,300,40,90` (the limb from orbit); `--mooncheck mars --fromorbit DEG` stands on the ground with the
  sun 90 − DEG up.
- **Not yet**: the Earth's limb seen from the Moon (the shell draws round the anchor's body only); a dust storm
  (raise the fine and coarse parts); Mars' sound and its thin air on a parachute or a wing.
