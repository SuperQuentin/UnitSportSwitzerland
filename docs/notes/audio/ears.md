# Ears on the body, not the camera (#375)

## Rule
- The listener is `Audio/Ears` (an `AudioListener3D`, made current, created by `ClientWorld` with
  the audio system): at the local body's head, `FootPlayer.EarFrame`, facing where the body faces,
  facing eased ~0.12 s. First person and VR: the camera IS the head, its frame is used (head turns,
  cockpit look included). Third person, chase, crash, free look: the eye height above the body,
  turned with the drawn figure. A passenger hears from its host's body. No body (title, spectating,
  probes without a local player): the camera, as Godot does on its own.
- Every audio system asks `Ears` for the ear, never the viewport camera: `Ears.Of(node)` (position,
  camera fallback), `Ears.FrameOf(node)`, or the `EarNode` that `ClientWorld` hands to
  `ReverbZones`, `Ambience` and `OccasionAmbience`. `WebRadio.Listener`, `InteriorManager`'s
  building sounds and door sounds, `Hearing` all go through it.
- `Ears.Cabin` = `FootPlayer.CabinOwner`: the body whose closed cabin the ears are in (oneself at the
  wheel of a car, truck, bus, helicopter or plane; the driver when riding along).
  `FootPlayer.ClosedCabin(kind)`. `Ears.Shut` eases 0..1 over 0.3 s and drives the world bus's
  cabin filter (`SfxBus.SetCabin`, called by `ReverbZones._Process`).
- The listener tracks Doppler (idle step); `Ears` is `IOriginShiftAware` and restarts the tracker
  on a shift, or every shift would be a pitch spike.

## Why
Everything was heard from the camera: a third-person or chase camera 5-8 m behind the body moved
the panning, the distance, the reverb rays and the radio occlusion ray. A car's own stereo was
heard from behind the car, through its body ("wall"); a church radio was muffled by whatever
pillar stood between the lens and the radio. The user asked for the sound to reach the body's
ears, in stereo, whatever the camera does.

## Pitfall, accepted
With the camera facing the body, screen-left sounds come from the right: that is what the body
hears. The eased facing keeps a figure snapping round from flipping the panning.

## How to check
In game: third person, walk past a radio and orbit the camera: the sound must not move. Drive with
the radio on, chase cam: the stereo is in the cabin, centred. `--interactcheck` (radio behind a
car: "wall", walked round: "open") runs from the body's ears.
