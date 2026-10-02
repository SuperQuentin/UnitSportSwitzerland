# Music bus and hearing through walls and doorways (#261)

- **Music bus** (`SfxBus.Music`, made by `SfxBus.Ensure` beside `Sfx`, sends to Master): radios
  (`RadioSpeaker`), car CDs and live stations (`WebRadioSpeaker`). Volume = Settings -> Audio ->
  Music (`GameSettings.MusicVolume`, default 0.7, same square-law slider as the others); the radio
  panel's slider is the same value (`RadioSpeaker.UserVolume`, saved with the settings; the old
  `user://radio.cfg` is no longer read). Its own reverb, eased by `ReverbZones` with the Sfx one,
  wet x0.75.
- **`Audio/Hearing`** (one per speaker; the speaker goes `TopLevel` and is placed every frame from
  its parent + its original offset): listener = the viewport camera.
  - same space: a ray ear -> source at 5 Hz, bodies (`CharacterBody3D`) skipped, a hit within 0.7 m of
    the source ignored; blocked = -9 dB, 900 Hz cutoff ("wall").
  - other space (interiors are 3 km down): through the nearest linked doorway of the interior holding
    the inside point (`InteriorManager.Links`, `DoorLink.ToInside/ToOutside`), level and cutoff by the
    leaf's `Swing` (-15 dB / 420 Hz shut .. -3 dB / 4.5 kHz open, "door"); no link (a shut building
    is not linked) = the same spot on the other side (`SurfacePoint`, or the inverse with
    `Hearing.Ground`), -15 dB, 420 Hz ("walls").
  - eased over 0.15 s, the cutoff in log space. `RadioSpeaker.HeardThrough` exposes the path.
