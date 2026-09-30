# Mantle

- **Mantle** (on foot): pushing into a wall whose top is 0.45–2.1 m above the feet, with open air
  over it and standing room on it, pulls you up (automatic in the air, needs Jump on the ground so
  walking into garden walls does not vault them). Jump + mantle therefore reaches ~3 m. Moved
  directly, not through MoveAndSlide, which exists to stop exactly this contact. Ground coyote
  time 0.12 s.
- **Ceilings (#151).** The ledge rays start 2.35 m over the feet: under a low ceiling (a car in a
  garage, any wall up to a room's ceiling, mid-jump) that is already above it, the down ray finds
  the ceiling's top and there is room up there, so the pull-up put you on the roof, or in the void
  over an interior. Being moved directly, nothing stopped it. So `ClearSweep` also sweeps the
  standing body (radius shaved 3 cm) up the face and over the lip; each sweep's start is tested
  on its own, because a Jolt cast ignores a shape it starts inside (starting 0.3 m up, the head
  was already in the ceiling and the cast saw nothing).
- Check: `<godot> --path . -- --mantlecheck` (1.4 m and 2.8 m must climb, 3.6 m not; under a
  one-face ceiling like an interior's, a 1.4 m block under 2.6 m and a wall up to a 2.5 m ceiling
  must not, a 1.4 m block under 3.5 m must; jumping again on top must not pass the ceiling).
