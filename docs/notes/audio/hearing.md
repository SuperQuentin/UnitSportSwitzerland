# Music bus and hearing through walls and doorways (#261)

- **Music bus** (`SfxBus.Music`, made by `SfxBus.Ensure` beside `Sfx`, sends to Master): radios
  (`RadioSpeaker`), car CDs and live stations (`WebRadioSpeaker`). Volume = Settings -> Audio ->
  Music (`GameSettings.MusicVolume`, default 0.7, same square-law slider as the others); the radio
  panel's slider is the same value (`RadioSpeaker.UserVolume`, saved with the settings; the old
  `user://radio.cfg` is no longer read). Its own reverb, eased by `ReverbZones` with the Sfx one,
  wet x0.75.
- **`Audio/Hearing`** (one per speaker; the speaker goes `TopLevel` and is placed every frame from
  its parent + its original offset): listener = the body's ears (`Ears`, `docs/notes/audio/ears.md`, #375).
  - "cabin": the speaker hangs on `Ears.Cabin` (the stereo of the car the ears sit in, driver or
    passenger): played from the dash (ears + (0, -0.3, -0.75)), clear, panning 0.35.
  - "own": a radio on the local body (held, on the back): where it hangs, clear, panning 0.45.
  - same space: 5 rays at 5 Hz, ears -> the source and 4 points 0.4 m around it; the share blocked
    scales the loss (one pillar 1-2 rays, a wall all 5). Hits within 0.35 m of the target (its table,
    the ground under it) or 0.3 m of the ears, players, and the source's own collider do not count.
    Outdoors fully blocked = -10 dB / 700 Hz ("wall"); both inside one interior = at most -4 dB /
    2.4 kHz ("room": the room's reflections fill in; this is what made a church radio muffled
    from half the church).
  - "shell": a stereo in a closed car the ears are not in (a driver's body, a parked `VehicleBody`):
    -10 dB, <= 900 Hz, the thump through the glass. Ears in a closed car: every other radio another
    -8 dB and <= 1.4 kHz (Music is not behind the bus cabin filter).
  - other space (interiors are 3 km down): through the nearest linked doorway of the interior holding
    the inside point (`InteriorManager.Links`, `DoorLink.ToInside/ToOutside`), level and cutoff by the
    leaf's `Swing` (-15 dB / 420 Hz shut .. -3 dB / 4.5 kHz open, "door"); no link (a shut building
    is not linked) = the same spot on the other side (`SurfacePoint`, or the inverse with
    `Hearing.Ground`), -15 dB, 420 Hz ("walls").
  - eased over 0.15 s, the cutoff in log space. `RadioSpeaker.HeardThrough` exposes the path,
    `Hearing.Blocked` the share of rays.
