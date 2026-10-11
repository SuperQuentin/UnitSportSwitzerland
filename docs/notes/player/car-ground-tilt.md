# A car stands on the ground under its wheels (#764)

- Until #764 a driven car's body stayed level on every slope (only its acceleration pitch, inside
  `CarRig`, moved it): nose in the hill on a climb, tail in the air going down, never leaning
  across a camber. Trucks and buses already pitched to their axles (`FootPlayer.Heavy.cs` `PitchCab`).
- `FootPlayer.TiltCar` (owner, each physics step after `MoveAndSlide`, only on the floor): the
  ground under the four wheels (`GroundUnder`, ±wheelbase/2, ±0.42 × body width) gives a pitch
  (clamped ±0.45 rad) and a roll (±0.35 rad), eased at rate 12. Held while airborne, so a jump
  keeps the attitude it left the ramp with.
- `PoseRideVisual` turns the whole visual by it about the contact patch (the node's origin), on
  top of the rider pivot; `BodyPose` carries it to remote peers with nothing new on the wire, and
  `AlignHull` tilts the hull boxes with it. The cockpit camera follows (`EyeFrame`).
- The character body itself stays upright, as with trucks.
- Check: `--ride car,10[,shot.png] [--sideview] --chunks fixture:hairpin --traffic 0` prints
  `pitch` / `roll` each second (the hairpin's descent: pitch about −11° against an 11% grade);
  `--sideview` frames the last second from 9 m abeam.
