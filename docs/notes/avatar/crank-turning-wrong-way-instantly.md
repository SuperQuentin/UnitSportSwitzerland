# A crank turning the wrong way is instantly obvious to anyone who rides

- **A crank turning the wrong way is instantly obvious to anyone who rides.** The bike faces +Z,
  so driving forward turns the chainring with its top moving toward +Z — meaning a crank starting
  at the front goes *down* next. Taking the obvious `(sin, cos)` circle runs it backwards. The
  same sign appears in `BikeMeshBuilder.Cranks` and `Cyclist.UpdateLegs`; they can only disagree
  if one is edited alone. Check it with `--avatars … --crank <rad>`, which parks the cranks —
  rotation direction cannot be judged from one frame.
