# The local day: a short day on every body (#1992)

Plan: `docs/plans/planets.md` section 2 ("The local day"). The user, Nov 15: at normal speed every body gets its own short
day; "the earth shouldn't move, but everything else should".

- **What turns**: the body under this peer (`SkyClock.Local`, set each frame by `World/Bodies/BodyViews` from the anchor's
  body and the camera's distance to its centre) takes `SkyClock.ExtraTurn` (the game's Earth angle less the true sidereal
  angle at the bodies' time) times `SkyClock.LocalWeight`, carried in `SkyMoment.Local` / `LocalTurn` by every moment
  `SkyClock.Moment` builds (`WorldClock.Sky`, `DayNight`'s). `Frames.Orientation` turns that body's axes about its pole in
  its own spin's sense (Venus, Uranus backward); `Frames.FromParent(body, SkyMoment)` turns a moon's orbit about its pole
  too, so its parent stays put in its sky. The Earth and the Sun never take it.
- **Everything the sky draws follows**: the sun, the stars' frame and the planets come through `Frames` (`SunPath`'s
  observer converted to the Earth and back, `BodyGlobe`'s and the atmosphere's sun from `Frames.Offset(.., SkyMoment)`).
  `SunPath.PlaceOf` keeps an off-Earth point's place for one sky moment only (it moves with the tree).
- **Not a place**: positions on a body are body-fixed (the same numbers whatever the turn), so peers agree; the server
  takes none. `Frames.Offset(.., DateTime)` and `Frames.Velocity` stay the true tree (handovers subtract true velocities).
- **The fade**: full within 15 % of the sphere of influence, none from 60 % (the Moon: 9,900 and 39,500 km), so a crossing
  happens on the true tree. While climbing through the band the distant sky swings back to its true place; the extra turn
  is in (−π, π], so inside the band a wrap of it shows as one jump of up to 2π·(1 − weight).
- **Rates**: at the 24-minute day the Moon's sun crosses in 24.4 minutes, Mars' in 24.0; at `/time speed real` the extra
  turn stands still, each body turns at its true rate, offset as the Earth's game hour is off the real one.
- **The Earth from the Moon**: still in the sky, its phase now cycling each game day with the Moon's sun; its own spin seen
  from there slow (the Moon turns with the game Earth, about one relative turn a real day).
- **Check**: `--frame earth --chunks fixture:straight --bodies E:/UnitSportSwitzerland/planets --sun real --date 2026-11-15
  --time 22 --occasion none --bodyshot moon,17,59,3,-60,95,later:2` (windowed): two pictures two game hours apart; the log
  gives the subsolar point (104.06° then 74.02° E: 30°) and the Earth's (2.22, −3.81 both). Unit: `LocalDayTests`.
