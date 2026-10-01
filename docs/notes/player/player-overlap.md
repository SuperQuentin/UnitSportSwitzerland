# Players set down inside each other

Two bodies on one spot (a shared spawn, `--at`, `/tpall`, a teleport onto someone) used to be
shoved apart by the physics: the owner's `MoveAndSlide` depenetrated out of the remote copy, the
synchronizer put the copy straight back, and the push added up — one player was thrown 3.8 km
sideways and 900 m up in a PvP loopback test (#203).

`FootPlayer.Overlap.cs`: every physics frame the owner tests its shapes against other players
(skipped when nobody is within 30 m). A player whose capsule centre is within half the summed radii
gets a collision exception and this body is moved 1.5 m/s away from it (coincident pairs pick
opposite directions by name); once the shapes no longer intersect the exception is removed and
they are solid again. Walking into someone never starts it, only being set down on top of them.

Check: dedicated server + two clients with the same `--at`; the server's `[server] player ... at`
lines end ~0.65-1.35 m apart and stay there, clients log `[spawn] ... easing apart`.
